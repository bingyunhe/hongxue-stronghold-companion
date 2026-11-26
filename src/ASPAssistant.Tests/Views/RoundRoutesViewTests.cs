using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Polyline = System.Windows.Shapes.Polyline;
using ASPAssistant.App;
using ASPAssistant.App.Views;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using ASPAssistant.Tests.Services;

namespace ASPAssistant.Tests.Views;

internal static class RoundRoutesViewTests
{
    internal static void VerifyDesertOverlay(bool night)
    {
        if (Environment.GetEnvironmentVariable("ASPASSISTANT_DESERT_REPLAY") is not { } dir) return;
        ThemeManager.Apply(GameShopTheme.Closure, night);
        var maps = GarrisonBoardStore.LoadAsync(GarrisonRoundRoutesTests.DataDirectory).GetAwaiter().GetResult()!;
        var imagePath = Path.Combine(dir,"enemy-2-0.png");
        var pixels = BmpPixels.TryRead(ImageBytes.ToBmp(File.ReadAllBytes(imagePath)))!;
        var match = new BoardRecognizer(maps).RecognizePannedDesertOverlay(pixels)!;
        var camera = match.OverlayProjection!;
        var overlay = new ASPAssistant.App.Windows.OverlayWindow();
        try
        {
            var canvas = (Canvas)overlay.FindName("OverlayCanvas");
            overlay.Left = overlay.Top = -20000; overlay.Opacity = 0; overlay.Topmost = false;
            overlay.Width = 1600; overlay.Height = 900;
            overlay.Show(); overlay.UpdateLayout();
            overlay.Measure(new Size(1600,900)); overlay.Arrange(new Rect(0,0,1600,900));
            overlay.UpdateBoard(match.Map,camera,true,true);
            var plan = new PlacementPlan { Map = match.Map, ActiveCovenants = [], Checks = [], Bench = [],
                Placed = [new("摆渡人",new(2,0),Facing.Right,PlacementRole.Damage,false,[],"摆渡人")] };
            overlay.UpdatePlacements(plan);
            var layer = (Canvas)canvas.Children[0];
            int gridAndPlan = layer.Children.Count;
            overlay.UpdateBoard(match.Map,camera,false,true);
            Assert.NotEmpty(layer.Children.OfType<Polyline>());
            Assert.DoesNotContain(layer.Children.OfType<TextBlock>(), t => t.Text is "地" or "高" or "特");
            Assert.Single(layer.Children.OfType<Border>());
            overlay.UpdatePlacements(null);
            Assert.NotEmpty(layer.Children.OfType<Polyline>());
            Assert.Empty(layer.Children.OfType<Border>());
            overlay.UpdateBoard(match.Map,camera,false,false);
            Assert.Empty(layer.Children);
            overlay.UpdatePlacements(plan);
            overlay.UpdateBoard(match.Map,camera,true,false);
            Assert.Empty(layer.Children.OfType<Polyline>());
            Assert.Contains(layer.Children.OfType<TextBlock>(), t => t.Text is "地" or "高" or "特");
            overlay.UpdateBoard(match.Map,camera,false,false);
            int onlyPlan = layer.Children.Count;
            Assert.True(gridAndPlan > onlyPlan && onlyPlan >= 3);
            overlay.UpdateBoard(null,null,false,false);
            Assert.Empty(layer.Children);
            overlay.UpdateBoard(match.Map,camera,true,true);
            Assert.Equal(gridAndPlan,layer.Children.Count);
            canvas.Measure(new Size(1600,900)); canvas.Arrange(new Rect(0,0,1600,900)); canvas.UpdateLayout();
            var transparent = new RenderTargetBitmap(1600,900,96,96,PixelFormats.Pbgra32);
            transparent.Render(canvas);
            var visual = new DrawingVisual();
            using (var ctx = visual.RenderOpen())
            {
                ctx.DrawImage(new BitmapImage(new Uri(imagePath)),new Rect(0,0,1600,900));
                ctx.DrawImage(transparent,new Rect(0,0,1600,900));
            }
            var result = new RenderTargetBitmap(1600,900,96,96,PixelFormats.Pbgra32);
            result.Render(visual);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(result));
            using var output = File.Create(Path.Combine(TestOutput.Directory,$"desert-overlay-{night}.png")); png.Save(output);
        }
        finally { overlay.Close(); }
    }

    internal static void VerifyPalaceOverlay()
    {
        var maps = GarrisonBoardStore.LoadAsync(GarrisonRoundRoutesTests.DataDirectory).GetAwaiter().GetResult()!;
        var map = maps.Find("palace")!;
        var window = new ASPAssistant.App.Windows.OverlayWindow { Width = 1280, Height = 720,
            Left = -20000, Top = -20000, Opacity = 0, Topmost = false, ShowActivated = false };
        try
        {
            window.Show();
            var canvas = (Canvas)window.FindName("OverlayCanvas");
            var projection = maps.Cameras[BoardCamera.Expanded];
            window.UpdateBoard(map, projection, false, false);
            window.UpdatePlacements(new PlacementPlan { Map = map, ActiveCovenants = [], Checks = [], Bench = [],
                Placed = [new("承曦格雷伊",new(1,1),Facing.Right,PlacementRole.Damage,false,[],"承曦格雷伊"),
                          new("波登可",new(1,2),Facing.Right,PlacementRole.Support,false,[],"波登可")] });
            var layer = (Canvas)canvas.Children[0];
            Assert.Equal(6, layer.Children.Count);
            Assert.Equal(2, layer.Children.OfType<Border>().Count());
            if (Environment.GetEnvironmentVariable("ASPASSISTANT_PALACE_OVERLAY_FRAME") is not { } file) return;
            canvas.Measure(new Size(1280,720)); canvas.Arrange(new Rect(0,0,1280,720)); canvas.UpdateLayout();
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawImage(new BitmapImage(new Uri(file)), new Rect(0,0,1280,720));
                context.DrawRectangle(new VisualBrush(canvas) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0,0,1280,720) },
                    null, new Rect(0,0,1280,720));
            }
            var result = new RenderTargetBitmap(1280,720,96,96,PixelFormats.Pbgra32);
            result.Render(visual);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(result));
            using var output = File.Create(Path.Combine(TestOutput.Directory,"palace-plan-only.png")); png.Save(output);
        }
        finally { window.Close(); }
    }

    internal static void VerifyLiveRecommendations(bool night)
    {
        ThemeManager.Apply(GameShopTheme.Closure, night);
        var source = GarrisonRoundRoutesTests.DataDirectory;
        var maps = GarrisonBoardStore.LoadAsync(source).GetAwaiter().GetResult();
        var operators = new JsonDataStore(source).LoadOperatorsAsync().GetAwaiter().GetResult()
            .GroupBy(o => o.Name).ToDictionary(g => g.Key, g => g.First());
        var manual = new DeployPoolState { Owned = ["灰喉", "砾", "烈夏", LineupEquipmentService.MechName], Extra = ["灰喉"] };
        var deploy = new DeployView();
        var dir = Path.Combine(Path.GetTempPath(), $"live-plans-{Guid.NewGuid():N}");
        deploy.Initialize(operators, [], maps, new TrackingViewModel(), new SettingsManager(dir), manual, false);
        var host = new Window { Content = deploy, Width = 330, Height = 590, Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            host.Show();
            var modes = (ListBox)deploy.FindName("ModeList");
            modes.SelectedIndex = 0;
            var field = new FieldMark(new(1, 3), 1, new(400, 330), [], null);
            var bench = field with { Slot = new(3, -1) };
            var units = new LiveUnit[] { new(field, 1, "灰喉", null, false, false),
                new(bench, 2, null, null, false, false), new(field with { Slot = new(3,3) }, 3, "杰克", null, false, false),
                new(field with { Slot = new(4,2), Tier = 5 }, 4, LineupEquipmentService.MechName, null, false, false),
                new(field with { Slot = new(4,1) }, 5, "烈夏", null, false, false),
                new(field with { Slot = new(2,3) }, 6, "烈夏", "烈夏", false, false) };
            var evidence = units.Where(u => u.Name == "烈夏").Select(u => u with { Mark = u.Mark with { StandaloneName = "烈夏" } }).ToArray();
            var scan = new FieldObservation(Guid.NewGuid(), new(DateTimeOffset.Now, "实况", []), "desert_crates", 8, evidence, []) { PersonalBoard = true };
            new DeployOwnership(manual).Observe(new(scan, evidence));
            deploy.ShowLiveRoster(new("desert_crates", 8, units));
            for (int mode = 1; mode <= 3; mode++)
            {
                modes.SelectedIndex = mode;
                AwaitPlan(mode == 1 ? 5 : 4);
                if (mode == 1)
                {
                    units = [.. units.Select(u => u.Mark.Slot.Bench ? u with { Name = "风丸", Manual = true } : u)];
                    deploy.ShowLiveRoster(new("desert_crates", 8, units));
                    var current = deploy.CurrentPlan;
                    Pump(650);
                    Assert.Same(current, deploy.CurrentPlan);
                    Assert.Contains(current!.Placed, p => p.Name == "灰喉");
                    SetGreyThroatOwned(false);
                    AwaitPlan(4);
                }
                var plan = deploy.CurrentPlan!;
                Assert.Contains(plan.Placed, p => p.Name == LineupEquipmentService.MechName);
                Assert.Equal(2, plan.Placed.Count(p => p.Name == "烈夏"));
                var names = plan.Placed.Select(u => u.Key).Concat(plan.Bench.Select(u => u.Name)).ToList();
                Assert.DoesNotContain("杰克", names);
                Assert.DoesNotContain("灰喉", names);
                Assert.DoesNotContain("风丸", names);
                Assert.Contains("砾", names);
                Assert.NotEmpty(((System.Windows.Controls.Primitives.UniformGrid)deploy.FindName("OperatorGrid")).Children);
                host.UpdateLayout();
                var image = new RenderTargetBitmap((int)deploy.ActualWidth, (int)deploy.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                image.Render(deploy);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(Path.Combine(TestOutput.Directory, $"recommendation-{mode}-Closure-{night}.png"));
                png.Save(file);
            }
            modes.SelectedIndex = 0;
            var benchRow = (System.Windows.Controls.Primitives.UniformGrid)deploy.FindName("BenchRow");
            Assert.Contains(benchRow.Children.OfType<Border>(), tile => tile.ToolTip?.ToString()?.Contains("风丸") == true);
            modes.SelectedIndex = 1;
            AwaitPlan(4);
            SetGreyThroatOwned(true);
            AwaitPlan(5);
            Assert.Contains(deploy.CurrentPlan!.Placed,p => p.Name == "灰喉");
            modes.SelectedIndex = 0;
            deploy.ShowLiveRoster(new(null, null, []));
            deploy.SetRecognizedBoard(new(maps!.Find("desert_crates")!, BoardCamera.Expanded, 1, 1));
            Assert.Equal("自动识别地图", ((TextBlock)deploy.FindName("MapSourceText")).Text);
            modes.SelectedIndex = 3;
            AwaitPlan(5);
            Assert.DoesNotContain("杰克", manual.Owned);
        }
        finally { host.Close(); }

        void SetGreyThroatOwned(bool owned)
        {
            deploy.FocusOperator("灰喉");
            var groups = (Panel)deploy.FindName("PoolGroups");
            var card = groups.Children.OfType<System.Windows.Controls.Primitives.UniformGrid>()
                .SelectMany(grid => grid.Children.OfType<Grid>()).Single(g => g.ToolTip?.ToString()?.StartsWith("灰喉 ·") == true);
            UIElement target = card;
            if (!owned)
            {
                var dock = card.Children.OfType<DockPanel>().Single();
                target = dock.Children.OfType<Border>().Select(b => b.Child).OfType<StackPanel>()
                    .SelectMany(p => p.Children.OfType<TextBlock>()).Single(t => t.Text == "−");
            }
            target.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Assert.Equal(owned, manual.IsOwned("灰喉"));
            deploy.ShowPage(false);
        }

        void AwaitPlan(int count)
        {
            var previous = deploy.CurrentPlan;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (ReferenceEquals(previous, deploy.CurrentPlan) || deploy.CurrentPlan is not { } plan || plan.Placed.Count + plan.Bench.Count != count)
            {
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(6), $"推荐阵容未更新到 {count} 人");
                Pump(25);
            }
        }

        static void Pump(int milliseconds)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var tick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            tick.Tick += (_, _) => { tick.Stop(); frame.Continue = false; };
            tick.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
    }

    internal static void VerifyPlayback(bool night)
    {
        ThemeManager.Apply(GameShopTheme.Closure, night);
        string data = GarrisonRoundRoutesTests.DataDirectory;
        var deploy = new DeployView();
        deploy.Initialize(new Dictionary<string, Operator>(), [], GarrisonBoardStore.LoadAsync(data).GetAwaiter().GetResult(),
            new TrackingViewModel(), new SettingsManager(Path.Combine(Path.GetTempPath(), "route-playback")),
            new DeployPoolState { ManualMap = "desert_cactus" }, false, GarrisonRoundRouteStore.LoadAsync(data).GetAwaiter().GetResult());
        var surface = new Border { Child = deploy, Background = (Brush)deploy.FindResource("PanelBackgroundBrush") };
        var host = new Window { Content = surface, Width = 280, Height = 660, WindowStyle = WindowStyle.None,
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Left = -20000, Top = -20000 };
        host.Show();
        var hud = new ShopEconomy(9, 0, 0, false, "规程等级13", true);
        var card = (RoundEnemyCard)deploy.FindName("RoundEnemyPanel");
        var details = (StackPanel)deploy.FindName("RouteDetails");
        var canvas = (Canvas)deploy.FindName("BoardCanvas");
        foreach (bool flying in new[] { false, true })
        {
            var roster = flying ? new RoundEnemyInfo(9, ["妖怪", "妖怪MKII", "威龙"])
                : new RoundEnemyInfo(9, ["深池逐火战士", "深池逐火精锐战士", "深池逐火护卫"]) { Bounties = ["未收录悬赏"] };
            card.DataContext = new EnemyViewModel { RoundEnemies = roster };
            deploy.SetRoundRouteContext(hud, roster);
            host.UpdateLayout();
            int phases = flying ? 3 : 2;
            var headers = details.Children.OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains(flying ? "上口 · 12 只" : "上口 · 6 只", headers);
            Assert.Contains(flying ? "下口 · 7 只" : "下口 · 14 只", headers);
            if (!flying) Assert.Contains("悬赏 未收录悬赏（额外敌人，关卡数据未标明出怪口）", headers);
            var arrow = Assert.Single(canvas.Children.OfType<System.Windows.Shapes.Polygon>());
            for (int i = 0; i < phases; i++)
            {
                Pump(i == 0 ? 300 : 2000);
                var active = Assert.Single(details.Children.OfType<Border>(), r => r.Background is not null);
                Assert.Same(details.Children.OfType<Border>().ElementAt(i), active);
                Assert.Equal(Visibility.Visible, arrow.Visibility);
                Assert.Equal($"{i + 1} / {phases}", ((TextBlock)deploy.FindName("RoutePhaseNumber")).Text);
                var matrix = ((MatrixTransform)arrow.RenderTransform).Matrix;
                var routeLine = canvas.Children.OfType<Polyline>().First(p => ReferenceEquals(p.Tag, active.Tag));
                Assert.True((new Point(matrix.OffsetX, matrix.OffsetY) - routeLine.Points[0]).Length > 1);
                foreach (var scale in new[] { 1d, 1.25 })
                {
                    deploy.LayoutTransform = new ScaleTransform(scale, scale);
                    host.Width = 280 * scale;
                    host.Height = 660 * scale;
                    host.UpdateLayout();
                    var view = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    view.Render(surface);
                    var png = new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(view));
                    using var file = File.Create(Path.Combine(TestOutput.Directory, $"gate-{flying}-{night}-{i}-{scale}.png"));
                    png.Save(file);
                    Assert.True(active.ActualWidth <= details.ActualWidth + 1);
                }
            }
            deploy.Visibility = Visibility.Collapsed;
            Assert.False(((MatrixTransform)arrow.RenderTransform).HasAnimatedProperties);
            deploy.Visibility = Visibility.Visible;
        }
        var bounty = new RoundEnemyInfo(4, ["萨卡兹刀兵", "萨卡兹大剑手", "萨卡兹大剑组长"]) { Bounties = ["遮天面包机"] };
        deploy.SetRoundRouteContext(hud with { Round = 4 }, bounty);
        host.UpdateLayout();
        var candidates = details.Children.OfType<Border>().Where(r => r.Child is TextBlock).ToArray();
        Assert.Equal(2, candidates.Length);
        Assert.Contains("上口", ((TextBlock)candidates[0].Child).Text);
        Assert.Contains("下口", ((TextBlock)candidates[1].Child).Text);
        var played = new HashSet<string>();
        // Ordinary routes can share the same timer. Observe one full cycle.
        for (int i = 0; i <= details.Children.OfType<Border>().Count() && played.Count < 2; i++)
        {
            foreach (var row in details.Children.OfType<Border>().Where(r => r.Background != null && r.Child is TextBlock))
                played.Add(((TextBlock)row.Child).Text);
            if (played.Count < 2) Pump(2010);
        }
        Assert.True(played.Count == 2, $"已播放 {string.Join("、", played)}，地图可见 {canvas.IsVisible}，行数 {details.Children.OfType<Border>().Count()}，箭头 {canvas.Children.OfType<System.Windows.Shapes.Polygon>().Count()}");
        var bountyView = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bountyView.Render(surface);
        var bountyPng = new PngBitmapEncoder();
        bountyPng.Frames.Add(BitmapFrame.Create(bountyView));
        using (var file = File.Create(Path.Combine(TestOutput.Directory, $"bounty-routes-Closure-{night}.png"))) bountyPng.Save(file);
        deploy.SetRoundRouteContext(hud with { Round = 4 }, bounty with { BountyLanes = new Dictionary<string,string> { ["遮天面包机"] = "lower" } });
        Assert.Contains("下口", ((TextBlock)Assert.Single(details.Children.OfType<Border>(), r => r.Child is TextBlock).Child).Text);
        Assert.Contains(details.Children.OfType<TextBlock>(), t => t.Text == "上口 · 3 只");
        Assert.Contains(details.Children.OfType<TextBlock>(), t => t.Text == "下口 · 7 只");
        for (int i = 0; i <= 3; i++)
        {
            Pump(i == 0 ? 250 : 2000);
            var active = Assert.Single(details.Children.OfType<Border>(), r => r.Background != null);
            var route = Assert.IsType<GarrisonRoundRoute>(active.Tag);
            Assert.Equal(i == 2, route.Path.Flying);
            Assert.Equal($"{i % 3 + 1} / 3", ((TextBlock)deploy.FindName("RoutePhaseNumber")).Text);
            var arrow = Assert.Single(canvas.Children.OfType<System.Windows.Shapes.Polygon>());
            Assert.Equal(Visibility.Visible, arrow.Visibility);
            Assert.True(((MatrixTransform)arrow.RenderTransform).HasAnimatedProperties);
        }
        deploy.SetRoundRouteContext(hud with { Round = 10 }, null);
        Assert.Empty(details.Children);
        host.Close();

        static void Pump(int milliseconds)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
    }
    internal static void VerifyRecognitionCards(bool night)
    {
        ThemeManager.Apply(GameShopTheme.Closure, night);
        var model = new EnemyViewModel();
        var deploy = new DeployView();
        var info = new EnemyView { DataContext = model };
        var mapCard = (RoundEnemyCard)deploy.FindName("RoundEnemyPanel");
        var infoCard = (RoundEnemyCard)info.FindName("RoundEnemyPanel");
        mapCard.DataContext = model;
        var source = GarrisonRoundRoutesTests.DataDirectory;
        deploy.Initialize(new Dictionary<string, Operator>(), [], GarrisonBoardStore.LoadAsync(source).GetAwaiter().GetResult(),
            new TrackingViewModel(), new SettingsManager(Path.Combine(Path.GetTempPath(), "enemy-card-test")),
            new DeployPoolState { ManualMap = "desert_crates" }, false);
        var window = new Window { Width = 280, Height = 550, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Opacity = 0, Left = -20000, Top = -20000, Content = deploy };
        window.Show();
        var cards = new[] { (View: (FrameworkElement)deploy, Card: mapCard, Name: "map"),
            (View: (FrameworkElement)info, Card: infoCard, Name: "run") };
        var clicks = 0;
        model.RoundEnemyReadRequested += () =>
        {
            clicks++;
            model.RoundEnemies = new RoundEnemyInfo(2, []) { IsReading = true, Hint = "正在识别小怪，请保持游戏窗口可见。" };
        };
        foreach (var (view, card, name) in cards)
        {
            window.Content = view;
            var button = (Button)card.FindName("ReadButton");
            var text = (TextBlock)card.FindName("SummaryText");
            model.RoundEnemies = new(2, []);
            window.UpdateLayout();
            Assert.True(button.IsEnabled);
            button.Command.Execute(null);
            window.UpdateLayout();
            Assert.Contains("正在识别", text.Text);
            Assert.False(button.IsEnabled);
            model.RoundEnemies = new(2, ["猎狗pro"]);
            Assert.Equal("第 2 回合 · 猎狗pro", text.Text);
            model.RoundEnemies = new(14, ["宿主重装士兵", "堂皇", "“余音”"]) { Leaders = ["假想敌：管"] };
            Assert.Contains("领袖 · 假想敌：管", text.Text);
            model.RoundEnemies = new(3, ["宿主士兵", "步兵"])
            {
                Bounties = ["磨砻"], IsPartial = true, Hint = "尚未确认到底，向下滚动可继续补录。"
            };
            foreach (var scale in new[] { 1d, 1.25 })
            {
                window.Width = 280 * scale;
                view.LayoutTransform = new ScaleTransform(scale, scale);
                window.UpdateLayout();
                Assert.Contains("悬赏 · 磨砻", text.Text);
                Assert.Contains("部分识别", text.Text);
                Assert.True(button.IsVisible);
                var bounds = button.TransformToAncestor(card).TransformBounds(new Rect(button.RenderSize));
                Assert.True(bounds.Right <= card.ActualWidth + 1);
                if (ReferenceEquals(card, mapCard))
                {
                    var header = (Border)deploy.FindName("MapHeaderCard");
                    var modes = (ListBox)deploy.FindName("ModeList");
                    Assert.True(header.TranslatePoint(new Point(0, header.ActualHeight), deploy).Y <= card.TranslatePoint(new Point(), deploy).Y);
                    Assert.True(card.TranslatePoint(new Point(0, card.ActualHeight), deploy).Y <= modes.TranslatePoint(new Point(), deploy).Y);
                }
                var surface = ReferenceEquals(card, mapCard) ? view : card;
                var size = new Rect(surface.RenderSize);
                var drawing = new DrawingVisual();
                using (var dc = drawing.RenderOpen())
                {
                    dc.DrawRectangle((Brush)view.FindResource("PanelBackgroundBrush"), null, size);
                    dc.DrawRectangle(new VisualBrush(surface), null, size);
                }
                var image = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale),
                    96 * scale, 96 * scale, PixelFormats.Pbgra32);
                image.Render(drawing);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(Path.Combine(TestOutput.Directory, $"round-enemy-{name}-Closure-{night}-{scale}.png"));
                encoder.Save(stream);
            }
        }
        Assert.Equal(2, clicks);
        window.Close();
    }

    internal static void Verify(bool night)
    {
        ThemeManager.Apply(GameShopTheme.Closure, night);
        var source = GarrisonRoundRoutesTests.DataDirectory;
        var board = GarrisonBoardStore.LoadAsync(source).GetAwaiter().GetResult();
        var store = GarrisonRoundRouteStore.LoadAsync(source).GetAwaiter().GetResult();
        var deploy = new DeployView();
        var actualOps = new JsonDataStore(source).LoadOperatorsAsync().GetAwaiter().GetResult().GroupBy(o=>o.Name).ToDictionary(g=>g.Key,g=>g.First());
        deploy.Initialize(actualOps, [], board, new TrackingViewModel(),
            new SettingsManager(Path.Combine(Path.GetTempPath(), $"round-routes-{Guid.NewGuid():N}")),
            new DeployPoolState { ManualMap = "desert_crates" }, false, store);
        var panel = (Brush)deploy.FindResource("PanelBackgroundBrush");
        var surface = new Border { Background = panel, Child = deploy };
        var host = new Window { Content = surface, Width = 294, Height = 520, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, ShowActivated = false, Opacity = 0, Left = -20000, Top = -20000 };
        host.Show();
        var canvas = (Canvas)deploy.FindName("BoardCanvas");
        var status = (TextBlock)deploy.FindName("RoundRouteStatus");
        var card = (Border)deploy.FindName("BoardCard");
        var hud = new ShopEconomy(1, 4, 5, false, "规程等级7", true);
        var roster = new RoundEnemyInfo(1, ["源石虫", "普通囚犯"]);
        GarrisonRoundRoute[] Lines() => canvas.Children.OfType<Polyline>().Select(line => line.Tag)
            .OfType<GarrisonRoundRoute>().ToArray();

        deploy.SetRoundRouteContext(hud, null);
        Assert.Contains("等待敌人名单", status.Text);
        Assert.NotEmpty(Lines());
        Assert.All(Lines(), line => Assert.True(line.Candidate));
        Save("pending");

        deploy.SetRoundRouteContext(hud, new RoundEnemyInfo(1, ["误读"]));
        Assert.Contains("名单未匹配", status.Text);
        Assert.NotEmpty(Lines());
        Assert.All(canvas.Children.OfType<Polyline>(), line => Assert.NotEmpty(line.StrokeDashArray));
        Save("unmatched");

        deploy.SetRoundRouteContext(hud, roster with { IsPartial = true });
        Assert.False(Assert.Single(Lines()).Candidate);
        Assert.Contains("按怪组推算，名单待补全", status.Text);
        Save("inferred");

        deploy.SetRoundRouteContext(hud, roster);
        var confirmed = Assert.Single(Lines());
        Assert.Equal("第 1 回合 · 下路地面路线", status.Text);
        Assert.False(confirmed.Candidate);
        var rendered = Assert.Single(canvas.Children.OfType<Polyline>());
        deploy.SetRoundRouteContext(hud with { Money = 3 }, new RoundEnemyInfo(1, roster.Names.ToArray()));
        Assert.Same(rendered, Assert.Single(canvas.Children.OfType<Polyline>()));
        Save("confirmed");

        deploy.SetRoundRouteContext(hud with { Round = 2 }, roster);
        Assert.Contains("等待敌人名单", status.Text);
        Assert.NotEmpty(Lines());
        Assert.DoesNotContain(rendered, canvas.Children.OfType<Polyline>());
        Assert.All(Lines(), line => Assert.True(line.Candidate));
        Save("next-round");

        deploy.SetRoundRouteContext(hud with { Round = 14 }, null);
        var stage = (Canvas)deploy.FindName("StageRoutesCanvas");
        Assert.NotEmpty(stage.Children.OfType<Polyline>());
        Assert.All(stage.Children.OfType<Polyline>(), line => Assert.NotEmpty(line.StrokeDashArray));
        deploy.SetRoundRouteContext(hud with { Round = 14 }, new RoundEnemyInfo(14, []) { Leaders = ["假想敌：胄"], IsPartial = true });
        Assert.True(((Canvas)deploy.FindName("StageRoutesCanvas")).Children.Count > 100);
        Assert.Contains("routes/", status.ToolTip.ToString());
        Save("boss");

        deploy.SetRoundRouteContext(hud,roster);
        var modes = (ListBox)deploy.FindName("ModeList");
        modes.SelectedIndex = 0;
        var unknown = new FieldMark(new(2,1),2,new(100,200),[],null);
        var gravel = new FieldMark(new(7,-1),2,new(500,400),[],"砾");
        deploy.ShowLiveRoster(new("desert_crates",1,[new(unknown,1,null,null,false,false),new(gravel,2,"砾","砾",false,false),
            new(new(new(0,2),1,new(450,350),[],null),3,"陨星",null,false,false) { FromPurchaseList = true } ])
            { Observations = [new(Guid.Empty, new(new DateTimeOffset(2026,11,20,17,2,0,TimeSpan.FromHours(8)), "识别原帧", []), "desert_crates", 1, [], []) { UncertainSlots = [new(3,1)] }] });
        Assert.Contains(canvas.Children.OfType<Border>(), tile => tile.ToolTip?.ToString() == "B6 · 占用待确认");
        Assert.Equal("上次识别：第 1 回合 17:02", ((TextBlock)deploy.FindName("LastFieldRead")).Text);
        var automaticField = (CheckBox)deploy.FindName("AutomaticFieldToggle");
        Assert.Equal("实时识别阵容", System.Windows.Automation.AutomationProperties.GetName(automaticField));
        Assert.False(automaticField.IsChecked);
        Assert.False(((ListBox)deploy.FindName("FieldCountList")).IsEnabled);
        var bench = (System.Windows.Controls.Primitives.UniformGrid)deploy.FindName("BenchRow");
        Assert.Equal(10,bench.Children.Count);
        Assert.Contains("右起第3席",((Border)bench.Children[7]).ToolTip.ToString());
        Assert.Contains("购买与棋子外观匹配",((Border)bench.Children[7]).ToolTip.ToString());
        Assert.Contains(canvas.Children.OfType<Border>(), t => t.ToolTip?.ToString()?.Contains("购买名单唯一匹配") == true);
        Assert.Contains(canvas.Children.OfType<Border>(),t=>t.ToolTip?.ToString()?.Contains("身份待确认")==true);
        Assert.True(((Button)deploy.FindName("ReadFieldButton")).IsEnabled);
        Save("live-field");
        var hint=new FieldMemoryLocation(7,"星极",new(2,2));
        deploy.ShowLiveRoster(new("desert_crates",9,[new LiveUnit(unknown,20,null,null,false,false){Suggestion=hint}])
            {Unlocated=[hint]});
        var grid=(System.Windows.Controls.Primitives.UniformGrid)deploy.FindName("OperatorGrid");
        var row=(DockPanel)((Border)grid.Children[0]).Child;
        var button=((StackPanel)row.Children[^1]).Children.OfType<Button>().Single();
        Assert.Equal("确认星极",button.Content);
        var pending=((WrapPanel)deploy.FindName("ChecksRow")).Children.OfType<TextBlock>()
            .Single(t=>t.Text.Contains("上次 C5"));
        Assert.Contains(pending.ContextMenu.Items.OfType<MenuItem>(),item=>Equals(item.Header,"已出售，移除本局记录"));
        ((Border)grid.Children[0]).BringIntoView();
        host.UpdateLayout();
        Assert.InRange(button.TranslatePoint(new Point(),surface).Y,0,surface.ActualHeight-button.ActualHeight);
        Save("memory");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Contains("候选已变化",((TextBlock)deploy.FindName("LiveMessage")).Text);
        ((Button)deploy.FindName("ReadFieldButton")).BringIntoView();
        automaticField.IsChecked = true;
        automaticField.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Save("automatic-field");
        deploy.ShowLiveRoster(new("desert_crates",1,[
            new(new(new(0,3),1,new(450,350),[],"地灵"),8,"地灵",null,false,false),
            new LiveUnit(new(new(1,3),1,new(520,350),[],null),9,"艾丝黛尔",null,false,false) { FromPurchaseList = true }
        ]));
        var inferredTile = Assert.Single(canvas.Children.OfType<Border>(), t => t.ToolTip?.ToString()?.Contains("购买名单唯一匹配") == true);
        var tip = new ToolTip { Content = inferredTile.ToolTip, Style = (Style)deploy.FindResource(typeof(ToolTip)) };
        // ToolTips live in a separate popup. Composite its rendered pixels over the real board.
        tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        tip.Arrange(new Rect(tip.DesiredSize));
        tip.UpdateLayout();
        var popup = new System.Windows.Shapes.Rectangle { Width = tip.ActualWidth, Height = tip.ActualHeight, Fill = new VisualBrush(tip) };
        Canvas.SetLeft(popup, 2);
        Canvas.SetTop(popup, 36);
        canvas.Children.Add(popup);
        Save("live-identity");
        canvas.Children.Remove(popup);
        modes.SelectedIndex = 2;
        Assert.True(((ListBox)deploy.FindName("FieldCountList")).IsEnabled);
        Assert.DoesNotContain(canvas.Children.OfType<Border>(),t=>t.ToolTip?.ToString()?.Contains("身份待确认")==true);

        var text = ((SolidColorBrush)status.Foreground).Color;
        var cardColor = ((SolidColorBrush)card.Background).Color;
        var panelColor = ((SolidColorBrush)panel).Color;
        double alpha = cardColor.A / 255d;
        var background = Color.FromRgb(
            (byte)Math.Round(cardColor.R * alpha + panelColor.R * (1 - alpha)),
            (byte)Math.Round(cardColor.G * alpha + panelColor.G * (1 - alpha)),
            (byte)Math.Round(cardColor.B * alpha + panelColor.B * (1 - alpha)));
        double a = Luminance(text), b = Luminance(background);
        double ratio = (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
        Assert.True(ratio >= 4.5, $"Route label contrast is {ratio:F2}");
        File.WriteAllText(Path.Combine(TestOutput.Directory, $"contrast-round-routes-Closure-{night}.json"),
            JsonSerializer.Serialize(new { RouteStatus = ratio }));

        void Save(string label)
        {
            foreach (var scale in new[] { 1d, 1.25 })
            {
                deploy.LayoutTransform = new ScaleTransform(scale, scale);
                host.Width = 294 * scale;
                host.Height = 520 * scale;
                host.UpdateLayout();
                var caption = (WrapPanel)deploy.FindName("MapCaption");
                var sourceText = (TextBlock)deploy.FindName("MapSourceText");
                if (caption.IsVisible)
                    Assert.InRange(sourceText.TranslatePoint(new Point(sourceText.ActualWidth, 0), caption).X, 0, caption.ActualWidth + 1);
                var image = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                image.Render(surface);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(Path.Combine(TestOutput.Directory, $"round-routes-Closure-{night}-{label}-{scale}.png"));
                encoder.Save(file);
            }
        }

        static double Luminance(Color c)
        {
            static double Linear(byte value) => value / 255d <= .04045 ? value / 255d / 12.92
                : Math.Pow((value / 255d + .055) / 1.055, 2.4);
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
    }
}
