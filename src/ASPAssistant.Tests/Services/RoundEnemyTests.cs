using ASPAssistant.Core.Interop;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using ASPAssistant.Core.GameModes.GarrisonProtocol;
using ASPAssistant.Core.Services;

namespace ASPAssistant.Tests.Services;

public sealed class RoundEnemyTests
{
    [Fact]
    public void WindowsInputMatchesNativeLayout()
    {
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf<User32.MouseInput>());
        Assert.Equal(IntPtr.Size, Marshal.OffsetOf<User32.MouseInput>("Mouse").ToInt32());
    }

    [Fact]
    public void UnsentClicksCanRetryTwiceWithBackoffInTheSameRound()
    {
        var state = new RoundEnemyState();
        var now = DateTime.UtcNow;
        state.Observe(1);
        for (int i = 1; i <= 2; i++)
        {
            Assert.True(state.TryAutoOpen());
            Assert.True(state.RetryUnsent(now));
            Assert.False(state.CanOpen(true, true, now));
            now = now.AddSeconds(2 * i);
            Assert.True(state.CanOpen(true, true, now));
        }
        Assert.True(state.TryAutoOpen());
        Assert.False(state.RetryUnsent(now));
        Assert.False(state.CanOpen(true, true, now.AddMinutes(1)));
        state.Observe(2);
        Assert.True(state.CanOpen(true, true, now));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task OpensOnceOnTheConfirmedFrameForAutomaticAndManualRequests(bool automatic, bool sceneRecognized)
    {
        var ocr = new OversizedNameOcr();
        using var connection = new GameConnectionService();
        var clicks = 0;
        using var scanner = new OcrScannerService(() => null, new GarrisonOcrStrategy(), new ShopReader(ocr, []),
            ocr, new(), [], getConnection: () => connection)
        {
            RoundEnemyReader = new(ocr), EconomyReader = new(ocr), AutomaticRoundEnemies = automatic,
            SendCapturedClick = (target, point, frame, revision) =>
            {
                Assert.Same(connection, target);
                Assert.Equal(connection.CaptureRevision, revision);
                Assert.InRange(point.X, 1370, 1400);
                clicks++;
                return ClickResult.Sent;
            },
        };
        var hud = new ShopEconomy(2, 5, 4, false, "规程等级7", true);
        var tracker = (ShopEconomyTracker)typeof(OcrScannerService).GetField("_economy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scanner)!;
        tracker.Observe(hud);
        tracker.Observe(hud);
        if (!automatic) Assert.Null(scanner.RequestRoundEnemyRead());

        using var frame = new Bitmap(1920, 1080);
        DrawEnemyButton(frame, 255);
        var bytes = ImageBytes.Encode(frame);
        Task Observe(ShopEconomy sample) =>
            scanner.ObserveRoundEnemiesAsync(bytes, sample, sceneRecognized, connection, scanner.RunRevision, connection.CaptureRevision, Environment.TickCount64);

        await Observe(hud with { Round = 1 });
        await Observe(hud with { IsPreparation = false });
        Assert.Equal(0, clicks);
        await Observe(hud);
        await Observe(hud);
        Assert.Equal(automatic || sceneRecognized ? 1 : 0, clicks);
        Assert.Equal(0, ocr.Reads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutomaticallyOpenedPanelClosesOnlyAfterPublishingACompleteList(bool hasNames)
    {
        using var closed = new Bitmap(1280, 720);
        DrawEnemyButton(closed, 255);
        using var open = new Bitmap(closed);
        using (var graphics = Graphics.FromImage(open))
        {
            graphics.FillRectangle(Brushes.Gray, 742, 250, 20, 14);
            graphics.FillRectangle(Brushes.White, 795, 80, 40, 12);
            graphics.FillRectangle(Brushes.White, 862, 122, 46, 19);
        }
        var openBytes = ImageBytes.Encode(open);
        var hud = new ShopEconomy(2, 5, 4, false, "规程等级7", true);
        var ocr = new OversizedNameOcr(hasNames);
        using var connection = new GameConnectionService();
        var clicks = 0;
        string[] published = [];
        using var scanner = new OcrScannerService(() => openBytes, new GarrisonOcrStrategy(), new ShopReader(ocr, []),
            ocr, new(), [], getConnection: () => connection)
        {
            RoundEnemyReader = new(ocr), EconomyReader = new(ocr), AutomaticRoundEnemies = true,
            SendCapturedClick = (_, _, _, _) =>
            {
                if (++clicks == 2) Assert.Equal(["猎狗pro"], published);
                return ClickResult.Sent;
            },
        };
        scanner.RoundEnemiesChanged += () => published = scanner.CurrentRoundEnemies!.Names.ToArray();
        var tracker = (ShopEconomyTracker)typeof(OcrScannerService).GetField("_economy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scanner)!;
        tracker.Observe(hud);
        tracker.Observe(hud);
        Task Observe(byte[] screenshot, ShopEconomy? exactHud) =>
            scanner.ObserveRoundEnemiesAsync(screenshot, exactHud, false, connection, scanner.RunRevision, connection.CaptureRevision, Environment.TickCount64);

        await Observe(ImageBytes.Encode(closed), hud);
        Assert.Equal(1, clicks);
        await Observe(openBytes, null);
        var reads = ocr.Reads;
        await scanner.ObserveRoundEnemiesAsync(openBytes, null, false, connection, scanner.RunRevision,
            connection.CaptureRevision, Environment.TickCount64 + 1);
        if (hasNames) Assert.Equal(reads, ocr.Reads);
        else Assert.True(ocr.Reads > reads);
        reads = ocr.Reads;
        await Observe(openBytes, null);
        Assert.Equal(reads, ocr.Reads);
        Assert.Equal(hasNames ? 2 : 1, clicks);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("closed")]
    [InlineData("next-round")]
    [InlineData("manual")]
    [InlineData("passive")]
    public async Task EmptyAutomaticReadWaitsForOneNewLivePanel(string scene)
    {
        var ocr = new OversizedNameOcr(hudRound: 3);
        using var board = new Bitmap(1280, 720);
        DrawEnemyButton(board, 255);
        using var panel = new Bitmap(board);
        using (var graphics = Graphics.FromImage(panel))
        {
            graphics.FillRectangle(Brushes.Gray, 742, 250, 20, 14);
            graphics.FillRectangle(Brushes.White, 795, 80, 40, 12);
        }
        var empty = ImageBytes.Encode(panel);
        using (var graphics = Graphics.FromImage(panel))
            graphics.FillRectangle(Brushes.White, 862, 122, 46, 19);
        var full = ImageBytes.Encode(panel);
        var closed = ImageBytes.Encode(board);
        var hud = new ShopEconomy(3, 4, 5, false, null, true);
        using var connection = new GameConnectionService();
        List<Point> clicks = [];
        using var scanner = new OcrScannerService(() => full, new GarrisonOcrStrategy(), new ShopReader(ocr, []),
            ocr, new(), [], getConnection: () => connection)
        {
            RoundEnemyReader = new(ocr), EconomyReader = new(ocr), AutomaticRoundEnemies = scene is not ("passive" or "manual"),
            SendCapturedClick = (_, point, _, _) => { clicks.Add(point); return ClickResult.Sent; }
        };
        var tracker = (ShopEconomyTracker)typeof(OcrScannerService).GetField("_economy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scanner)!;
        tracker.Observe(hud);
        tracker.Observe(hud);
        long tick = Environment.TickCount64;
        Task Observe(byte[] bytes, bool buffered = false) => scanner.ObserveRoundEnemiesAsync(bytes, hud, true,
            connection, scanner.RunRevision, connection.CaptureRevision, tick, buffered: buffered);
        if (scene == "manual") Assert.Null(scanner.RequestRoundEnemyRead());
        tick = Environment.TickCount64;
        if (scene != "passive")
        {
            await Observe(closed);
            Assert.Single(clicks);
            clicks.Clear();
        }
        await Observe(empty);
        int reads = ocr.Reads;
        Assert.True(reads > 0);
        await Observe(full); // The same scan cannot spend the retry on an older frame.
        tick = Environment.TickCount64 + 1;
        await Observe(full, buffered: true);
        Assert.Equal(reads, ocr.Reads);
        if (scene == "next-round") hud = hud with { Round = 4 };
        await Observe(scene == "closed" ? closed : full);
        if (scene is "open" or "manual")
        {
            Assert.Equal(["猎狗pro"], scanner.CurrentRoundEnemies!.Names);
            Assert.Equal(new Point(640, 360), Assert.Single(clicks));
        }
        else if (scene == "passive")
        {
            Assert.Equal(["猎狗pro"], scanner.CurrentRoundEnemies!.Names);
            Assert.Empty(clicks);
        }
        else
        {
            Assert.Equal(reads, ocr.Reads);
            Assert.Empty(clicks);
            Assert.Empty(scanner.CurrentRoundEnemies!.Names);
            if (scene == "closed")
            {
                tick += 5000;
                await Observe(closed);
                Assert.Empty(clicks);
            }
        }
    }

    [Theory]
    [InlineData("manual-board", 1280, "open")]
    [InlineData("manual-panel", 1600, "open")]
    [InlineData("automatic", 1920, "open")]
    [InlineData("passive", 1600, "open")]
    [InlineData("manual-panel", 1600, "closed")]
    [InlineData("automatic", 1280, "next-round")]
    [InlineData("manual-panel", 1280, "partial")]
    [InlineData("automatic", 1280, "partial")]
    [InlineData("manual-panel", 1280, "changed-ink")]
    [InlineData("manual-panel", 1280, "scroll-by-player")]
    public async Task ReturnsToBoardAfterRequestedReadIncludingPartialList(string entry, int latestWidth, string lastScene)
    {
        using var board = new Bitmap(1280, 720);
        DrawEnemyButton(board, 255);
        var boardBytes = ImageBytes.Encode(board);
        using var panel = new Bitmap(1280, 720); // No magnifier is visible on this panel.
        using (var graphics = Graphics.FromImage(panel))
        {
            graphics.FillRectangle(Brushes.Gray, 742, 250, 20, 14);
            graphics.FillRectangle(Brushes.White, 795, 80, 40, 12);
            graphics.FillRectangle(Brushes.White, 862, 122, 46, 19);
        }
        var panelBytes = ImageBytes.Encode(panel);
        using var latest = new Bitmap(lastScene == "closed" ? board : panel, latestWidth, latestWidth * 9 / 16);
        if (lastScene is "changed-ink" or "scroll-by-player")
        {
            // An animated pixel behind the translucent panel changes its page stamp.
            var area = RoundEnemyReader.Inspect(latest, false);
            latest.SetPixel(area.Names.Left, area.Title.Top, Color.White);
            Assert.NotEqual(RoundEnemyReader.PageStamp(panel, RoundEnemyReader.Inspect(panel, false)),
                RoundEnemyReader.PageStamp(latest, area));
        }
        var latestBytes = ImageBytes.Encode(latest);
        var ocr = new OversizedNameOcr { NextRoundFrame = lastScene == "next-round" ? latestBytes : null };
        using var connection = new GameConnectionService();
        List<Point> clicks = [];
        int captures = 0;
        bool returned = false;
        using var scanner = new OcrScannerService(() => { captures++; return returned ? boardBytes : latestBytes; },
            new GarrisonOcrStrategy(), new ShopReader(ocr, []), ocr, new(), [], getConnection: () => connection)
        {
            RoundEnemyReader = new(ocr), EconomyReader = new(ocr), AutomaticRoundEnemies = entry == "automatic",
            RoundEnemyScroll = (_, _, _, _) => false,
            SendCapturedClick = (_, point, size, _) =>
            {
                clicks.Add(point);
                returned = point == new Point(size.Width / 2, size.Height / 2);
                return ClickResult.Sent;
            }
        };
        var hud = new ShopEconomy(2, 4, 5, false, null, true);
        var tracker = (ShopEconomyTracker)typeof(OcrScannerService).GetField("_economy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scanner)!;
        tracker.Observe(hud);
        tracker.Observe(hud);
        if (entry.StartsWith("manual", StringComparison.Ordinal)) Assert.Null(scanner.RequestRoundEnemyRead());
        Task Board(bool recognized) => scanner.ObserveRoundEnemiesAsync(boardBytes, hud, recognized, connection,
            scanner.RunRevision, connection.CaptureRevision, Environment.TickCount64);
        if (entry is "manual-board" or "automatic")
        {
            await Board(true);
            Assert.InRange(Assert.Single(clicks).X, 900, 950);
            await Board(false); // Animation between opening and the first retained panel.
            Assert.Single(clicks);
            clicks.Clear();
        }
        var layout = RoundEnemyReader.Inspect(panel, false) with { MoreBelow = lastScene is "partial" or "scroll-by-player" };
        Task ObservePanel() => scanner.ObserveRoundEnemiesAsync(panelBytes, hud, false, connection, scanner.RunRevision,
            connection.CaptureRevision, Environment.TickCount64, (panel, layout), buffered: entry == "automatic");
        await ObservePanel();
        if (lastScene == "scroll-by-player")
        {
            Assert.True(scanner.CurrentRoundEnemies!.IsPartial);
            Assert.Empty(clicks);
            await scanner.ObserveRoundEnemiesAsync(latestBytes, hud, false, connection, scanner.RunRevision,
                connection.CaptureRevision, Environment.TickCount64 + 1);
        }
        Assert.Equal(["猎狗pro"], scanner.CurrentRoundEnemies!.Names);
        Assert.Equal(lastScene == "partial", scanner.CurrentRoundEnemies.IsPartial);
        if (lastScene == "partial") Assert.Contains("已返回棋盘", scanner.CurrentRoundEnemies.Hint);
        int reads = ocr.Reads;
        await ObservePanel();
        Assert.Equal(reads, ocr.Reads);
        bool shouldClose = entry != "passive" && lastScene is "open" or "partial" or "changed-ink" or "scroll-by-player";
        Assert.Equal(entry == "passive" ? 0 : (shouldClose ? 2 : 1) +
            (lastScene == "partial" ? 1 : lastScene == "scroll-by-player" ? 2 : 0), captures);
        if (shouldClose)
            Assert.Equal(new Point(latest.Width / 2, latest.Height / 2), Assert.Single(clicks));
        else Assert.Empty(clicks);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RecoveryAfterCaptureLossChecksPanelAndCurrentRound(bool panelOpened, bool roundChanged)
    {
        var ocr = new OversizedNameOcr(hudRound: roundChanged ? 2 : 1);
        using var connection = new GameConnectionService();
        using var closed = new Bitmap(1280, 720);
        DrawEnemyButton(closed, 255);
        using var opened = new Bitmap(closed);
        using (var graphics = Graphics.FromImage(opened))
        {
            graphics.FillRectangle(Brushes.Gray, 742, 250, 20, 14);
            graphics.FillRectangle(Brushes.White, 795, 80, 40, 12);
            graphics.FillRectangle(Brushes.White, 862, 122, 46, 19);
        }
        var closedBytes = ImageBytes.Encode(closed);
        var openBytes = ImageBytes.Encode(opened);
        byte[]? capture = null;
        int clicks = 0;
        using var scanner = new OcrScannerService(() => capture, new GarrisonOcrStrategy(), new ShopReader(ocr, []),
            ocr, new(), [], getConnection: () => connection)
        {
            AutomaticRoundEnemies = true, RoundEnemyReader = new(ocr), EconomyReader = new(ocr),
            SendCapturedClick = (_, _, _, _) => { clicks++; return ClickResult.Sent; }
        };
        await scanner.ScanNowAsync();
        var tracker = (ShopEconomyTracker)typeof(OcrScannerService).GetField("_economy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scanner)!;
        var hud = new ShopEconomy(1, 4, 5, false, "规程等级13", true);
        tracker.Observe(hud);
        tracker.Observe(hud);
        Task Observe(byte[] image, long tick) => scanner.ObserveRoundEnemiesAsync(image, hud, true,
            connection, scanner.RunRevision, connection.CaptureRevision, tick);
        await Observe(closedBytes, 0);
        await Observe(closedBytes, 200);
        await scanner.ScanNowAsync(); // PC foreground mismatch: capture returns null.
        Assert.Equal(1, clicks);

        if (roundChanged && !panelOpened)
        {
            hud = hud with { Round = 2 };
            await Observe(closedBytes, 14000); // HUD moved ahead of the stabilized tracker.
            Assert.Equal(1, clicks);
            tracker.Observe(hud);
            tracker.Observe(hud);
            await Observe(closedBytes, 14500);
            Assert.Equal(2, scanner.CurrentRoundEnemies!.Round);
            Assert.Equal(2, clicks);
            return;
        }
        if (panelOpened)
        {
            capture = openBytes; // An unexpected auto-close would be observable.
            await scanner.ScanNowAsync();
            if (roundChanged)
            {
                Assert.Empty(scanner.CurrentRoundEnemies!.Names);
                await scanner.ScanNowAsync();
            }
            Assert.Equal(roundChanged ? 2 : 1, scanner.CurrentRoundEnemies!.Round);
            Assert.Equal(["猎狗pro"], scanner.CurrentRoundEnemies!.Names);
            var reads = ocr.Reads;
            await Observe(openBytes, 14500);
            Assert.Equal(reads, ocr.Reads);
            Assert.Equal(1, clicks);
            return;
        }
        await Observe(closedBytes, 14000);
        await Observe(closedBytes, 17500);
        Assert.Equal(1, clicks);
        await Observe(closedBytes, 18000);
        Assert.Equal(2, clicks);
        await Observe(closedBytes, 18500);
        await Observe(closedBytes, 22500);
        await Observe(closedBytes, 23000);
        Assert.Contains("未确认敌人面板", scanner.CurrentRoundEnemies!.Hint);
        Assert.Equal(2, clicks);
        Assert.Equal(0, ocr.Reads);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task BriefPanelDuringBusyScanBelongsOnlyToItsRound(bool probe, bool newRound, bool automatic)
    {
        using var closed = new Bitmap(1280, 720);
        using var open = new Bitmap(closed);
        using (var g = Graphics.FromImage(open))
        {
            g.FillRectangle(Brushes.Gray, 742, 250, 20, 14);
            g.FillRectangle(Brushes.White, 795, 80, 40, 12);
            g.FillRectangle(Brushes.White, 862, 122, 46, 19);
        }
        var panel = ImageBytes.Encode(open);
        var shop = ImageBytes.Encode(closed);
        var ocr = new OversizedNameOcr(hudRound: 1) { NextRoundFrame = newRound ? shop : null };
        using var connection = new GameConnectionService();
        Action? duringCapture = null;
        byte[]? live = null;
        int fullCaptures = 0;
        using var scanner = new OcrScannerService(() => { duringCapture?.Invoke(); return live; },
            new GarrisonOcrStrategy(), new ShopReader(ocr, []), ocr, new(), [], getConnection: () => connection)
        {
            AutomaticRoundEnemies = automatic, RoundEnemyReader = new(ocr), EconomyReader = new(ocr),
            CaptureEnemyPanelFrame = probe ? () => { fullCaptures++; return panel; } : null,
            SendCapturedClick = (_, _, _, _) => throw new InvalidOperationException("Unexpected input")
        };
        await scanner.ScanNowAsync(); // Attach the source before stabilizing the preparation HUD.
        var tracker = (ShopEconomyTracker)typeof(OcrScannerService).GetField("_economy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scanner)!;
        var hud = new ShopEconomy(1, 4, 5, false, null, true);
        tracker.Observe(hud);
        tracker.Observe(hud);
        scanner.ProbeBusyRound();
        Assert.Equal(0, fullCaptures);
        duringCapture = () => { scanner.ProbeBusyRound(); scanner.ProbeBusyRound(); };
        await scanner.ScanNowAsync();
        Assert.Equal(0, ocr.Reads);
        live = shop;
        await scanner.ScanNowAsync();
        Assert.Equal(probe ? 1 : 0, fullCaptures);
        if (probe && !newRound)
        {
            Assert.Equal(["猎狗pro"], scanner.CurrentRoundEnemies!.Names);
            await scanner.ScanNowAsync();
            Assert.Equal(1, fullCaptures);
        }
        else Assert.Empty(scanner.CurrentRoundEnemies?.Names ?? []);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DropsBufferedPanelWhenSourceOrRunChanges(bool changeSource)
    {
        var ocr = new OversizedNameOcr();
        using var first = new GameConnectionService();
        using var second = new GameConnectionService();
        var active = first;
        Action? duringCapture = null;
        using var frame = new Bitmap(1280, 720);
        var bytes = ImageBytes.Encode(frame);
        int fullCaptures = 0;
        using var scanner = new OcrScannerService(() => { duringCapture?.Invoke(); return null; },
            new GarrisonOcrStrategy(), new ShopReader(ocr, []), ocr, new(), [], getConnection: () => active)
        {
            RoundEnemyReader = new(ocr), EconomyReader = new(ocr),
            CaptureEnemyPanelFrame = () => { fullCaptures++; return bytes; }
        };
        await scanner.ScanNowAsync();
        var tracker = (ShopEconomyTracker)typeof(OcrScannerService).GetField("_economy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scanner)!;
        var hud = new ShopEconomy(1, 4, 5, false, null, true);
        tracker.Observe(hud);
        tracker.Observe(hud);
        duringCapture = scanner.ProbeBusyRound;
        await scanner.ScanNowAsync();
        var slot = typeof(OcrScannerService).GetField("_bufferedPanel", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.NotNull(slot.GetValue(scanner));
        if (changeSource) active = second;
        else scanner.ResetEconomy();
        await scanner.ScanNowAsync();
        Assert.Null(slot.GetValue(scanner));
        Assert.Equal(1, fullCaptures);
        Assert.Equal(0, ocr.Reads);
    }

    [Fact]
    public void ManualRetryWorksWithAutomaticDisabledAndDoesNotRepeatAfterSuccess()
    {
        var state = new RoundEnemyState();
        var now = DateTime.UtcNow;
        state.Observe(2);
        Assert.False(state.CanOpen(automatic: false, sceneRecognized: true, now));
        Assert.True(state.CanOpen(automatic: true, sceneRecognized: true, now));
        Assert.False(state.CanOpen(automatic: true, sceneRecognized: false, now));
        Assert.True(state.TryAutoOpen());
        state.BeginConfirmation();
        state.PanelRead = true;
        Assert.True(state.RequestManual(now));
        Assert.False(state.PanelRead);
        Assert.False(state.RequestManual(now));
        Assert.False(state.CanOpen(automatic: false, sceneRecognized: true, now));
        state.EndConfirmation();
        Assert.True(state.CanOpen(automatic: false, sceneRecognized: true, now.AddSeconds(3)));
        Assert.True(state.TryManualOpen());
        Assert.False(state.CanOpen(automatic: false, sceneRecognized: true, now.AddSeconds(3)));
        Assert.False(state.ExpireManual(now.AddSeconds(7)));
        Assert.True(state.ExpireManual(now.AddSeconds(8)));
        Assert.False(state.Info!.IsReading);
        Assert.True(state.RequestManual(now.AddSeconds(9)));
        state.ApplyRead(new(["猎狗pro"], [], true, true, false, "敌人", 0));
        Assert.False(state.RequestManual(now.AddSeconds(10)));
        Assert.Equal("猎狗pro", Assert.Single(state.Info!.Names));
        Assert.True(state.Observe(3));
        Assert.Empty(state.Info!.Names);
    }

    [Fact]
    public async Task ManualButtonsExpireWhileCaptureIsBlockedAndIgnoreLateResults()
    {
        var ocr = new OversizedNameOcr();
        var folder = Path.Combine(AppContext.BaseDirectory, "data");
        var maps = (await ASPAssistant.Core.Data.GarrisonBoardStore.LoadAsync(folder))!;
        using var connection = new GameConnectionService(new() { Source = ASPAssistant.Core.Models.GameCaptureSource.Adb });
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reads = 0;
        var clicks = 0;
        using var scanner = new OcrScannerService(() =>
        {
            Interlocked.Increment(ref reads);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(16));
            return null;
        }, new GarrisonOcrStrategy(), new ShopReader(ocr, []), ocr, new(), [], getConnection: () => connection)
        {
            EconomyReader = new(ocr), LiveReader = new(folder, maps), BoardRecognizer = new(maps),
            SendCapturedClick = (_, _, _, _) => { clicks++; return ClickResult.Sent; }
        };
        var tracker = (ShopEconomyTracker)typeof(OcrScannerService).GetField("_economy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scanner)!;
        var hud = new ShopEconomy(2, 4, 5, false, null, true);
        tracker.Observe(hud);
        tracker.Observe(hud);
        var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scanner.RoundEnemiesChanged += () => { if (scanner.CurrentRoundEnemies?.Hint?.Contains("超时") == true) expired.TrySetResult(); };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(scanner.RequestRoundEnemyRead());
        var field = scanner.ReadLiveFieldAsync();
        try
        {
            Assert.True(entered.Wait(2000));
            await expired.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("8 秒", await field.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.InRange(clock.Elapsed.TotalSeconds, 7.8, 10);
            Assert.False(scanner.CurrentRoundEnemies!.IsReading);
            Assert.Null(scanner.RequestRoundEnemyRead());
        }
        finally { release.Set(); }
        Assert.DoesNotContain("正在识别", await scanner.ReadLiveFieldAsync());
        Assert.Empty(scanner.LiveRoster.Units);
        Assert.Equal(2, reads);
        Assert.Equal(0, clicks);
    }

    [Fact]
    public async Task LatePanelOcrCannotWriteToANewManualRequest()
    {
        using var panel = new Bitmap(1280, 720);
        using (var g = Graphics.FromImage(panel))
        {
            g.FillRectangle(Brushes.White, 795, 80, 40, 12);
            g.FillRectangle(Brushes.White, 862, 122, 46, 19);
        }
        var image = ImageBytes.Encode(panel);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ocr = new OversizedNameOcr { WaitForRead = async () => { entered.TrySetResult(); await release.Task; } };
        using var connection = new GameConnectionService();
        int clicks = 0;
        using var scanner = new OcrScannerService(() => image, new GarrisonOcrStrategy(), new ShopReader(ocr, []), ocr,
            new(), [], getConnection: () => connection)
        {
            RoundEnemyReader = new(ocr), EconomyReader = new(ocr),
            SendCapturedClick = (_, _, _, _) => { clicks++; return ClickResult.Sent; }
        };
        var tracker = (ShopEconomyTracker)typeof(OcrScannerService).GetField("_economy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scanner)!;
        var hud = new ShopEconomy(2, 4, 5, false, null, true);
        tracker.Observe(hud); tracker.Observe(hud);
        var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scanner.RoundEnemiesChanged += () => { if (scanner.CurrentRoundEnemies?.Hint?.Contains("超时") == true) expired.TrySetResult(); };
        Assert.Null(scanner.RequestRoundEnemyRead());
        var old = scanner.ObserveRoundEnemiesAsync(image, hud, false, connection, scanner.RunRevision, connection.CaptureRevision,
            Environment.TickCount64, (panel, RoundEnemyReader.Inspect(panel, false)));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await expired.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(scanner.RequestRoundEnemyRead());
        }
        finally { release.TrySetResult(); }
        await old;
        Assert.True(scanner.CurrentRoundEnemies!.IsReading);
        Assert.Empty(scanner.CurrentRoundEnemies.Names);
        Assert.Equal(1, ocr.Reads);
        Assert.Equal(0, clicks);
    }

    [Fact]
    public async Task OversizedOcrBoxInSmallMixedScriptNameGetsOneEnlargedRetry()
    {
        var ocr = new OversizedNameOcr();
        using var frame = new Bitmap(1280, 720);
        using (var graphics = Graphics.FromImage(frame))
        {
            graphics.Clear(Color.Black);
            graphics.FillRectangle(Brushes.Gray, 742, 250, 20, 14);
            graphics.FillRectangle(Brushes.White, 795, 80, 40, 12);
            graphics.FillRectangle(Brushes.White, 862, 122, 46, 19);
        }
        using var stream = new MemoryStream();
        frame.Save(stream, ImageFormat.Png);
        var layout = RoundEnemyReader.Inspect(frame, locateButton: false);
        var result = await new RoundEnemyReader(ocr).ReadAsync(stream.ToArray(), frame, layout);
        Assert.Equal(new[] { "猎狗pro" }, result!.Names);
        Assert.Equal(3, ocr.Reads);
    }

    [Fact]
    public void CompletedListAndAutomaticAttemptAreSharedForTheRound()
    {
        var state = new RoundEnemyState();
        Assert.True(state.Observe(5));
        Assert.Empty(state.Info!.Names);
        Assert.True(state.TryAutoOpen());
        Assert.False(state.TryAutoOpen());
        state.PanelRead = true;
        state.ApplyRead(new(["源石虫"], [], true, true, false, "敌人", 0));
        state.PanelClosed();
        Assert.False(state.Observe(5));
        Assert.False(state.Observe(4));
        Assert.Equal("源石虫", Assert.Single(state.Info!.Names));
        Assert.True(state.Observe(6));
        Assert.Empty(state.Info!.Names);
        Assert.True(state.TryAutoOpen());
        state.Reset();
        Assert.True(state.Observe(1));
        Assert.False(state.AutoAttempted);
    }

    [Fact]
    public void CompletesScrolledListOnlyAfterReadingOverlappingPagesFromTheTop()
    {
        var state = new RoundEnemyState();
        state.Observe(3);
        state.ApplyRead(new(["宿主士兵", "步兵"], ["磨砻"], true, false, false, "敌人", 0));
        Assert.True(state.Info!.IsPartial);
        Assert.Equal("敌人", state.CoveredPage!.LastSection);
        Assert.True(state.RequestManual(DateTime.UtcNow));
        state.ApplyRead(new(["源石虫"], [], false, true, false, "敌人", 0));
        Assert.True(state.Info!.IsPartial);
        Assert.Contains("源石虫", state.Info.Names);
        state.ApplyRead(new(["宿主士兵", "步兵"], ["磨砻"], true, false, false, "敌人", 0));
        state.ApplyRead(new(["步兵", "源石虫"], [], false, true, false, "敌人", 0));
        Assert.True(state.Info!.HasCompleteList);
        Assert.Equal(new[] { "宿主士兵", "步兵", "源石虫" }, state.Info.Names);
        Assert.Equal("磨砻", Assert.Single(state.Info.Bounties));
    }

    [Fact]
    public void BountyAloneAndAnUnresolvedMiddleRowCannotCompleteTheRoster()
    {
        var state = new RoundEnemyState();
        state.Observe(11);
        state.ApplyRead(new([], ["异质裂兽·α"], true, true, false, "悬赏", 0));
        Assert.True(state.Info!.IsPartial);
        state.ApplyRead(new(["磨砻"], ["异质裂兽·α"], true, false, true, "敌人", 1));
        state.ApplyRead(new(["磨砻", "俗心"], [], false, true, false, "敌人", 0));
        Assert.False(state.Info.HasCompleteList);
        state.Observe(14);
        state.ApplyRead(new(["宿主重装士兵"], [], true, false, false, "敌人", 0) { Leaders = ["假想敌：管"] });
        state.ApplyRead(new(["宿主重装士兵", "堂皇", "“余音”"], [], false, true, false, "敌人", 0));
        Assert.True(state.Info.HasCompleteList);
        Assert.Equal(["假想敌：管"], state.Info.Leaders);
        Assert.Equal(3, state.Info.Names.Count);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task ScrollsOwnedPanelsOnlyAndRequiresAnOverlappingStableBottom(bool adb, bool passive, bool moves)
    {
        using var top = new Bitmap(1280, 720);
        using (var g = Graphics.FromImage(top))
        {
            g.Clear(Color.Black);
            g.FillRectangle(Brushes.White, 795, 80, 40, 12);
            g.FillRectangle(Brushes.White, 862, 122, 46, 19);
            g.FillRectangle(Brushes.Gray, 742, 345, 20, 14);
            g.FillRectangle(Brushes.Gray, 1118, 69, 3, 95);
        }
        using var bottom = new Bitmap(top);
        using (var g = Graphics.FromImage(bottom))
        {
            g.FillRectangle(Brushes.Black, 1118, 69, 3, 95);
            g.FillRectangle(Brushes.Gray, 1118, 246, 3, 95);
            // A changed visible row footprint accompanies the moved thumb.
            g.FillRectangle(Brushes.White, 865, 150, 32, 5);
        }
        var hud = new ShopEconomy(2, 5, 4, false, "规程等级7", true);
        var first = ImageBytes.Encode(top);
        var last = ImageBytes.Encode(bottom);
        var live = first;
        var ocr = new OversizedNameOcr();
        using var connection = new GameConnectionService(new()
        {
            Source = adb ? ASPAssistant.Core.Models.GameCaptureSource.Adb : ASPAssistant.Core.Models.GameCaptureSource.Pc
        });
        int scrolls = 0, closes = 0;
        using var scanner = new OcrScannerService(() => live, new GarrisonOcrStrategy(), new ShopReader(ocr, []),
            ocr, new(), [], getConnection: () => connection)
        {
            RoundEnemyReader = new(ocr), EconomyReader = new(ocr), AutomaticRoundEnemies = false,
            RoundEnemyScroll = (target, point, size, revision) =>
            {
                Assert.Equal(adb, target.IsAdb);
                Assert.Equal(connection.CaptureRevision, revision);
                Assert.True(RoundEnemyReader.Inspect(top, false).Names.Contains(point));
                scrolls++;
                if (moves) live = last;
                return true;
            },
            SendCapturedClick = (_, _, _, _) => { closes++; return ClickResult.Sent; }
        };
        var tracker = (ShopEconomyTracker)typeof(OcrScannerService).GetField("_economy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scanner)!;
        tracker.Observe(hud);
        tracker.Observe(hud);
        if (!passive) Assert.Null(scanner.RequestRoundEnemyRead());
        Task Observe(byte[] bytes) => scanner.ObserveRoundEnemiesAsync(bytes, hud, true, connection, scanner.RunRevision,
            connection.CaptureRevision, Environment.TickCount64);
        await Observe(first);
        if (passive)
        {
            Assert.True(scanner.CurrentRoundEnemies!.IsPartial);
            await Observe(last);
        }
        Assert.Equal(passive ? 0 : 1, scrolls);
        Assert.Equal(passive ? 0 : 1, closes);
        Assert.Equal(moves, scanner.CurrentRoundEnemies!.HasCompleteList);
    }

    [Theory]
    [InlineData("shop-closure-round1-1600x1024.png", 1144)]
    [InlineData("shop-terminal-ui100-2000x1125.png", 1430)]
    [InlineData("shop-ui100-2000x1125.png", 1474)]
    [InlineData("shop-ursus-ui100-2000x1125.png", 1431)]
    public void FindsEnemyButtonWithoutMistakingTheShopForTheEnemyPanel(string file, int expectedX)
    {
        using var frame = new Bitmap(Path.Combine(AppContext.BaseDirectory, "TestData", file));
        var layout = RoundEnemyReader.Inspect(frame, locateButton: true);
        Assert.True(layout.Title.IsEmpty);
        Assert.InRange(layout.Button!.Value.X, expectedX - 17, expectedX + 17);
    }

    [Theory]
    [InlineData(1280, 720, 255)]
    [InlineData(1434, 807, 160)]
    [InlineData(1920, 1080, 255)]
    public void FindsTheGlyphsInsideAGlowingBorder(int width, int height, int brightness)
    {
        using var frame = new Bitmap(width, height);
        DrawEnemyButton(frame, brightness);
        var layout = RoundEnemyReader.Inspect(frame, locateButton: true);
        Assert.NotNull(layout.Button);
        Assert.InRange(layout.Button.Value.X, (int)(1375 * width / 1920d), (int)(1395 * width / 1920d));
    }

    [Theory]
    [InlineData("ban-sample-1920x1080.png")]
    [InlineData("info-ingame-1920x1080.png")]
    [InlineData("gameplay-markers-1920x1080.png")]
    public void IgnoresFramesWithoutEnemyButton(string file)
    {
        using var frame = new Bitmap(Path.Combine(AppContext.BaseDirectory, "TestData", file));
        Assert.Null(RoundEnemyReader.Inspect(frame, locateButton: true).Button);
    }

    private static void DrawEnemyButton(Bitmap frame, int brightness)
    {
        using var graphics = Graphics.FromImage(frame);
        graphics.Clear(Color.Black);
        graphics.ScaleTransform(frame.Width / 1920f, frame.Height / 1080f);
        using var outline = new Pen(Color.FromArgb(brightness, brightness * 4 / 5, 25), 3);
        using var glyph = new SolidBrush(outline.Color);
        graphics.DrawRectangle(outline, 1343, 28, 91, 34);
        graphics.DrawEllipse(outline, 1356, 37, 20, 18);
        graphics.FillRectangle(glyph, 1370, 47, 7, 11);
        foreach (var x in new[] { 1387, 1403 })
            graphics.FillPolygon(glyph, new[] { new Point(x, 42), new Point(x + 13, 48), new Point(x, 54) });
    }

    private sealed class OversizedNameOcr(bool hasNames = true, int hudRound = 2) : IOcrEngine
    {
        public int Reads { get; private set; }
        public byte[]? NextRoundFrame { get; init; }
        public Func<Task>? WaitForRead { get; init; }

        public async Task<IReadOnlyList<OcrTextResult>> RecognizeRegionAsync(byte[] png, int x, int y, int w, int h)
        {
            Reads++;
            if (WaitForRead is not null) await WaitForRead();
            IReadOnlyList<OcrTextResult> result = y == 0 && w > 700 ? [new($"回合{(ReferenceEquals(png, NextRoundFrame) ? hudRound + 1 : hudRound)}", (450, 26, 40, 13)), new("休息一下", (631, 24, 54, 16))]
                : x != 0 ? [new("敌人", (x + 15, y, 35, 16))]
                : !hasNames ? []
                : w < 300 ? [new("措狗pro", (20, 3, 50, 36))]
                : [new("猎狗pro", (20, 17, 76, 24))];
            return result;
        }

        public Task<IReadOnlyList<OcrTextResult>> RecognizeLineAsync(byte[] png, int x, int y, int w, int h)
            => throw new NotSupportedException();

        public Task<string?> FindCandidateInRegionAsync(byte[] png, int x, int y, int w, int h, IReadOnlyList<string> candidates)
            => throw new NotSupportedException();
    }
}
