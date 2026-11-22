using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ASPAssistant.App;
using ASPAssistant.App.Views;
using ASPAssistant.App.Windows;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.GameModes.GarrisonProtocol;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using Path = System.IO.Path;

namespace ASPAssistant.Tests.Views;

public partial class ThemeAndHighlightTests
{
    [Trait("Category", "LocalScreenshot")]
    [Theory]
    [InlineData(false, 400)]
    [InlineData(true, 280)]
    public Task MapPivotActionsOnRecordedBoard(bool night, int width) => ui.RunAsync(() =>
    {
        var context = SynchronizationContext.Current;
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        var store = new JsonDataStore(data);
        var operators = store.LoadOperatorsAsync().GetAwaiter().GetResult();
        var (bonds, equipmentMap) = store.LoadCovenantsAsync().GetAwaiter().GetResult();
        var calc = new CovenantCalculator(operators, bonds, equipmentMap);
        var ops = operators.ToDictionary(o => o.Name);
        var maps = GarrisonBoardStore.LoadAsync(data).GetAwaiter().GetResult()!;
        var frame = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Board", "short-field-r10.png"));
        using var ocr = new MaaOcrEngine(data);
        using var connection = new GameConnectionService(new() { Source = GameCaptureSource.Adb });
        int captures = 0;
        using var scanner = new OcrScannerService(() => { captures++; return frame; }, new GarrisonOcrStrategy(),
            new ShopReader(ocr, []), ocr, new(), [], getConnection: () => connection)
        {
            EconomyReader = new(ocr), BoardRecognizer = new(maps), LiveReader = new(data, maps),
            FieldTiers = ops.ToDictionary(p => p.Key, p => p.Value.Tier)
        };
        var tmp = Path.Combine(Path.GetTempPath(), "map-pivot-" + Guid.NewGuid().ToString("N"));
        var tracking = new TrackingViewModel { ManualTerminalLevel = 4 };
        var bans = new BanViewModel();
        var planner = new LineupPlannerViewModel(new(operators, bonds), operators, bans, calc.EquipmentRules);
        planner.SetBossContext("假想敌：茧", 20);
        var library = new LineupViewModel(new LineupStore(Path.Combine(tmp, "lineups"), calc.EquipmentRules), calc);
        Lineup Team(string name, params string[] members) => new() { Name = name, Slots = members.Select(n => new LineupSlot { OperatorName = n }).ToList() };
        var old = Team("原阵容", "夜莺", "山");
        var next = Team("抓牌转型", "蛇屠箱", "灰喉", "华法琳", "维伊");
        library.LoadLineups([old, next]);
        tracking.TrackLineup(old);
        var holdings = new DeployPoolState();
        holdings.SetCount("灰喉", 1);
        holdings.SetElite("灰喉", true);
        var host = new SidePanelWindow(new OperatorBrowseViewModel(), new EquipmentBrowseViewModel(), tracking,
            new GameStateViewModel(), bans, new EnemyViewModel(), library, new LineupShareService(calc.EquipmentRules, tmp),
            operators, [], [], new SettingsManager(tmp), GameShopTheme.Closure,
            new SidePanelPositionSettings { FreeMovement = true }, covenants: bonds)
            { Width = width, Height = 1000, ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000 };
        var trackingView = (TrackingView)host.FindName("TrackingView");
        var view = trackingView.DeployPage;
        view.Initialize(ops, bonds, maps, tracking, new SettingsManager(tmp), holdings, false);
        view.ConnectMapPlanner(planner, library);
        view.ConnectLiveField(scanner);
        T Get<T>(string n) where T : FrameworkElement => (T)view.FindName(n);
        void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Until(Func<bool> check)
        {
            var timer = Stopwatch.StartNew();
            while (!check() && timer.Elapsed < TimeSpan.FromSeconds(15)) { Pump(); Thread.Sleep(5); }
            Assert.True(check());
        }
        void Click(string name)
        {
            var button = Get<Button>(name);
            Assert.True(button.IsVisible && button.IsEnabled, name);
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
            Pump();
        }
        void Save(string page)
        {
            var transition = Stopwatch.StartNew();
            while (transition.ElapsedMilliseconds < 350) { Pump(); Thread.Sleep(10); }
            view.UpdateLayout();
            var image = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            image.Render(view);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(TestOutput.Directory, $"map-pivot-{page}-{width}-{night}.png"));
            png.Save(file);
        }
        try
        {
            ThemeManager.Apply(GameShopTheme.Closure, night);
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            host.Show(); Pump();
            ((TabControl)host.FindName("MainTabs")).SelectedIndex = 1;
            ((ListBox)trackingView.FindName("TrackingPageList")).SelectedIndex = 2; Pump();
            var timer = Stopwatch.StartNew();
            Task.Run(() => scanner.ReadLiveFieldAsync(ownBoard: true)).GetAwaiter().GetResult(); Pump();
            Get<ListBox>("ModeList").SelectedIndex = 0; Pump();
            Click("MapGuideButton");
            var guide = Application.Current.Windows.OfType<TrackingGuideWindow>().Single();
            ((TextBox)guide.FindName("Notes")).Text = "先保留过渡前排";
            Assert.True(host.IsEnabled && guide.IsVisible);
            var captured = scanner.LiveRoster;
            var place = captured.Observations[^1].Readings.Keys.First(s => !s.Bench && captured.Units.All(u => u.Mark.Slot != s));
            var canvas = Get<Canvas>("BoardCanvas");
            void Tap(FieldSlot slot)
            {
                var rect = canvas.Children.OfType<Rectangle>().Single(r => Equals(r.Tag, slot));
                rect.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                Pump();
            }
            Tap(place);
            var options = Get<WrapPanel>("CorrectionChoices");
            var grey = options.Children.OfType<Button>().Single(b => Equals(b.Content, "灰喉"));
            grey.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Assert.Contains("定位到", Get<Button>("ConfirmCorrectionButton").Content.ToString());
            Save("locate");
            if (night)
            {
                Task.Run(() => scanner.ReadLiveFieldAsync(ownBoard: true)).GetAwaiter().GetResult();
                Pump();
            }
            Click("ConfirmCorrectionButton");
            Assert.Equal("灰喉", scanner.LiveRoster.Units.Single(u => u.Mark.Slot == place).Name);
            Assert.Equal(1, holdings.CountOf("灰喉"));
            Assert.True(holdings.IsElite("灰喉"));
            Assert.False(scanner.PlaceMissedUnit(captured, place, "夜莺"));
            var pending = scanner.LiveRoster.Units.Single(u => u.Mark.Slot.ToString() == "D7");
            Tap(pending.Mark.Slot);
            Until(() => options.Children.OfType<Button>().Any(b => Equals(b.Content, "蛇屠箱")));
            options.Children.OfType<Button>().Single(b => Equals(b.Content, "蛇屠箱")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Save("review");
            Click("ConfirmCorrectionButton");
            Assert.Equal("蛇屠箱", scanner.LiveRoster.Units.Single(u => u.Mark.Slot == pending.Mark.Slot).Name);
            Assert.Equal(1, holdings.CountOf("蛇屠箱"));
            Click("LeaveCorrectionButton");
            var nextSlot = captured.Observations[^1].Readings.Keys.First(s => !s.Bench && scanner.LiveRoster.Units.All(u => u.Mark.Slot != s));
            holdings.SetCount("维伊", 1);
            Tap(nextSlot);
            var wei = options.Children.OfType<Button>().Single(b => Equals(b.Content, "维伊"));
            wei.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Click("CountOnlyButton");
            Assert.Equal(2, holdings.CountOf("维伊"));
            Assert.DoesNotContain(scanner.LiveRoster.Units, u => u.Mark.Slot == nextSlot);
            int requestedReads = night ? 2 : 1;
            Assert.Equal(requestedReads, captures);
            Get<ListBox>("ModeList").SelectedIndex = 2; Pump();
            var before = timer.ElapsedMilliseconds;
            Click("MapSwitchButton");
            var list = Get<ListBox>("PivotChoices");
            Until(() => list.Items.Count > 0);
            var searchMilliseconds = timer.ElapsedMilliseconds - before;
            Assert.Contains(list.Items.Cast<MapPivotChoice>(), c => c.Source == "规划");
            list.SelectedItem = list.Items.Cast<MapPivotChoice>().Single(c => c.Lineup.Id == next.Id);
            Pump(); Save("choices");
            var text = string.Join("\n", Get<StackPanel>("PivotDetails").Children.OfType<TextBlock>().Select(t => t.Text));
            Assert.Contains("原阵容持有", text); Assert.Contains("灰喉", ((MapPivotChoice)list.SelectedItem).Held);
            Assert.Contains("停止提醒", text); Assert.Contains("夜莺", text);
            Click(night ? "PivotAppendButton" : "PivotReplaceButton");
            Until(() => !Get<Border>("PivotPanel").IsVisible);
            Assert.Equal(next.Id, tracking.CurrentMapLineup!.Id);
            Assert.Equal(night, tracking.IsLineupTracked(old.Id));
            Assert.Equal(2, holdings.CountOf("维伊")); Assert.True(holdings.IsElite("灰喉"));
            Assert.Equal(2, Get<ListBox>("ModeList").SelectedIndex);
            Assert.Equal(requestedReads, captures);
            Until(() => view.CurrentPlan is { } plan && plan.Placed.Any(p => p.Name == "灰喉") && plan.Placed.Any(p => p.Name == "维伊"));
            Save("switched");
            var readButton = Get<Button>("ReadFieldButton");
            Click("ReadFieldOptions");
            Assert.True(readButton.ContextMenu.IsOpen);
            Assert.Equal(requestedReads, captures);
            readButton.ContextMenu.IsOpen = false;
            readButton.ContextMenu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "识别我的棋盘并同步"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Until(() => readButton.IsEnabled && Get<Border>("CorrectionPanel").IsVisible);
            Click("LeaveCorrectionButton");
            Assert.Equal(2, Get<ListBox>("ModeList").SelectedIndex);
            Task.Run(() => scanner.ReadLiveFieldAsync()).GetAwaiter().GetResult(); Pump();
            Assert.True(guide.IsVisible);
            Assert.Contains(next.Name, ((TextBlock)guide.FindName("Following")).Text);
            Assert.Equal("先保留过渡前排", ((TextBox)guide.FindName("Notes")).Text);
            var save = guide.SaveAsync(false);
            Until(() => save.IsCompleted); save.GetAwaiter().GetResult();
            Assert.Equal("先保留过渡前排", library.Lineups.Single(l => l.Id == old.Id).EditorStages!.Single(s => s.IsFinal).OperationIdea);
            Assert.Equal(next.Id, tracking.CurrentMapLineup!.Id);
            Assert.False(scanner.PlaceMissedUnit(captured, nextSlot, "灰喉"));
            Assert.False(scanner.PlaceMissedUnit(scanner.LiveRoster, nextSlot, "灰喉"));
            File.WriteAllText(Path.Combine(TestOutput.Directory, $"map-pivot-timing-{width}.txt"),
                $"candidate search and WPF rendering: {searchMilliseconds} ms; locate, count and switch: 0 additional captures");
        }
        finally { host.Close(); SynchronizationContext.SetSynchronizationContext(context); }
    }, $"map-pivot-{night}");
}
