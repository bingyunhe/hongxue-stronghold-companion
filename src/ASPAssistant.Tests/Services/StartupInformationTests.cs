using System.Drawing;
using ASPAssistant.Core.Interop;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.GameModes.GarrisonProtocol;
using ASPAssistant.Core.Services;

namespace ASPAssistant.Tests.Services;

[Collection("Native OCR")]
public class StartupInformationTests
{
    [Fact]
    public async Task ReplayBlueLensRecordAndClose()
    {
        if (Environment.GetEnvironmentVariable("ASPASSISTANT_STARTUP_BOARD") is not { } path) return;
        var board = File.ReadAllBytes(path);
        var modal = File.ReadAllBytes(Environment.GetEnvironmentVariable("ASPASSISTANT_STARTUP_MODAL")!);
        var lens = OcrScannerService.InformationControl(board, false)!.Value.Point;
        var cross = OcrScannerService.InformationControl(modal, true)!.Value.Point;
        Assert.InRange(lens.X, 369, 387);
        Assert.InRange(cross.X, 1338, 1358);
        Assert.Null(OcrScannerService.InformationControl(board, true));
        Assert.Null(OcrScannerService.InformationControl(modal, false));
        using (var source = new Bitmap(path))
        using (var resized = new Bitmap(source, 1280, 720))
            Assert.NotNull(OcrScannerService.InformationControl(ImageBytes.Encode(resized), false));
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        using var ocr = new MaaOcrEngine(data);
        var store = new JsonDataStore(data);
        var (covenants, _) = await store.LoadCovenantsAsync();
        var strategy = new GarrisonOcrStrategy();
        var boss = new BossDataService();
        await boss.LoadAsync(GarrisonRoundRoutesTests.DataDirectory);
        var live = board;
        var analyzer = new BanScreenAnalyzer(() => Task.FromResult<byte[]?>(live), ocr, strategy,
            new EnemyRecognizer(ocr, strategy, boss), await store.LoadOperatorsAsync(),
            covenants.Where(c => c.BondType == "SEASON").Select(c => c.Name).ToList(),
            covenants.Where(c => c.BondType != "SEASON").Select(c => c.Name).ToList());
        var timer = new System.Diagnostics.Stopwatch();
        long recognizedMs = 0, openedMs = 0, closedMs = 0;
        analyzer.AnalysisCompleted += result =>
        {
            Assert.Equal("矩兽", result.EnemyFaction);
            recognizedMs = timer.ElapsedMilliseconds;
        };
        var economy = new ShopEconomyReader(ocr);
        int gameMessages = 0;
        analyzer.ShowGameOverlayMessageRequested += _ => gameMessages++;

        List<Point> clicks = [];
        using var connection = new GameConnectionService();
        using var scanner = new OcrScannerService(() => live, strategy, new ShopReader(ocr, []), ocr, new(), [],
            getConnection: () => connection)
        {
            StartupInformationEnabled = true, EconomyReader = economy,
            BoardRecognizer = new BoardRecognizer((await GarrisonBoardStore.LoadAsync(GarrisonRoundRoutesTests.DataDirectory))!),
            RecordStartupInformationAsync = () => analyzer.RunAnalysisAsync(showGameResult: false),
            SendCapturedClick = (_, point, _, _) =>
            {
                if (point == lens) openedMs = timer.ElapsedMilliseconds;
                else closedMs = timer.ElapsedMilliseconds;
                clicks.Add(point);
                live = point == lens ? modal : board;
                return ClickResult.Sent;
            }
        };
        Task<bool> Observe(byte[] frame) => scanner.TryStartupInformationAsync(frame,
            connection, scanner.RunRevision, connection.CaptureRevision);
        Assert.False(await Observe(modal));

        Assert.Null(scanner.CurrentEconomy);
        timer.Start();
        await scanner.ScanNowAsync();
        timer.Stop();
        Assert.Equal(0, gameMessages);
        Assert.Equal([lens, cross], clicks);
        Assert.False(await Observe(board));
        var success = Assert.Single(RecognitionDiagnostics.Samples.Freeze(), e => e.Kind == "中途启动录入" && e.Result == "已返回棋盘");
        File.WriteAllText(Path.Combine(TestOutput.Directory, "startup-first-scan-ms.txt"), $"首次扫描至打开 {openedMs} ms，录入完成至关闭 {closedMs - recognizedMs} ms，完整流程 {timer.ElapsedMilliseconds} ms");
        File.WriteAllLines(Path.Combine(TestOutput.Directory, "startup-information-replay.txt"), success.Actions);
        foreach (var scenario in new[] { "disabled", "already-recorded", "cancelled", "failed" })
        {
            live = board;
            int inputs = 0;
            using var other = new OcrScannerService(() => live, strategy, new ShopReader(ocr, []), ocr, new(), [],
                getConnection: () => connection)
            {
                StartupInformationEnabled = scenario != "disabled", EconomyReader = economy,
                RecordStartupInformationAsync = () => Task.FromResult(false),
                SendCapturedClick = (_, point, _, _) => { inputs++; live = modal; return ClickResult.Sent; }
            };
            if (scenario == "already-recorded") other.MarkInformationRecorded();
            if (scenario == "cancelled") other.CancelStartupInformation();
            await other.TryStartupInformationAsync(board, connection, other.RunRevision, connection.CaptureRevision);
            Assert.Equal(scenario == "failed" ? 1 : 0, inputs);
        }
    }
}
