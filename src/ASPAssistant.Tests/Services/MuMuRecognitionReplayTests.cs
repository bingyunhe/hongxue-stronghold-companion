using System.Diagnostics;
using System.Text.Json;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.GameModes.GarrisonProtocol;
using ASPAssistant.Core.Services;

namespace ASPAssistant.Tests.Services;

[Collection("Native OCR")]
public class MuMuRecognitionReplayTests
{
    [Theory, Trait("Category","LocalScreenshot")]
    [InlineData(0)]
    [InlineData(100)]
    public async Task SeventhRoundVideoTracksThreePurchasesAcrossTwoRefreshes(int startOffsetMs)
    {
        var evidence = Directory.CreateDirectory(Path.Combine(TestOutput.Directory, $"shop-r7-{startOffsetMs}")).FullName;
        ASPAssistant.Core.AppLogger.Initialize(evidence);
        var directory = Environment.GetEnvironmentVariable("ASPASSISTANT_SHOP_VIDEO_FRAMES")
            ?? Path.Combine(AppContext.BaseDirectory, "TestData", "shop-r7-video");
        var frames = Directory.GetFiles(directory, "*.png", SearchOption.AllDirectories)
            .OrderBy(file => Path.GetFileName(file), StringComparer.Ordinal)
            .ThenBy(file => Path.GetDirectoryName(file), StringComparer.Ordinal)
            .Select(File.ReadAllBytes).ToArray();
        Assert.Equal(165, frames.Length);
        using var fixture = new ShopReaderFixture();
        fixture.Reader.Theme = ASPAssistant.Core.Models.GameShopTheme.Closure;
        var names = fixture.Names.ToHashSet();
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        var maps = (await GarrisonBoardStore.LoadAsync(data))!;
        using var connection = new GameConnectionService(new() { Source = ASPAssistant.Core.Models.GameCaptureSource.Adb,
            Emulator = ASPAssistant.Core.Models.EmulatorKind.MuMu });
        var time = new Stopwatch();
        var events = new List<(string Name, double At, DateTimeOffset Offer)>();
        var trace = new System.Collections.Concurrent.ConcurrentQueue<string>();
        byte[] Capture(string kind, int delay)
        {
            var index = Math.Min((int)(time.ElapsedMilliseconds / 100), frames.Length - 1);
            trace.Enqueue($"{time.ElapsedMilliseconds} {kind} {index}");
            Thread.Sleep(delay);
            return frames[index];
        }
        using var scanner = new OcrScannerService(() => Capture("ADB", 510), new GarrisonOcrStrategy(),
            fixture.Reader, fixture.Engine, new(), fixture.Names, intervalMs: 500, getConnection: () => connection)
        {
            CaptureShopFrame = () => Capture("buffer", 35), PurchaseTargets = () => names, AutomaticPurchaseTracking = true,
            BoardRecognizer = new(maps), EconomyReader = new(fixture.Engine)
        };
        scanner.PurchasesDetected += (purchased, _, _) =>
        {
            lock (events)
                foreach (var name in purchased) events.Add((name, time.Elapsed.TotalSeconds, scanner.LastPurchase.OfferedAt));
        };
        await scanner.ScanNowAsync();
        var start = DateTimeOffset.UtcNow;
        time.Start();
        await Task.Delay(startOffsetMs);
        scanner.Start();
        await Task.Delay(TimeSpan.FromSeconds(20));
        scanner.Dispose();
        var gate = typeof(OcrScannerService).GetField("_scanning", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        while ((int)gate.GetValue(scanner)! != 0 && time.Elapsed.TotalSeconds < 30) await Task.Delay(10);
        Assert.Equal(0, (int)gate.GetValue(scanner)!);
        File.WriteAllLines(Path.Combine(evidence, "captures.txt"), trace);
        File.WriteAllLines(Path.Combine(evidence, "purchases.txt"),
            events.Select(e => $"{e.Name} offer={(e.Offer-start).TotalSeconds:F2} confirmed={e.At:F2}"));
        Assert.Equal(new[] { "泡泡", "风絮", "地灵" }, events.Select(e => e.Name));
        Assert.InRange((events[0].Offer - start).TotalSeconds, -3, 6.94);
        Assert.InRange((events[1].Offer - start).TotalSeconds, 6.93, 11.64);
        Assert.InRange((events[2].Offer - start).TotalSeconds, 11.63, 13.25);
        var payments = File.ReadLines(ASPAssistant.Core.AppLogger.LogFilePath!)
            .Where(line => line.Contains("[ShopPurchase] Confirmed [", StringComparison.Ordinal))
            .Select(line => line.Split("Confirmed ", 2)[1]).ToArray();
        Assert.Equal(new[] { "[泡泡], money=10->7", "[风絮], money=6->3", "[地灵], money=2->0" }, payments);
    }

    [Fact, Trait("Category","LocalScreenshot")]
    public async Task SeventhRoundVideoScrollingNameKeepsItsSlotUntilRefresh()
    {
        var directory = Environment.GetEnvironmentVariable("ASPASSISTANT_SHOP_VIDEO_FRAMES")
            ?? Path.Combine(AppContext.BaseDirectory, "TestData", "shop-r7-video");
        BmpPixels Frame(int index) => BmpPixels.TryRead(ImageBytes.ToBmp(File.ReadAllBytes(
            Path.Combine(directory, $"{index:D3}.png"))))!;
        using var fixture = new ShopReaderFixture();
        fixture.Reader.Theme = ASPAssistant.Core.Models.GameShopTheme.Closure;
        var named = Frame(49);
        var cards = await fixture.Reader.ReadAsync(named.EncodedFrame, named.Width, named.Height, fixture.Names);
        Assert.Equal(new[] { "随身身份牌", "夏栎", "风絮", "云迹", "Miss.Christine" }, cards.Select(c => c.Name));
        Assert.Equal(cards, fixture.Reader.CompareOpenShelf(named, named, cards));
        Assert.Equal(new[] { "随身身份牌", "夏栎", "云迹", "Miss.Christine" },
            fixture.Reader.CompareOpenShelf(named, Frame(52), cards)?.Select(c => c.Name));
        Assert.Null(fixture.Reader.CompareOpenShelf(named, Frame(60), cards));
    }

    [Fact, Trait("Category","LocalScreenshot")]
    public async Task PalaceFifthRoundIdentity()
    {
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        var maps = (await GarrisonBoardStore.LoadAsync(data))!;
        var operators = await new JsonDataStore(data).LoadOperatorsAsync();
        var tiers = operators.ToDictionary(o => o.Name, o => o.Tier);
        var state = new LiveFieldState(tiers);
        var reader = new LiveFieldReader(data, maps);
        var rows = new List<object>();
        foreach (var id in new[] { "0-MAA", "0-ADB", "12-MAA", "12-ADB" })
        {
            var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Board", $"palace-r5-{id}.png"));
            var pixels = BmpPixels.TryRead(ImageBytes.ToBmp(bytes))!;
            var board = new BoardRecognizer(maps).Recognize(pixels)!;
            var readings = new Dictionary<FieldSlot, SlotReading>();
            var marks = reader.ReadWithTrace(pixels, board, true, true, default, readings);
            var mark = marks.Single(m => m.Slot.ToString() == "A5");
            state.Observe(board.Map.Id, 5, marks, rows.Count * 1200);
            rows.Add(new { id, mark.Tier, mark.StandaloneName, mark.SpriteName, readings[mark.Slot].Decision,
                shapes = mark.ShapeCandidates.Select(c => new { c.Name, c.Score }),
                sparse = mark.Scores?.OrderBy(v => v.Value).Take(5), dense = mark.PurchasePoseScores,
                roster = state.Roster.Units.ToDictionary(u => u.Mark.Slot.ToString(), u => u.Name) });
            Assert.Equal(7, marks.Count);
            Assert.Equal("摆渡人", mark.StandaloneName);
            Assert.Equal("摆渡人", state.Roster.Units.Single(u => u.Mark.Slot == mark.Slot).Name);
        }
        File.WriteAllText(Path.Combine(TestOutput.Directory, "palace.json"), JsonSerializer.Serialize(rows));
    }

    [Fact, Trait("Category","LocalScreenshot")]
    public async Task ShopTimingAndDuplicateSlots()
    {
        using var fixture = new ShopReaderFixture();
        var hud = new ShopEconomyReader(fixture.Engine);
        var rows = new List<object>();
        for (int repeat = 0; repeat < 5; repeat++)
        foreach (var scene in new[] { "duplicates", "later" })
        {
            var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", $"mumu-shop-{scene}.png"));
            var sw = Stopwatch.StartNew();
            var frame = ImageBytes.ToBmp(bytes);
            var cards = await fixture.Reader.ReadAsync(frame, 1280, 720, fixture.Names);
            var shop = sw.Elapsed.TotalMilliseconds;
            var wallet = await hud.ReadAsync(frame, 1280, 720);
            rows.Add(new { repeat, scene, shop, hud = sw.Elapsed.TotalMilliseconds - shop, wallet.Money });
            Assert.Equal(scene == "duplicates" ? new[] { "虹彩活血饮料","地灵","夏栎","夏栎" } :
                new[] { "简易通讯机","锡人","夏栎","车尔尼" }, cards.Select(c => c.Name));
            Assert.Equal(4, cards.Select(c => c.Id).Distinct().Count());
            Assert.Equal(scene == "duplicates" ? 4 : 0, wallet.Money);
        }
        File.WriteAllText(Path.Combine(TestOutput.Directory, "shop-times.json"), JsonSerializer.Serialize(rows));
    }

    [Fact, Trait("Category","LocalScreenshot")]
    public async Task ScannerPublishesNewStockFromRecordedFrames()
    {
        using var fixture = new ShopReaderFixture();
        var maps = (await GarrisonBoardStore.LoadAsync(Path.Combine(AppContext.BaseDirectory, "data")))!;
        var frames = new[] { "duplicates", "later" }.Select(scene => File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "TestData", $"mumu-shop-{scene}.png"))).ToArray();
        byte[] frame = frames[0];
        int captures = 0;
        using var scanner = new OcrScannerService(() => { captures++; return frame; }, new GarrisonOcrStrategy(),
            fixture.Reader, fixture.Engine, new(), fixture.Names)
        {
            BoardRecognizer = new(maps), EconomyReader = new(fixture.Engine),
            PurchaseTargets = () => new HashSet<string> { "夏栎" }, AutomaticPurchaseTracking = true
        };
        string[] published = [];
        var times = new List<object>();
        var watch = new Stopwatch();
        double publication = 0;
        string expected = "";
        scanner.GameStateUpdated += state =>
        {
            published = state.ShopItems.Select(c => c.Name).ToArray();
            if (publication == 0 && published.Contains(expected)) publication = watch.Elapsed.TotalMilliseconds;
        };
        for (int i = 0; i < 6; i++)
        {
            frame = frames[i % 2];
            expected = i % 2 == 0 ? "虹彩活血饮料" : "简易通讯机";
            publication = 0;
            int start = captures;
            watch.Restart();
            await scanner.ScanNowAsync();
            Assert.True(publication > 0);
            var scan = watch.Elapsed.TotalMilliseconds;
            // The old labels leave after the second observation.
            await scanner.ScanNowAsync();
            Assert.Contains(expected, published);
            Assert.Equal(4, published.Length);
            Assert.Equal(2, captures - start);
            times.Add(new { i, captures = captures - start, publication, scan, twoScans = watch.Elapsed.TotalMilliseconds });
        }
        File.WriteAllText(Path.Combine(TestOutput.Directory, "shop-publication.json"), JsonSerializer.Serialize(times));
    }
}
