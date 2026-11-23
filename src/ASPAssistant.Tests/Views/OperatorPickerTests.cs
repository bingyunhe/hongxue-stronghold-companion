using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Reflection;
using ASPAssistant.App;
using ASPAssistant.App.Views;
using ASPAssistant.App.Windows;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.GameModes.GarrisonProtocol;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

internal static class OperatorPickerTests
{
    internal static void VerifyRankedCorrection(bool night, bool quick = false)
    {
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        var maps = GarrisonBoardStore.LoadAsync(data).GetAwaiter().GetResult()!;
        var frame = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Board", quick ? "short-field-r10.png" : "city-round9-four-marks.png"));
        using var ocr = new MaaOcrEngine(data);
        using var connection = new GameConnectionService(new() { Source = GameCaptureSource.Adb });
        int captures = 0;
        var saved = new System.Collections.Concurrent.ConcurrentQueue<RecognitionSample>();
        using var scanner = new OcrScannerService(() => { captures++; return frame; }, new GarrisonOcrStrategy(),
            new ShopReader(ocr, []), ocr, new(), [], getConnection: () => connection)
        {
            SaveFieldReview = (_, sample) => saved.Enqueue(sample),
            EconomyReader = new(ocr), BoardRecognizer = new(maps), LiveReader = new(data, maps),
            FieldTiers = new JsonDataStore(data).LoadOperatorsAsync().GetAwaiter().GetResult()
                .GroupBy(o => o.Name).ToDictionary(g => g.Key, g => g.First().Tier)
        };
        Task.Run(() => scanner.ReadLiveFieldAsync()).GetAwaiter().GetResult();
        if (quick)
        {
            ThemeManager.Apply(GameShopTheme.Closure, night);
            var deploy = new DeployView();
            var holdings = new DeployPoolState();
            var tracking = new TrackingViewModel();
            tracking.AddTracking("蛇屠箱", TrackingType.Operator);
            deploy.Initialize(new JsonDataStore(data).LoadOperatorsAsync().GetAwaiter().GetResult().ToDictionary(op=>op.Name),
                [], maps, tracking, new SettingsManager(Path.Combine(Path.GetTempPath(), "quick-live-review")), holdings, false);
            deploy.ConnectLiveField(scanner);
            var owner = new Window { Content = deploy, Width = 400, Height = 550, Left = -20000, Top = -20000,
                WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
            var target=scanner.LiveRoster.Units.Single(u=>u.Mark.Slot.ToString()=="D7");
            var timer=System.Diagnostics.Stopwatch.StartNew();
            var first=scanner.GetLiveReviewCandidates(target);
            var firstMs=timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            Assert.Same(first,scanner.GetLiveReviewCandidates(target));
            var cachedMs=timer.Elapsed.TotalMilliseconds;
            Assert.Contains("蛇屠箱",first);
            scanner.SetReviewExclusions(new HashSet<string> { "蛇屠箱" });
            Assert.DoesNotContain("蛇屠箱", scanner.GetLiveReviewCandidates(target));
            Assert.False(scanner.ConfirmLiveReviewName(target, "蛇屠箱"));
            scanner.SetReviewExclusions(null);
            Assert.Contains("蛇屠箱", scanner.GetLiveReviewCandidates(target));
            Assert.Equal(1,captures);
            Task.Run(() => scanner.ReadLiveFieldAsync()).GetAwaiter().GetResult();
            Assert.False(scanner.ConfirmLiveReviewName(target,"蛇屠箱"));
            target=scanner.LiveRoster.Units.Single(u=>u.Mark.Slot.ToString()=="D7");
            var tile=(Border)typeof(DeployView).GetMethod("LiveTile",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(deploy,[target])!;
            var menu=tile.ContextMenu;
            try
            {
                owner.Show(); menu.PlacementTarget=deploy; menu.IsOpen=true;
                var deadline=System.Diagnostics.Stopwatch.StartNew();
                MenuItem? item;
                do
                {
                    Pump();
                    item=menu.Items.OfType<MenuItem>().FirstOrDefault(i=>Equals(i.Header,"确认蛇屠箱"));
                    if (item==null) Thread.Sleep(5);
                } while (item==null && deadline.ElapsedMilliseconds<10000);
                Assert.NotNull(item);
                var shown = menu.Items.OfType<MenuItem>().Select(i => i.Header as string)
                    .Where(h => h?.StartsWith("确认") == true).Select(h => h![2..]).ToArray();
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.True(SpinWait.SpinUntil(() => !saved.IsEmpty, 2000));
                var sample = Assert.Single(saved);
                Assert.Equal(shown, sample.Correction!.Candidates);
                Assert.Equal(("D7", "右键·快捷确认", "蛇屠箱"), (sample.Correction.Slot, sample.Correction.Entry, sample.Correction.Chosen));
                Assert.Same(frame, Assert.Single(sample.Frames).Image);
                Assert.Equal("蛇屠箱",scanner.LiveRoster.Units.Single(u=>u.Id==target.Id).Name);
                Assert.False(scanner.LiveRoster.Observations[^1].PersonalBoard);
                Assert.Empty(holdings.Owned);
                Assert.Equal(2,captures);
                menu.IsOpen = false;
                var actionButton = (Button)deploy.FindName("ReadFieldButton");
                var ownMenu = actionButton.ContextMenu;
                ownMenu.PlacementTarget = actionButton;
                ownMenu.IsOpen = true;
                Pump();
                ownMenu.UpdateLayout();
                var preview = new RenderTargetBitmap((int)Math.Ceiling(ownMenu.ActualWidth), (int)Math.Ceiling(ownMenu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                preview.Render(ownMenu);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(preview));
                using (var output = File.Create(Path.Combine(TestOutput.Directory,$"own-board-menu-{night}.png"))) encoder.Save(output);
                var context = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(owner.Dispatcher));
                try { ownMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "识别我的棋盘并同步")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
                finally { SynchronizationContext.SetSynchronizationContext(context); }
                ownMenu.IsOpen = false;
                deadline.Restart();
                while (!actionButton.IsEnabled && deadline.Elapsed < TimeSpan.FromSeconds(10)) { Pump(); Thread.Sleep(5); }
                Assert.True(actionButton.IsEnabled);
                Assert.True(scanner.LiveRoster.Observations[^1].PersonalBoard);
                Assert.Equal(3,captures);
                var ownUnit = scanner.LiveRoster.Units.Single(u => u.Mark.Slot.ToString() == "D7");
                Assert.False(scanner.SetLiveIdentity(target, "杰克"));
                Assert.True(scanner.SetLiveIdentity(ownUnit, "蛇屠箱"));
                Assert.Equal(1, holdings.CountOf("蛇屠箱"));
                var entry = tracking.TrackedOperators.Single(e => e.Name == "蛇屠箱");
                Assert.True(entry.IsAcquired);
                Assert.Equal("已完成", tracking.OperatorProgress(entry));
                deploy.ApplyRecognizedPurchases(["蛇屠箱"], scanner.LiveRoster.Observations[^1].Frame.Time, 10);
                Assert.Equal(1, holdings.CountOf("蛇屠箱"));
                Task.Run(() => scanner.ReadLiveFieldAsync()).GetAwaiter().GetResult();
                Assert.False(scanner.LiveRoster.Observations[^1].PersonalBoard);
                Assert.Equal(4,captures);
                File.WriteAllText(Path.Combine(TestOutput.Directory,$"quick-review-{night}.txt"),
                    $"D7 {string.Join(' ',first)}; menu {firstMs:F2} ms; cached {cachedMs:F3} ms; manual reads 4; captures {captures}; own board D7 synced; unconfirmed co-op ignored");
            }
            finally { menu.IsOpen=false;owner.Close(); }
            return;
        }
        var pickerNames = scanner.GetLiveReviewCandidates(scanner.LiveRoster.Units.Single(u => u.Mark.Slot == new FieldSlot(4,0)));
        VerifyLiveCorrection(night, scanner);
        Assert.True(SpinWait.SpinUntil(() => !saved.IsEmpty, 2000));
        var chosen = Assert.Single(saved);
        Assert.Equal("夜莺", chosen.Correction!.Chosen);
        Assert.Equal("右键·更换干员", chosen.Correction.Entry);
        Assert.Equal(pickerNames, chosen.Correction.Candidates);
        Assert.Same(frame, Assert.Single(chosen.Frames).Image);
        var corrected = scanner.LiveRoster.Units.Single(u => u.Mark.Slot == new FieldSlot(4, 0));
        Assert.Equal("夜莺", corrected.Name);
        Assert.Empty(scanner.GetLiveReviewCandidates(corrected));
        Assert.Equal(1, captures);
    }

    internal static void VerifyLiveCorrection(bool night, OcrScannerService? scanner = null)
    {
        ThemeManager.Apply(GameShopTheme.Closure, night);
        var data = Path.Combine(AppContext.BaseDirectory, "TestData", "Regulation");
        var operators = new JsonDataStore(data).LoadOperatorsAsync().GetAwaiter().GetResult()
            .GroupBy(o => o.Name).ToDictionary(g => g.Key, g => g.First());
        var deploy = new DeployView();
        var holdings = new DeployPoolState();
        deploy.Initialize(operators, [], GarrisonBoardStore.LoadAsync(data).GetAwaiter().GetResult(), new TrackingViewModel(),
            new SettingsManager(Path.Combine(Path.GetTempPath(), "live-picker-tests")), holdings, false);
        var area = SystemParameters.WorkArea;
        var owner = new Window { Content = deploy, Width = 280, Height = 500, Left = area.Right - 280, Top = area.Top,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        owner.Show();
        var mark = new FieldMark(new(2, 1), 4, default, [], null);
        if (scanner != null) deploy.ConnectLiveField(scanner);
        var unit = scanner is null ? new LiveUnit(mark, 1, null, null, false, false)
            : scanner.LiveRoster.Units.Single(u => u.Mark.Slot == new FieldSlot(4, 0));
        Assert.Null(unit.Name);
        var tile = (Border)typeof(DeployView).GetMethod("LiveTile", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(deploy, [unit])!;
        var change = tile.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "更换干员并记住"));
        Exception? failure = null;
        try
        {
            owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                var picker = Assert.Single(owner.OwnedWindows.Cast<Window>().OfType<OperatorPickerWindow>());
                try
                {
                    Assert.InRange(picker.Left + picker.ActualWidth, area.Left, area.Right + 1);
                    var filters = (OperatorBrowseViewModel)picker.DataContext;
                    var list = (ListBox)picker.FindName("OperatorsList");
                    if (scanner != null)
                    {
                        Assert.StartsWith("外观候选　奥尔佳", ((TextBlock)picker.FindName("TitleMeta")).Text);
                        Assert.Equal("奥尔佳", ((Operator)list.Items[0]).Name);
                        Assert.Null(list.SelectedItem);
                        SendEnter(list, (FrameworkElement)picker.Content);
                        Assert.True(picker.IsVisible);
                        Assert.Null(picker.Result);
                        Assert.Null(scanner.LiveRoster.Units.Single(u => u.Id == unit.Id).Name);
                        SavePicker(picker, $"live-review-ranking-Closure-{night}.png");
                    }
                    filters.TierChips.Single(c => c.Value == "4").IsSelected = true;
                    filters.SearchText = "夜莺";
                    picker.UpdateLayout();
                    var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                    Assert.Equal("夜莺", ((Operator)row.Content).Name);
                    if (scanner == null) SavePicker(picker, $"live-correction-Closure-{night}.png");
                    RaiseMouse(row, Mouse.PreviewMouseDownEvent);
                    RaiseMouse(row, Mouse.MouseUpEvent);
                    Assert.Equal("夜莺", picker.Result);
                }
                catch (Exception ex) { failure = ex; }
                finally { if (picker.IsVisible) picker.Close(); }
            });
            change.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Null(failure);
            if (scanner != null)
            {
                Assert.True(scanner.LiveRoster.Observations[^1].PersonalBoard);
                Assert.Equal(1, holdings.CountOf("夜莺"));
            }
            Assert.Contains(scanner == null ? "已移动" : "本局已记住", ((TextBlock)deploy.FindName("LiveMessage")).Text);
        }
        finally { owner.Close(); }
    }

    internal static void VerifyRunHoldings(bool night)
    {
        ThemeManager.Apply(GameShopTheme.Closure, night);
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        var store = new JsonDataStore(data);
        var ops = store.LoadOperatorsAsync().GetAwaiter().GetResult().ToDictionary(o => o.Name);
        var (bonds, _) = store.LoadCovenantsAsync().GetAwaiter().GetResult();
        var holdings = new DeployPoolState();
        holdings.SetCount("角峰", 1);
        var tracking = new TrackingViewModel();
        tracking.AddTracking("耶拉", TrackingType.Operator);
        tracking.AddTracking("折桠", TrackingType.Operator);
        var deploy = new DeployView();
        deploy.Initialize(ops, bonds, GarrisonBoardStore.LoadAsync(data).GetAwaiter().GetResult(), tracking,
            new SettingsManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())), holdings, false);
        var owner = new Window { Content = deploy, Width = 400, Height = 600, ShowInTaskbar = false, ShowActivated = false };
        Button ButtonNamed(string name) => (Button)deploy.FindName(name);
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Open(Button button, Action<OperatorPickerWindow, OperatorBrowseViewModel> inspect)
        {
            Exception? failure = null;
            owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                var picker = Assert.Single(owner.OwnedWindows.OfType<OperatorPickerWindow>());
                try { inspect(picker, (OperatorBrowseViewModel)picker.DataContext); }
                catch (Exception ex) { failure = ex; }
                finally { if (picker.IsVisible) picker.Close(); }
            });
            Click(button);
            Assert.Null(failure);
        }
        try
        {
            owner.Show(); Pump();
            var record = ButtonNamed("RecordHoldingButton");
            Open(record, (_, vm) => Assert.Contains(vm.OtherChips, c => c.Value == "奇迹"));
            string[] active = ["炎", "伊比利亚", "乌萨斯", "谢拉格"];
            string[] greyCore = ["维多利亚", "萨尔贡", "莱塔尼亚", "叙拉古"];
            string[] extra = ["精准", "迅捷", "助力", "不屈", "调和", "协防干员", "聚合", "奥术", "坚守", "远见"];
            var missing = bonds.Select(c => c.Name).Except(active).Except(greyCore).Except(extra).ToHashSet();
            var grey = greyCore.Concat(["坚守", "远见"]).ToHashSet();
            var run = new BanScreenResult(active.Concat(greyCore).Select(c => new CovenantBanState(c, grey.Contains(c), 0)).ToList(),
                extra.Select(c => new CovenantBanState(c, grey.Contains(c), 0)).ToList(), null, missing);
            deploy.SetRunCovenants(run, ops.Values.Where(o => CovenantBanRules.HasMissingCovenant(o, missing)
                || o.RequiredStrategyId.Length == 0 && CovenantBanRules.IsExcluded(o, grey)).Select(o => o.Name));
            Open(record, (picker, vm) =>
            {
                Assert.Equal(active.Order(), vm.AvailableCoreCovenants.Skip(1));
                Assert.Equal(extra.Order(), vm.AvailableOtherCovenants.Skip(1));
                Assert.DoesNotContain(vm.FilteredOperators, o => o.Name == "耶拉");
                var chip = vm.OtherChips.Single(c => c.Value == "坚守");
                chip.IsSelected = true;
                Assert.Equal(vm.FilteredOperators.Count, chip.Count);
                Assert.Contains(vm.FilteredOperators, o => o.Name == "角峰");
                vm.ResetFilters();
                Assert.DoesNotContain(vm.OtherChips, c => c.Value == "奇迹");
                chip.IsSelected = true;
                picker.UpdateLayout();
                SavePicker(picker, $"run-holdings-Closure-{night}.png");
                vm.SearchText = "角峰";
                PressEnter((ListBox)picker.FindName("OperatorsList"), (FrameworkElement)picker.Content);
            });
            Click(ButtonNamed("CountOnlyButton"));
            Assert.Equal(2, holdings.CountOf("角峰"));
            var change = typeof(DeployView).GetMethod("ChangeCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
            holdings.SetCount("耶拉", 1);
            change.Invoke(deploy, ["耶拉", 1]);
            Assert.Equal(1, holdings.CountOf("耶拉"));
            change.Invoke(deploy, ["耶拉", -1]);
            Assert.Equal(0, holdings.CountOf("耶拉"));
            var bulk = Descendants<Button>(deploy).Single(b => Equals(b.Content, "本阶段已获取"));
            Click(bulk);
            Assert.Equal(0, holdings.CountOf("耶拉"));
            Assert.Equal(1, holdings.CountOf("折桠"));
            Open(Descendants<Button>(deploy).Single(b => Equals(b.Content, "＋ 添加")), (_, vm) =>
            {
                Assert.Contains(vm.OtherChips, c => c.Value == "坚守");
                Assert.DoesNotContain(vm.OtherChips, c => c.Value == "奇迹");
                Assert.DoesNotContain(vm.FilteredOperators, o => o.Name == "怒潮凛冬");
            });
            Open(record, (picker, _) =>
            {
                deploy.SetRunCovenants(run with { CoreCovenants = run.CoreCovenants.Select(c => c with { IsBanned = !c.IsBanned }).ToList() }, ["角峰"]);
                Assert.False(picker.IsVisible);
            });
            Open(record, (_, vm) =>
            {
                Assert.DoesNotContain(vm.CoreChips, c => c.Value == "谢拉格");
                Assert.DoesNotContain(vm.FilteredOperators, o => o.Name == "角峰");
            });
            Open(record, (picker, _) =>
            {
                deploy.SetRunCovenants(null, []);
                Assert.False(picker.IsVisible);
            });
            Open(record, (_, vm) => Assert.Contains(vm.FilteredOperators, o => o.Name == "耶拉"));
        }
        finally { owner.Close(); }
    }

    private static void SavePicker(OperatorPickerWindow picker, string file)
    {
        var surface = (FrameworkElement)picker.Content;
        var view = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        view.Render(surface);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(view));
        using var output = File.Create(Path.Combine(TestOutput.Directory, file));
        png.Save(output);
    }

    public static void VerifySelection()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
        try
        {
            var operators = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "TestData", "Regulation"))
                .LoadOperatorsAsync().GetAwaiter().GetResult();
            foreach (var name in new[] { "褐果", "拉普兰德", "夏栎", "安哲拉", "复奏" })
            {
                var picker = new OperatorPickerWindow(operators, "", new HashSet<string>())
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
                    ShowActivated = false, ShowInTaskbar = false, Opacity = 0
                };
                try
                {
                    var list = (ListBox)picker.FindName("OperatorsList");
                    list.Height = 261;
                    picker.Show();
                    var content = (FrameworkElement)picker.Content;
                    content.Measure(new Size(778, 698));
                    content.Arrange(new Rect(0, 0, 778, 698));
                    content.UpdateLayout();
                    var scroll = Descendants<ScrollViewer>(list).First();
                    var index = list.Items.Cast<Operator>().ToList().FindIndex(op => op.Name == name);
                    list.ScrollIntoView(list.Items[index]);
                    Pump();
                    var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
                    row.BringIntoView();
                    Pump();
                    var point = row.TranslatePoint(new Point(100, 10), list);
                    var hit = VisualTreeHelper.HitTest(list, point).VisualHit;
                    ((Operator)((ListBoxItem)ItemsControl.ContainerFromElement(list, hit)).Content).Name.Should().Be(name);
                    RaiseMouse((UIElement)hit, Mouse.PreviewMouseDownEvent);
                    var pressedOffset = scroll.VerticalOffset;

                    // Scroll one row between press and release; row height changes with the picker layout.
                    list.SelectedItem = row.Content;
                    row.BringIntoView();
                    scroll.ScrollToVerticalOffset(pressedOffset + row.ActualHeight + row.Margin.Top + row.Margin.Bottom);
                    Pump();
                    hit = VisualTreeHelper.HitTest(list, point).VisualHit;
                    ((Operator)((ListBoxItem)ItemsControl.ContainerFromElement(list, hit)).Content).Name.Should().Be(((Operator)list.Items[index + 2]).Name,
                        "the same screen point now hits the following grid row after scrolling");
                    RaiseMouse((UIElement)hit, Mouse.MouseUpEvent);
                    picker.Result.Should().Be(name, "scrolling between press and release must preserve the clicked operator");
                }
                finally { picker.Close(); }
            }

            foreach (var strategySelected in new[] { false, true })
            foreach (var useKeyboard in new[] { false, true })
            {
                var picker = new OperatorPickerWindow(operators, "",
                    strategySelected ? new HashSet<string> { "band_mech" } : new HashSet<string>());
                try
                {
                    var filters = (OperatorBrowseViewModel)picker.DataContext;
                    filters.SearchText = "暴躁铁皮";
                    var list = (ListBox)picker.FindName("OperatorsList");
                    var content = (FrameworkElement)picker.Content;
                    content.Measure(new Size(778, 698));
                    content.Arrange(new Rect(0, 0, 778, 698));
                    content.UpdateLayout();
                    var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                    if (useKeyboard)
                        PressEnter(list, content);
                    else
                    {
                        RaiseMouse(row, Mouse.PreviewMouseDownEvent);
                        RaiseMouse(row, Mouse.MouseUpEvent);
                    }
                    picker.Result.Should().Be(strategySelected ? LineupEquipmentService.MechName : null);
                }
                finally { picker.Close(); }
            }

            foreach (var useKeyboard in new[] { false, true })
            {
                var picker = new OperatorPickerWindow(operators, "", new HashSet<string>());
                try
                {
                    var filters = (OperatorBrowseViewModel)picker.DataContext;
                    filters.SearchText = "拉普兰德";
                    filters.SelectedTierFilters = [1];
                    var list = (ListBox)picker.FindName("OperatorsList");
                    var content = (FrameworkElement)picker.Content;
                    content.Measure(new Size(778, 698));
                    content.Arrange(new Rect(0, 0, 778, 698));
                    content.UpdateLayout();
                    list.Items.Cast<Operator>().Select(op => op.Name).Should().Equal("拉普兰德");
                    var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                    if (useKeyboard)
                    {
                        PressEnter(list, content);
                    }
                    else
                    {
                        // Releasing over a row without a row press (e.g. after dragging the scrollbar) is ignored.
                        RaiseMouse(row, Mouse.MouseUpEvent);
                        picker.Result.Should().BeNull();
                        RaiseMouse(Descendants<ScrollBar>(list).First(bar => bar.Orientation == Orientation.Vertical), Mouse.PreviewMouseDownEvent);
                        RaiseMouse(row, Mouse.MouseUpEvent);
                        picker.Result.Should().BeNull("a scrollbar press must not choose an operator");
                        RaiseMouse(row, Mouse.PreviewMouseDownEvent);
                        RaiseMouse(list, Mouse.MouseUpEvent);
                        picker.Result.Should().BeNull("releasing outside a row cancels the click");
                        RaiseMouse(row, Mouse.MouseUpEvent);
                        picker.Result.Should().BeNull("a cancelled press must not leak into a later release");
                        RaiseMouse(row, Mouse.PreviewMouseDownEvent);
                        RaiseMouse(row, Mouse.MouseUpEvent);
                    }
                    picker.Result.Should().Be("拉普兰德");
                }
                finally { picker.Close(); }
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    internal static void RaiseMouse(UIElement source, RoutedEvent routedEvent)
        => source.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = routedEvent });

    private static void PressEnter(ListBox list, FrameworkElement content)
    {
        list.SelectedItem = list.Items[0];
        SendEnter(list, content);
    }

    private static void SendEnter(ListBox list, FrameworkElement content)
    {
        list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new PickerPresentationSource(content),
            Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) yield return result;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private sealed class PickerPresentationSource(Visual root) : PresentationSource
    {
        public override Visual RootVisual { get; set; } = root;
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget? GetCompositionTargetCore() => null;
    }
}
