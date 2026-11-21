using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using ASPAssistant.Core;
using ASPAssistant.Core.GameModes.GarrisonProtocol;
using ASPAssistant.Core.Services;
using FluentAssertions;
using Moq;

namespace ASPAssistant.Tests.Services;

[Collection("Application logging")]
public sealed class FeedbackReportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "feedback-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _logPath = typeof(AppLogger).GetField("_logFilePath", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly FieldInfo _presence = typeof(AppLogger).GetField("_isGamePresent", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly FieldInfo _snapshot = typeof(RecognitionDiagnostics).GetField("_latest", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previousLog;
    private readonly object? _previousPresence;
    private readonly object? _previousSnapshot;
    public FeedbackReportTests()
    {
        _previousLog = _logPath.GetValue(null); _previousPresence = _presence.GetValue(null); _previousSnapshot = _snapshot.GetValue(null);
        _snapshot.SetValue(null, null);
        AppLogger.Initialize(_directory);
    }

    [Fact]
    public void BusinessFailuresAreRecordedWithoutGameAndRecognitionErrorsAreRateLimited()
    {
        AppLogger.SetGamePresenceCheck(static () => false);
        AppLogger.Warn("Lineup", "Import failed: local proxy refused 127.0.0.1:7890");
        AppLogger.Warn("Update", "Download failed");
        for (var i = 0; i < 100; i++) AppLogger.RecognitionFailure("FeedbackTest", "same-job", "job failed");
        var text = File.ReadAllText(AppLogger.LogFilePath!);
        text.Should().Contain("Import failed").And.Contain("Download failed");
        text.Split("[FeedbackTest]").Should().HaveCount(2);
    }

    [Theory]
    [InlineData("C:\\Users\\tester\\AppData\\file.json", "tester")]
    [InlineData("D:/private/source.cs:line 99", "private")]
    [InlineData("https://example.com/api?token=sample-sensitive&user=abc", "sample-sensitive")]
    [InlineData("Authorization: Bearer sample-sensitive", "sample-sensitive")]
    [InlineData("Cookie: session=sample-sensitive", "sample-sensitive")]
    [InlineData("api_key=sample-sensitive", "sample-sensitive")]
    [InlineData("https://user:sample-sensitive@proxy.example:7890", "sample-sensitive")]
    public void PreviewRemovesLocalPathsCredentialsAndUrlParameters(string input, string hidden)
    {
        FeedbackReport.Sanitize(input).Should().NotContain(hidden);
        FeedbackReport.Sanitize("无法连接 127.0.0.1:7890").Should().Contain("127.0.0.1:7890");
        FeedbackReport.Sanitize("https://example.com/api?user=abc").Should().StartWith("https://example.com/api?");
    }

    [Fact]
    public void ExportContainsOnlyReviewedTextAndAnExplicitlySelectedFrozenScreenshot()
    {
        var now = DateTimeOffset.Now;
        var frame = new byte[] { 1, 2, 3 };
        RecognitionDiagnostics.Record(now, "商店识别", frame, new { Name = "风絮", Region = new { X = .1, Y = .8 } });
        var report = FeedbackReport.Prepare();
        RecognitionDiagnostics.Record(now.AddMilliseconds(1), "商店识别", [4, 5], new { Name = "仇白" });
        report.Recognition!.Frame.Should().BeSameAs(frame);
        var path = Path.Combine(_directory, "report.zip");
        report.Export(path, "识别有误", "风絮未高亮", "打开商店", false);
        using (var zip = ZipFile.OpenRead(path))
        {
            zip.Entries.Select(x => x.FullName).Should().Equal("反馈说明.txt");
            using var reader = new StreamReader(zip.Entries[0].Open());
            reader.ReadToEnd().Should().Be(report.Preview("识别有误", "风絮未高亮", "打开商店"));
        }
        var withImage = Path.Combine(_directory, "with-image.zip");
        report.Export(withImage, "识别有误", "风絮未高亮", "打开商店", true);
        using var included = ZipFile.OpenRead(withImage);
        included.Entries.Should().HaveCount(2);
        using var image = included.GetEntry("识别画面.png")!.Open();
        using var bytes = new MemoryStream(); image.CopyTo(bytes);
        bytes.ToArray().Should().Equal(frame);
        Action overwrite = () => report.Export(path, "程序报错", "描述", "步骤", false);
        overwrite.Should().Throw<IOException>();
    }

    [Fact]
    public void BothFeedbackArchivesKeepTheConnectionCauseAfterLogTruncation()
    {
        var missing = Path.Combine(_directory, "missing-adb.exe");
        var error = Assert.Throws<IOException>(() => AdbGameScreens.Run(missing, ["devices"]));
        FeedbackReport.RecordConnectionFailure("查找屏幕失败", error.Message);
        File.ReadAllText(AppLogger.LogFilePath!).Should().Contain("Windows 错误 2").And.NotContain(missing);
        File.AppendAllText(AppLogger.LogFilePath!, new string('x', 3 * 1024 * 1024) + "\n");
        var history = new RecognitionSampleHistory();
        history.Begin("识别", 1);
        var report = FeedbackReport.Prepare();
        foreach (var sample in new RecognitionSample?[] { null, history.Freeze().Single() })
        {
            var path = Path.Combine(_directory, $"connection-{sample is null}.zip");
            (report with { Selected = sample }).Export(path, "程序报错", "", "", false);
            using var zip = ZipFile.OpenRead(path);
            using var reader = new StreamReader(zip.GetEntry("反馈说明.txt")!.Open());
            reader.ReadToEnd().Should().Contain("查找屏幕失败").And.Contain("Windows 错误 2").And.NotContain(missing);
        }
    }

    [Fact]
    public void GameMarksReopenTogetherWithTheOriginalFramesAndPlayerCorrection()
    {
        var game = Guid.NewGuid();
        var now = DateTimeOffset.Now;
        var unit = new LiveUnit(new(new(4,1),1,new(1,2),[],null),4,"烈夏",null,false,false);
        var copy = unit with { Id = 7, Mark = unit.Mark with { Slot = new(2,3) } };
        var before = new FieldObservation(game,new(now,"第三回合原图",[1]),"city",3,[unit],[]);
        var slot = new FieldSlot(4,0);
        var after = new FieldObservation(game,new(now.AddSeconds(1),"第四回合原图",[2]),"city",4,[unit,copy],["烈夏"])
        { Readings = new Dictionary<FieldSlot,SlotReading> { [slot] = new(new(20,30,40,50),"角标与已知棋盘外观均未匹配") } };
        var roster = new LiveRoster("city",4,[unit,copy]) { Observations = [before,after] };
        var sample = FieldFeedback.Mark(roster,slot,"战斗 · 候选：暴躁铁皮 · 替补：暴躁铁皮",null);
        FieldFeedback.Save(_directory,game,sample);
        FieldFeedback.Save(_directory,game,new(4,"干员复核 · C7","focus=B7",now.AddMinutes(1),
            [new(now,"点击前",[3]),new(now,"选中 B7",[4])]) { Actions = ["点击 (741,266) · 1280×720"] });
        FieldFeedback.Save(_directory,Guid.NewGuid(),sample with { Kind = "下一局" });
        var games = FieldFeedback.LoadGames(_directory);
        Assert.Equal(2,games.Length);
        var report = games.Single(s => s.Cases.Length == 2);
        report.Cases[0] = report.Cases[0] with { Expected = "A7 折桠" };
        var path = Path.Combine(_directory,"整局.zip");
        new FeedbackReport("测试","",null) { Selected = report }.Export(path,"识别有误","","",true);
        using var zip = ZipFile.OpenRead(path);
        using var text = new StreamReader(zip.GetEntry("反馈说明.txt")!.Open());
        text.ReadToEnd().Should().Contain("A7 折桠").And.Contain("新增 #7").And.Contain("替补：暴躁铁皮")
            .And.Contain("focus=B7").And.Contain("X=20").And.Contain("741,266");
        using var manifest = JsonDocument.Parse(zip.GetEntry("recognition.json")!.Open());
        int index = manifest.RootElement.GetProperty("cases")[0].GetProperty("InputFrame").GetInt32()-1;
        var file = manifest.RootElement.GetProperty("frames")[index].GetProperty("file").GetString()!;
        using var image = zip.GetEntry(file)!.Open();
        Assert.Equal(2,image.ReadByte());
    }

    [Fact]
    public void BatchExportKeepsFailedAndEditedRecordsPending()
    {
        var now = DateTimeOffset.Now;
        var sample = new RecognitionSample(6,"C5","待复核",now,[new(now,"棋盘",[1,2])]);
        FieldFeedback.Save(_directory,Guid.NewGuid(),sample);
        FieldFeedback.Save(_directory,Guid.NewGuid(),sample);
        var games = FieldFeedback.LoadGames(_directory);
        var records = games.SelectMany(g => g.GameRecords).ToArray();
        var feedback = new FeedbackReport("测试", $"[DeployOwnership] 对局 {games[0].Game:N} 卡达 0 → 1\n[DeployOwnership] 对局 {games[1].Game:N} 维伊 1 → 1", null);
        var path = Path.Combine(_directory,"两局.zip");
        File.WriteAllText(path,"已有文件");
        Assert.Throws<IOException>(() => feedback.Export(path,"识别有误","","",true,records));
        Assert.All(FieldFeedback.LoadGames(_directory).SelectMany(g => g.GameRecords),r => Assert.True(r.Pending));
        path = Path.Combine(_directory,"成功.zip");
        feedback.Export(path,"识别有误","","",true,records);
        using var archive = ZipFile.OpenRead(path);
        Assert.Equal(2,archive.Entries.Count(e => e.Name == "recognition.json"));
        Assert.Equal(2,archive.Entries.Count(e => e.Name == "01.png"));
        using var history = new StreamReader(archive.GetEntry($"对局-{games[0].Game:N}/持有记录.txt")!.Open());
        history.ReadToEnd().Should().Contain("卡达 0 → 1").And.NotContain("维伊");
        Assert.NotNull(archive.GetEntry($"对局-{games[1].Game:N}/持有记录.txt"));
        Assert.All(FieldFeedback.LoadGames(_directory).SelectMany(g => g.GameRecords),r => Assert.False(r.Pending));
        FieldFeedback.SetNote(games[0],0,"卡达");
        var edited = Assert.Single(FieldFeedback.LoadGames(_directory).SelectMany(g => g.GameRecords).Where(r => r.Pending));
        Assert.Equal("卡达",edited.Note);
        var corrected = Path.Combine(_directory,"补充.zip");
        feedback.Export(corrected,"识别有误","","",false,[edited]);
        using var second = ZipFile.OpenRead(corrected);
        Assert.DoesNotContain(second.Entries,e => e.Name.EndsWith(".png"));
        using var json = JsonDocument.Parse(second.Entries.Single(e => e.Name == "recognition.json").Open());
        Assert.Equal("卡达",json.RootElement.GetProperty("expected").GetString());
    }

    [Fact]
    public void PreparationBoundsLargeLogsAndKeepsTheLatestFailure()
    {
        File.WriteAllText(AppLogger.LogFilePath!, new string('x', 3 * 1024 * 1024) + "\n[ERROR] latest failure\n");
        var report = FeedbackReport.Prepare();
        report.Logs.Should().Contain("仅包含末尾").And.Contain("latest failure");
        report.Logs.Length.Should().BeLessThan(2 * 1024 * 1024 + 100);
    }

    [Fact]
    public void MissingLogStillAllowsFeedbackWithoutExposingItsLocalPath()
    {
        File.Delete(AppLogger.LogFilePath!);
        var report = FeedbackReport.Prepare();
        report.Logs.Should().Contain("无法读取日志").And.NotContain(_directory);
        report.Export(Path.Combine(_directory, "without-log.zip"), "程序报错", "无法启动", "", false);
        File.Exists(Path.Combine(_directory, "without-log.zip")).Should().BeTrue();
    }

    [Fact]
    public void LateRecognitionCannotReplaceNewerFrameAndOversizeImagesAreNotRetained()
    {
        var now = DateTimeOffset.Now;
        RecognitionDiagnostics.Record(now, "new", [2], new { Status = "new" });
        RecognitionDiagnostics.Record(now.AddSeconds(-1), "late", [1], new { Status = "old" });
        RecognitionDiagnostics.Latest!.Source.Should().Be("new");
        RecognitionDiagnostics.Record(now.AddSeconds(1), "large", new byte[RecognitionDiagnostics.MaximumFrameBytes + 1], new { Status = "too large" });
        RecognitionDiagnostics.Latest!.Frame.Should().BeEmpty();
        RecognitionDiagnostics.Latest.Result.Should().Contain("too large");
    }

    [Fact]
    public async Task ScannerPairsOneCapturedFrameWithResultsWithoutAdditionalCapturesOrOcr()
    {
        using var bitmap = new Bitmap(960, 540);
        using var image = new MemoryStream(); bitmap.Save(image, ImageFormat.Png);
        var frame = image.ToArray();
        var captures = 0;
        var engine = new Mock<IOcrEngine>();
        engine.Setup(x => x.RecognizeRegionAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(Array.Empty<OcrTextResult>());
        engine.Setup(x => x.RecognizeLineAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(Array.Empty<OcrTextResult>());
        using var scanner = new OcrScannerService(() => { captures++; return frame; }, new GarrisonOcrStrategy(),
            new ShopReader(engine.Object, []), engine.Object, new(), []);
        await scanner.ScanNowAsync();
        captures.Should().Be(1);
        RecognitionDiagnostics.Latest!.Frame.Should().BeSameAs(frame);
        RecognitionDiagnostics.Latest.Result.Should().Contain("未检测到展开的商店");
        var calls = engine.Invocations.Count;
        var report = FeedbackReport.Prepare();
        report.Export(Path.Combine(_directory, "scanner.zip"), "识别有误", "漏识别", "", true);
        captures.Should().Be(1);
        engine.Invocations.Count.Should().Be(calls, "preparation and export reuse the frame and never run OCR");
    }

    [Fact]
    public void FrozenEventExportsOrderedPreviewedFramesAndOmitsImagesWhenUnchecked()
    {
        var history = new RecognitionSampleHistory();
        var id = history.Begin("干员复核 · D8", 8);
        history.Step(id, "点击前", [0x42, 0x4d, 1]);
        history.Step(id, "目标 D8 · 点击 900,390");
        history.Step(id, "选中 D9", [0x89, 0x50, 2]);
        var selected = Assert.Single(history.Freeze());
        history.Step(id, "后续扫描");
        var report = new FeedbackReport("本机版本", "[LiveField] selected=D9\n[Private] unrelated", null) { Selected = selected };
        var preview = report.Preview("识别有误","", "");
        Assert.Contains("目标 D8", preview);
        Assert.Contains("选中 D9", preview);
        Assert.DoesNotContain("unrelated", preview);
        Assert.DoesNotContain("后续扫描", preview);
        foreach (var include in new[] { true, false })
        {
            var path = Path.Combine(_directory, $"event-{include}.zip");
            report.Export(path, "识别有误", "", "", include);
            using var zip = ZipFile.OpenRead(path);
            Assert.Equal(include ? 4 : 1, zip.Entries.Count);
            using var reader = new StreamReader(zip.GetEntry("反馈说明.txt")!.Open());
            Assert.Equal(preview, reader.ReadToEnd());
            for (int i = 0; include && i < selected.Frames.Length; i++)
            {
                using var manifest = JsonDocument.Parse(zip.GetEntry("recognition.json")!.Open());
                var pathInZip = manifest.RootElement.GetProperty("frames")[i].GetProperty("file").GetString()!;
                using var encoded = zip.GetEntry(pathInZip)!.Open();
                using var bytes = new MemoryStream();
                encoded.CopyTo(bytes);
                Assert.Equal(selected.Frames[i].Image, bytes.ToArray());
                Assert.EndsWith(selected.Frames[i].Extension, pathInZip);
            }
        }
    }

    [Fact]
    public void SampleBuffersKeepRecentFramesAndEnforceTheirByteLimits()
    {
        var history = new RecognitionSampleHistory();
        var frame = new byte[12 * 1024 * 1024];
        for (int i = 0; i < 18; i++)
        {
            history.Observe(frame, 4, DateTimeOffset.Now);
            var id = history.Begin($"事件{i}", 4, true);
            history.Step(id, "核对", frame);
        }
        var groups = history.Freeze();
        Assert.Equal("事件17", groups[0].Kind);
        Assert.InRange(groups[..^1].Sum(s => s.Frames.Sum(f => f.Image.Length)), 1, RecognitionSampleHistory.EventBytes);
        Assert.InRange(groups[^1].Frames.Sum(f => f.Image.Length), 1, RecognitionSampleHistory.RecentBytes);
        history.ClearRecent();
        Assert.DoesNotContain(history.Freeze(), s => s.Kind == "最近游戏画面");
    }

    [Fact]
    public void MeasureSnapshotRetentionCost()
    {
        var frame = new byte[1024 * 1024];
        var result = new { Status = "完成", Items = new[] { new { Name = "风絮", X = .1, Y = .8, Width = .1, Height = .03 } }, RawText = new string('识', 500) };
        var now = DateTimeOffset.Now;
        RecognitionDiagnostics.Record(now, "benchmark", frame, result);
        const int count = 2000;
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (var i = 1; i <= count; i++) RecognitionDiagnostics.Record(now.AddTicks(i), "benchmark", frame, result);
        watch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        RecognitionDiagnostics.Latest!.Frame.Should().BeSameAs(frame);
        var evidence = new { Iterations = count, AddedMillisecondsPerFrame = watch.Elapsed.TotalMilliseconds / count,
            AllocatedBytesPerFrame = allocated / count, RetainedFrameBytes = frame.Length, MaximumRetainedFrameBytes = RecognitionDiagnostics.MaximumFrameBytes,
            AdditionalCaptureCalls = 0, AdditionalOcrCalls = 0 };
        Directory.CreateDirectory(TestOutput.Directory);
        File.WriteAllText(Path.Combine(TestOutput.Directory, "feedback-performance.json"), JsonSerializer.Serialize(evidence));
    }

    public void Dispose()
    {
        _logPath.SetValue(null, _previousLog); _presence.SetValue(null, _previousPresence); _snapshot.SetValue(null, _previousSnapshot);
        Directory.Delete(_directory, true);
    }
}
