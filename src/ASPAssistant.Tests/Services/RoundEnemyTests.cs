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
    [Theory]
    [InlineData("规程等级13", 5, "敌人", true)]
    [InlineData("规程等级13", 5, "悬赏", false)]
    [InlineData("规程等级13", 14, "敌人", false)]
    [InlineData("极速模拟", 9, "领袖", false)]
    public async Task OnlyOrdinaryRoundsFinishWithoutBottomEvidence(string mode, int round, string heading, bool singlePage)
    {
        using var panel = new Bitmap(1280, 720);
        using (var graphics = Graphics.FromImage(panel))
        {
            graphics.FillRectangle(Brushes.White, 795, 80, 40, 12);
            graphics.FillRectangle(Brushes.White, 862, 122, 46, 19);
        }
        var routes = await ASPAssistant.Core.Data.GarrisonRoundRouteStore.LoadAsync(GarrisonRoundRoutesTests.DataDirectory);
        var reader = new RoundEnemyReader(new OversizedNameOcr { Heading = heading }, routes);
        var layout = RoundEnemyReader.Inspect(panel, false) with { BottomUnknown = true };
        var read = await reader.ReadAsync(ImageBytes.Encode(panel), panel, layout, mode: mode, round: round);
        Assert.Equal(singlePage, read!.EndsAtBottom);
        Assert.Equal(singlePage, read.StartsAtTop);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DesertRoundFourteenKeepsTheEnemySectionAcrossSmallScroll(int attempt)
    {
        if (Environment.GetEnvironmentVariable("ASPASSISTANT_DESERT_REPLAY") is not { } directory) return;
        using var ocr = new MaaOcrEngine(Path.Combine(AppContext.BaseDirectory, "data"));
        var routes = await ASPAssistant.Core.Data.GarrisonRoundRouteStore.LoadAsync(GarrisonRoundRoutesTests.DataDirectory);
        var reader = new RoundEnemyReader(ocr, routes);
        RoundEnemyReadResult? previous = null;
        var pages = new List<RoundEnemyReadResult>();
        foreach (int step in new[] { 3, 4 })
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, $"enemy-{attempt}-{step}.png"));
            using var stream = new MemoryStream(bytes);
            using var frame = new Bitmap(stream);
            var layout = RoundEnemyReader.Inspect(frame, true);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var read = await reader.ReadAsync(bytes, frame, layout, previous, "规程等级4", 14);
            Assert.NotNull(read);
            File.AppendAllText(Path.Combine(TestOutput.Directory, $"desert-enemy-{attempt}.txt"),
                $"{step}: {layout}, names={string.Join(",",read.Names)}, leaders={string.Join(",",read.Leaders)}, bottom={read.EndsAtBottom}, time={watch.ElapsedMilliseconds}ms\n");
            pages.Add(read);
            previous = read;
        }
        Assert.All(pages, page =>
        {
            Assert.Contains("萨卡兹枯朽辟路前锋", page.Names);
            Assert.False(page.EndsAtBottom);
        });
        Assert.Contains(pages[0].Leaders,n=>n.Contains("萨米的意志"));
        Assert.Contains("冰爆源石虫",pages[1].Names);

        var board = File.ReadAllBytes(Path.Combine(directory, $"enemy-{attempt}-0.png"));
        var top = File.ReadAllBytes(Path.Combine(directory, $"enemy-{attempt}-3.png"));
        var scrolled = File.ReadAllBytes(Path.Combine(directory, $"enemy-{attempt}-4.png"));
        var live = board;
        int clicks = 0, scrolls = 0;
        using var connection = new GameConnectionService();
        using var scanner = new OcrScannerService(() => live, new GarrisonOcrStrategy(), new ShopReader(ocr, []),
            ocr, new(), [], getConnection: () => connection)
        {
            RoundEnemyReader = reader, EconomyReader = new(ocr),
            SendCapturedClick = (_, _, _, _) => { live = ++clicks == 1 ? top : board; return ClickResult.Sent; },
            RoundEnemyScroll = (_, _, _, _) =>
            {
                // The recording ends on this page. Further wheel input has no recorded frame.
                live = scrolled;
                return ++scrolls == 1;
            }
        };
        var hud = new ShopEconomy(14, 0, 0, false, "规程等级4", true);
        var tracker = (ShopEconomyTracker)typeof(OcrScannerService).GetField("_economy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scanner)!;
        tracker.Observe(hud);
        tracker.Observe(hud);
        Assert.Null(scanner.RequestRoundEnemyRead());
        Task Observe(byte[] frame) => scanner.ObserveRoundEnemiesAsync(frame, hud, true, connection,
            scanner.RunRevision, connection.CaptureRevision, Environment.TickCount64);
        await Observe(board);
        Assert.Equal(1, clicks);
        await Observe(top);
        Assert.Equal(2, scrolls);
        Assert.True(scanner.CurrentRoundEnemies!.IsPartial);
        Assert.Contains("冰爆源石虫", scanner.CurrentRoundEnemies.Names);
    }

    [Fact]
    public async Task FifthRoundCapturedOrdinaryListReturnsWithoutScrolling()
    {
        if (Environment.GetEnvironmentVariable("ASPASSISTANT_ORDINARY_REPLAY") is not { } directory) return;
        using var ocr = new MaaOcrEngine(Path.Combine(AppContext.BaseDirectory, "data"));
        var routes = await ASPAssistant.Core.Data.GarrisonRoundRouteStore.LoadAsync(GarrisonRoundRoutesTests.DataDirectory);
        var hud = new ShopEconomy(5, 0, 0, false, "规程等级13", true);
        var board = File.ReadAllBytes(Path.Combine(directory, "board.png"));
        foreach (var image in new[] { "capture", "attachment" })
        {
            var panel = File.ReadAllBytes(Path.Combine(directory, image + ".png"));
            var live = board;
            using var connection = new GameConnectionService();
            int clicks = 0, scrolls = 0, confirmations = 0;
            using var scanner = new OcrScannerService(() =>
            {
                if (clicks == 2 && live == board) confirmations++;
                return live;
            }, new GarrisonOcrStrategy(), new ShopReader(ocr, []), ocr, new(), [], getConnection: () => connection)
            {
                RoundEnemyReader = new(ocr, routes), EconomyReader = new(ocr),
                RoundEnemyScroll = (_, _, _, _) => { scrolls++; return false; },
                SendCapturedClick = (_, _, _, _) => { live = ++clicks == 1 ? panel : board; return ClickResult.Sent; }
            };
            var tracker = (ShopEconomyTracker)typeof(OcrScannerService).GetField("_economy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scanner)!;
            tracker.Observe(hud);
            tracker.Observe(hud);
            Assert.Null(scanner.RequestRoundEnemyRead());
            Task Observe(byte[] frame) => scanner.ObserveRoundEnemiesAsync(frame, hud, true, connection,
                scanner.RunRevision, connection.CaptureRevision, Environment.TickCount64);
            await Observe(board);
            await Observe(panel);
            var info = scanner.CurrentRoundEnemies!;
            Assert.Equal(["萨卡兹子裔战士", "萨卡兹子裔轰击者", "萨卡兹子裔责罚者"], info.Names);
            Assert.True(info.HasCompleteList, image + " " + info.Hint);
            Assert.Equal(0, scrolls);
            Assert.Equal(2, clicks);
            Assert.Equal(1, confirmations);
        }
    }

    [Fact]
    public async Task FourthRoundCapturedBountyPages()
    {
        if (Environment.GetEnvironmentVariable("ASPASSISTANT_ENEMY_PAGES") is not { } directory) return;
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        using var ocr = new MaaOcrEngine(data);
        var routes = await ASPAssistant.Core.Data.GarrisonRoundRouteStore.LoadAsync(GarrisonRoundRoutesTests.DataDirectory);
        var reader = new RoundEnemyReader(ocr, routes, new HashSet<string> { "遮天面包机" });
        var state = new RoundEnemyState();
        state.Observe(4);
        foreach (var page in new[] { "open", "changed", "top", "bottom" })
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, page + ".png"));
            using var stream = new MemoryStream(bytes);
            using var frame = new Bitmap(stream);
            var layout = RoundEnemyReader.Inspect(frame, true);
            var read = await reader.ReadAsync(bytes, frame, layout, state.CoveredPage, "规程等级13", 4);
            Assert.NotNull(read);
            state.ApplyRead(read);
            File.AppendAllText(Path.Combine(TestOutput.Directory, "round-enemy-pages.txt"),
                $"{page} {layout} names={string.Join("、",read.Names)} bounty={string.Join("、",read.Bounties)} top={read.StartsAtTop} bottom={read.EndsAtBottom} unread={read.HasUnreadRows} anchor={read.UnreadTailAnchor} hint={state.Info!.Hint}\n");
        }
        Assert.Equal(["萨卡兹刀兵", "萨卡兹大剑手", "萨卡兹大剑组长"], state.Info!.Names);
        Assert.Equal(["遮天面包机"], state.Info.Bounties);
        Assert.True(state.Info.HasCompleteList);

        byte[] Load(string name) => File.ReadAllBytes(Path.Combine(directory, name + ".png"));
        var board = Load("board");
        var top = Load("top");
        var bottom = Load("bottom");
        var live = Load("changed");
        var hud = new ShopEconomy(4, 0, 0, false, "规程等级13", true);
        using var connection = new GameConnectionService();
        int clicks = 0, verifiedFrames = 0;
        RoundEnemyState? request = null;
        using var scanner = new OcrScannerService(() =>
        {
            if (ReferenceEquals(live, board)) verifiedFrames++;
            return live;
        }, new GarrisonOcrStrategy(), new ShopReader(ocr, []), ocr, new(), [], getConnection: () => connection)
        {
            RoundEnemyReader = reader, EconomyReader = new(ocr),
            RoundEnemyScroll = (_, _, _, _) =>
            {
                Assert.True(request!.ExpireManual(DateTime.MaxValue));
                live = bottom;
                return true;
            },
            SendCapturedClick = (_, point, _, _) =>
            {
                Assert.Equal(new Point(945, 32), point);
                if (++clicks == 2) live = board;
                return ClickResult.Sent;
            }
        };
        request = (RoundEnemyState)typeof(OcrScannerService).GetField("_roundEnemyState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scanner)!;
        var economy = (ShopEconomyTracker)typeof(OcrScannerService).GetField("_economy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scanner)!;
        economy.Observe(hud);
        economy.Observe(hud);
        Assert.Null(scanner.RequestRoundEnemyRead());
        Task Observe(byte[] frame) => scanner.ObserveRoundEnemiesAsync(frame, hud, true, connection, scanner.RunRevision,
            connection.CaptureRevision, Environment.TickCount64);
        await Observe(board);
        Assert.True(clicks == 1, $"board clicks={clicks}, state={scanner.CurrentRoundEnemies}");
        await Observe(Load("open"));
        Assert.True(scanner.CurrentRoundEnemies!.IsPartial, $"opening frame: clicks={clicks}, state={scanner.CurrentRoundEnemies}");
        live = top;
        await Observe(top);
        Assert.Same(bottom, live);
        Assert.Equal(1, clicks);
        await Observe(bottom);
        await Observe(board);
        Assert.True(scanner.CurrentRoundEnemies!.HasCompleteList);
        Assert.Equal(state.Info.Names, scanner.CurrentRoundEnemies.Names);
        Assert.Equal(["遮天面包机"], scanner.CurrentRoundEnemies.Bounties);
        Assert.Equal(2, clicks);
        Assert.Equal(1, verifiedFrames);
        var confirmation = Assert.Single(RecognitionDiagnostics.Samples.Freeze(),
            sample => sample.Frames.Any(f => ReferenceEquals(f.Image, board) && f.Step.EndsWith("已返回棋盘")));
        File.AppendAllLines(Path.Combine(TestOutput.Directory, "round-enemy-pages.txt"), confirmation.Actions);

    }

    [Theory]
    [InlineData(70, "upper")]
    [InlineData(179, "lower")]
    [InlineData(100, null)]
    public void BountyLaneRequiresVisibleGatesAndClearProximity(int y, string? expected)
    {
        using var board = new Bitmap(358, 202);
        using (var g = Graphics.FromImage(board))
        {
            g.FillRectangle(Brushes.Red, 65, 52, 14, 15);
            g.FillRectangle(Brushes.Red, 50, 134, 14, 15);
        }
        var rim = Color.FromArgb(135,180,180);
        board.SetPixel(65,y,Color.White);
        board.SetPixel(58,y,rim);
        board.SetPixel(72,y-2,rim);
        board.SetPixel(62,y-6,rim);
        Assert.Equal(expected, RoundEnemyReader.LocateBountyLanes(board, ["遮天面包机"]).GetValueOrDefault("遮天面包机"));
        if (Environment.GetEnvironmentVariable("ASPASSISTANT_BOUNTY_REPLAY") is { } path)
        {
            using var actual = new Bitmap(path);
            var layout = RoundEnemyReader.Inspect(actual, true);
            Assert.False(layout.Title.IsEmpty);
            Assert.InRange(layout.Button!.Value.X, 1025, 1090);
            Assert.InRange(layout.Button.Value.Y, 20, 55);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal("lower", RoundEnemyReader.LocateBountyLanes(actual, ["遮天面包机"])["遮天面包机"]);
            File.WriteAllText(Path.Combine(TestOutput.Directory, "round-enemy-bounty-replay.txt"), $"遮天面包机 lower {timer.Elapsed.TotalMilliseconds:F2} ms");
        }
    }

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
            Assert.Equal(RoundEnemyReader.Inspect(panel, true).Button, Assert.Single(clicks));
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
    [InlineData("manual-panel", 1280, "no-button")]
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
        using var panel = new Bitmap(1280, 720);
        if (lastScene != "no-button") DrawEnemyButton(panel, 255);
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
        bool observingPanel = false;
        using var scanner = new OcrScannerService(() => { captures++; return returned ? boardBytes : latestBytes; },
            new GarrisonOcrStrategy(), new ShopReader(ocr, []), ocr, new(), [], getConnection: () => connection)
        {
            RoundEnemyReader = new(ocr), EconomyReader = new(ocr), AutomaticRoundEnemies = entry == "automatic",
            RoundEnemyScroll = (_, _, _, _) => false,
            SendCapturedClick = (_, point, size, _) =>
            {
                clicks.Add(point);
                returned = observingPanel && point == RoundEnemyReader.Inspect(latest, true).Button;
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
        observingPanel = true;
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
            Assert.Equal(RoundEnemyReader.Inspect(latest, true).Button, Assert.Single(clicks));
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
    [Trait("Category", "LocalScreenshot")]
    public async Task LiveFieldSwitchFreezesTheLastSingleFrameRead()
    {
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        var maps = (await ASPAssistant.Core.Data.GarrisonBoardStore.LoadAsync(data))!;
        using var ocr = new MaaOcrEngine(data);
        using var connection = new GameConnectionService(new() { Source = ASPAssistant.Core.Models.GameCaptureSource.Adb });
        var samples = Path.Combine(AppContext.BaseDirectory, "TestData", "Board");
        byte[]? frame = File.ReadAllBytes(Path.Combine(samples, "manual-roster-round5.png"));
        int captures = 0;
        using var scanner = new OcrScannerService(() => { captures++; return frame; }, new GarrisonOcrStrategy(),
            new ShopReader(ocr, []), ocr, new(), [], getConnection: () => connection)
        {
            EconomyReader = new(ocr), BoardRecognizer = new(maps), LiveReader = new(data, maps)
        };
        Assert.False(scanner.AutomaticLiveField);
        await scanner.ScanNowAsync();
        Assert.Empty(scanner.LiveRoster.Observations);
        int before = captures;
        Assert.Contains("上场", await scanner.ReadLiveFieldAsync());
        Assert.Equal(1, captures - before);
        var manual = scanner.LiveRoster.Observations.Last();
        Assert.Equal(5, manual.Round);
        frame = File.ReadAllBytes(Path.Combine(samples, "manual-roster-round6.png"));
        var tracker = (ShopEconomyTracker)typeof(OcrScannerService).GetField("_economy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scanner)!;
        var (width, height) = ImageBytes.GetDimensions(frame);
        var hud = await scanner.EconomyReader.ReadPreparationAsync(frame, width, height);
        Assert.Equal(6, hud.Round);
        tracker.Observe(hud);
        tracker.Observe(hud);
        await scanner.ScanNowAsync();
        Assert.Same(manual, scanner.LiveRoster.Observations.Last());
        scanner.AutomaticLiveField = true;
        await scanner.ScanNowAsync();
        var automatic = scanner.LiveRoster.Observations.Last();
        Assert.Equal(6, automatic.Round);
        scanner.AutomaticLiveField = false;
        await scanner.ScanNowAsync();
        Assert.Same(automatic, scanner.LiveRoster.Observations.Last());
        frame = null;
        before = captures;
        Assert.Contains("未取得", await scanner.ReadLiveFieldAsync());
        Assert.Equal(1, captures - before);
        Assert.Same(automatic, scanner.LiveRoster.Observations.Last());
    }

    [Theory]
    [Trait("Category", "LocalScreenshot")]
    [InlineData("C5")]
    [InlineData("B7-hidden")]
    [InlineData("B7-neighbor")]
    [InlineData("C5-unknown")]
    public async Task CityFieldReviewUsesConfirmedSlotAndSavesFailedClicks(string scenario)
    {
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        var maps = (await ASPAssistant.Core.Data.GarrisonBoardStore.LoadAsync(data))!;
        var dir = Path.Combine(AppContext.BaseDirectory, "TestData", "Board");
        var board = File.ReadAllBytes(Path.Combine(dir, "city-expanded-a6-174304.png"));
        var detail = File.ReadAllBytes(Path.Combine(dir, "city-c5-detail-1750.png"));
        if (scenario == "C5-unknown")
        {
            using var stream = new MemoryStream(detail);
            using var bitmap = new Bitmap(stream);
            using (var g = Graphics.FromImage(bitmap)) g.FillRectangle(Brushes.Black,bitmap.Width*.38f,0,bitmap.Width*.62f,bitmap.Height);
            detail = ImageBytes.Encode(bitmap);
        }
        using var ocr = new MaaOcrEngine(data);
        using var connection = new GameConnectionService(new() { Source = ASPAssistant.Core.Models.GameCaptureSource.Adb });
        var live = board;
        var clicks = new List<Point>();
        RecognitionSample? saved = null;
        using var scanner = new OcrScannerService(() => live, new GarrisonOcrStrategy(), new ShopReader(ocr, []), ocr, new(), [], getConnection: () => connection)
        {
            EconomyReader = new(ocr), LiveReader = new(data,maps), BoardRecognizer = new(maps),
            FieldTiers = new Dictionary<string,int> { ["卡达"]=1, ["奥尔佳"]=3, ["烈夏"]=1, ["地灵"]=1, ["陨星"]=1, ["“暴躁铁皮”"]=5 },
            SaveFieldReview = (_, sample) => saved = sample,
            SendCapturedClick = (_, point, _, _) =>
            {
                clicks.Add(point);
                if (scenario != "B7-hidden") live = clicks.Count == 1 ? detail : board;
                return ClickResult.Sent;
            }
        };
        Assert.Contains("上场 6 人", await scanner.ReadLiveFieldAsync());
        var slot = scenario.StartsWith("B7") ? new FieldSlot(4,1) : new FieldSlot(2,2);
        var unit = scanner.LiveRoster.Units.Single(u => u.Mark.Slot == slot);
        var result = await scanner.ReadLiveFieldAsync(unit);
        if (scenario == "C5")
        {
            Assert.Contains("已核实为卡达", result);
            Assert.Contains("已返回棋盘", result);
            Assert.Equal(2, clicks.Count);
            Assert.Null(saved);
            return;
        }
        Assert.NotNull(saved);
        Assert.Contains(saved.Frames, f => f.Step == "点击前" && f.Image.Length > 0);
        Assert.Contains(saved.Frames, f => f.Step.Contains(slot.ToString()) && f.Image.Length > 0);
        Assert.Contains(saved.Actions, a => a.Contains("点击 {X="));
        Assert.Contains(scenario switch { "B7-hidden" => "panel-hidden", "B7-neighbor" => "focus=C5", _ => "focus=unknown" }, saved.Result);
        Assert.Equal(scenario == "B7-hidden" ? 2 : 1, clicks.Count);
        Assert.Equal(clicks.Count, clicks.Distinct().Count());
        Assert.DoesNotContain(scanner.LiveRoster.Units, u => u.Mark.Slot == new FieldSlot(4,1) && u.Name == "卡达");
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
        state.ApplyRead(new(["步兵", "源石虫"], [], false, true, false, "敌人", 0));
        Assert.True(state.Info!.HasCompleteList);
        Assert.Equal(new[] { "宿主士兵", "步兵", "源石虫" }, state.Info.Names);
        Assert.Equal("磨砻", Assert.Single(state.Info.Bounties));
    }

    [Fact]
    public void FourthRoundEnemyPagePreservesBountyFromFirstPage()
    {
        var state = new RoundEnemyState();
        state.Observe(4);
        state.ApplyRead(new(["萨卡兹刀兵", "萨卡兹大剑手"], ["遮天面包机"], true, false, false, "敌人", 0));
        state.ApplyRead(new(["萨卡兹刀兵", "萨卡兹大剑手", "萨卡兹大剑组长"], [], true, true, false, "敌人", 0));
        Assert.True(state.Info!.HasCompleteList);
        Assert.Equal(["遮天面包机"], state.Info.Bounties);
        Assert.Equal(3, state.Info.RouteNames.Count);
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
    [InlineData(false, false, true, "same-panel")]
    [InlineData(true, false, true, "same-panel")]
    [InlineData(false, false, true, "player-closed")]
    [InlineData(false, false, true, "capture-lost")]
    public async Task ScrollsOwnedPanelsOnlyAndRequiresAnOverlappingStableBottom(bool adb, bool passive, bool moves, string? expiry = null)
    {
        using var top = new Bitmap(1280, 720);
        using (var g = Graphics.FromImage(top))
        {
            DrawEnemyButton(top, 255);
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
        using var board = new Bitmap(1280, 720);
        DrawEnemyButton(board, 255);
        var boardBytes = ImageBytes.Encode(board);
        RoundEnemyState? state = null;
        int scrolls = 0, closes = 0, opens = 0, returnedFrames = 0;
        using var scanner = new OcrScannerService(() => { if (live == boardBytes) returnedFrames++; return live; }, new GarrisonOcrStrategy(), new ShopReader(ocr, []),
            ocr, new(), [], getConnection: () => connection)
        {
            RoundEnemyReader = new(ocr), EconomyReader = new(ocr), AutomaticRoundEnemies = false,
            RoundEnemyScroll = (target, point, size, revision) =>
            {
                Assert.Equal(adb, target.IsAdb);
                Assert.Equal(connection.CaptureRevision, revision);
                Assert.True(RoundEnemyReader.Inspect(top, false).Names.Contains(point));
                scrolls++;
                if (expiry != null) Assert.True(state!.ExpireManual(DateTime.MaxValue));
                if (moves) live = last;
                return true;
            },
            SendCapturedClick = (_, point, _, _) =>
            {
                Assert.Equal(RoundEnemyReader.Inspect(board, true).Button, point);
                if (expiry != null && opens == 0) opens++;
                else { closes++; live = boardBytes; }
                return ClickResult.Sent;
            }
        };
        var tracker = (ShopEconomyTracker)typeof(OcrScannerService).GetField("_economy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scanner)!;
        tracker.Observe(hud);
        tracker.Observe(hud);
        if (!passive) Assert.Null(scanner.RequestRoundEnemyRead());
        long captureTick = Environment.TickCount64;
        Task Observe(byte[] bytes) => scanner.ObserveRoundEnemiesAsync(bytes, hud, true, connection, scanner.RunRevision,
            connection.CaptureRevision, ++captureTick);
        if (expiry != null)
        {
            state = (RoundEnemyState)typeof(OcrScannerService).GetField("_roundEnemyState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scanner)!;
            await Observe(boardBytes);
            Assert.Equal(1, opens);
        }
        await Observe(first);
        if (expiry != null)
        {
            Assert.True(scanner.CurrentRoundEnemies!.IsPartial);
            Assert.Equal(0, closes);
            if (expiry == "player-closed") await Observe(boardBytes);
            if (expiry == "capture-lost") state!.PauseConfirmationClock(captureLost: true);
            await Observe(last);
            await Observe(last);
            Assert.Equal(expiry == "same-panel" ? 1 : 0, closes);
            Assert.Equal(closes, returnedFrames);
            Assert.True(scanner.CurrentRoundEnemies!.HasCompleteList);
            Assert.Equal(1, scrolls);
            return;
        }
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
        public string Heading { get; init; } = "敌人";
        public int Reads { get; private set; }
        public byte[]? NextRoundFrame { get; init; }
        public Func<Task>? WaitForRead { get; init; }

        public async Task<IReadOnlyList<OcrTextResult>> RecognizeRegionAsync(byte[] png, int x, int y, int w, int h)
        {
            Reads++;
            if (WaitForRead is not null) await WaitForRead();
            IReadOnlyList<OcrTextResult> result = y == 0 && w > 700 ? [new($"回合{(ReferenceEquals(png, NextRoundFrame) ? hudRound + 1 : hudRound)}", (450, 26, 40, 13)), new("休息一下", (631, 24, 54, 16))]
                : x != 0 ? [new(Heading, (x + 15, y, 35, 16))]
                : !hasNames ? []
                : w < 300 ? [new("措狗pro", (20, 3, 50, 36))]
                : [new("猎狗pro", (20, 17, 76, 24))];
            return result;
        }

        public Task<IReadOnlyList<OcrTextResult>> RecognizeLineAsync(byte[] png, int x, int y, int w, int h)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<OcrTextResult>>(hasNames ? [new("猎狗pro", (x, y, w, h))] : []);
        }

        public Task<string?> FindCandidateInRegionAsync(byte[] png, int x, int y, int w, int h, IReadOnlyList<string> candidates)
            => throw new NotSupportedException();
    }
}
