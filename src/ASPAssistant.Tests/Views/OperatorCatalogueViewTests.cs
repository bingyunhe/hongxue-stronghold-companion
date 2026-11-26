using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ASPAssistant.App;
using ASPAssistant.App.Controls;
using ASPAssistant.App.Views;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

internal static class OperatorCatalogueViewTests
{
    public static void VerifyMissingCovenants(bool night)
    {
        ThemeManager.Apply(GameShopTheme.Closure, night);
        var store = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "data"));
        var operators = store.LoadOperatorsAsync().GetAwaiter().GetResult();
        var vm = new OperatorBrowseViewModel();
        vm.LoadOperators(operators);
        var view = new OperatorBrowseView { DataContext = vm };
        var host = new Window { Content = view, Width = 350, Height = 840, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        var picker = new ASPAssistant.App.Windows.OperatorPickerWindow(operators, "", new HashSet<string>()) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            host.Show();
            view.FilterToggle.IsChecked = true;
            void Check(FrameworkElement surface, OperatorBrowseViewModel filters, string name)
            {
                surface.UpdateLayout();
                var core = filters.CoreChips.Last();
                var additional = filters.OtherChips.Last();
                Assert.Equal(("无核心盟约", 50), (core.Label, core.Count));
                Assert.Equal(("无附加盟约", 25), (additional.Label, additional.Count));
                var button = Desc<System.Windows.Controls.Primitives.ToggleButton>(surface).Single(b => b.DataContext == core && b.IsVisible);
                button.BringIntoView();
                button.IsChecked = true;
                Assert.Equal(50, filters.MatchedCount);
                filters.SearchText = "空弦";
                filters.OtherChips.Single(c => c.Value == "精准").IsSelected = true;
                Assert.Equal("空弦", Assert.Single(filters.FilteredOperators).Name);
                Assert.Equal(1, core.Count);
                surface.UpdateLayout();
                Save(surface, GameShopTheme.Closure, night, name);
                filters.ResetFilters();
                additional.IsSelected = true;
                Assert.Equal(25, filters.MatchedCount);
                filters.CoreChips.Single(c => c.Value == "谢拉格").IsSelected = true;
                Assert.Contains(filters.FilteredOperators, o => o.Name == "哈洛德");
                core.IsSelected = true;
                Assert.Empty(filters.FilteredOperators);
                Assert.False(filters.CoreChips.Single(c => c.Value == "谢拉格").IsSelected);
                filters.OtherChips.Single(c => c.Value == "精准").IsSelected = true;
                Assert.False(additional.IsSelected);
                filters.ResetFilters();
                Assert.Equal(143, filters.MatchedCount);
            }
            Check(view, vm, "missing-catalogue");
            vm.SetAvailableCovenants(["炎", "精准"]);
            vm.SetCovenantBanChecker(c => c == "乌萨斯");
            vm.OnlyCurrentRun = true;
            vm.CoreChips.Last().IsSelected = true;
            Assert.Contains(vm.FilteredOperators, o => o.Name == "空弦");
            Assert.DoesNotContain(vm.FilteredOperators, o => o.Name == "烈夏");
            vm.SetBanChecker(n => n == "灰喉");
            vm.SelectedAvailabilityFilter = AvailabilityFilter.Unavailable;
            Assert.Equal("灰喉", Assert.Single(vm.FilteredOperators).Name);
            Assert.Equal(1, vm.CoreChips.Last().Count);
            vm.ResetFilters();
            vm.ClearCovenantHighlights();
            vm.SetBanChecker(_ => false);
            picker.Show();
            Check((FrameworkElement)picker.Content, (OperatorBrowseViewModel)picker.DataContext, "missing-picker");
            var custom = store.LoadCustomOperatorsAsync().GetAwaiter().GetResult();
            foreach (var op in custom) op.IsSelectedCustom = true;
            vm.LoadOperators(custom);
            Assert.Equal(43, vm.CoreChips.Last().Count);
            Assert.Equal(32, vm.OtherChips.Last().Count);
        }
        finally { picker.Close(); host.Close(); }
    }

    public static void VerifyMissingCovenantLists(bool night)
    {
        ThemeManager.Apply(GameShopTheme.Closure, night);
        var store = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "data"));
        var operators = store.LoadOperatorsAsync().GetAwaiter().GetResult();
        var (covenants, _) = store.LoadCovenantsAsync().GetAwaiter().GetResult();
        var planner = new LineupPlannerViewModel(new(operators, covenants), operators, new BanViewModel(), EquipmentTestData.Rules);
        var planning = new LineupPlannerView { DataContext = planner };
        var host = new Window { Content = planning, Width = 340, Height = 840, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        var custom = store.LoadCustomOperatorsAsync().GetAwaiter().GetResult();
        var manage = new ASPAssistant.App.Windows.CustomOperatorSelectionWindow(custom) { ShowActivated = false, ShowInTaskbar = false };
        void Layout(FrameworkElement element)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            element.UpdateLayout();
        }
        void Toggle(FrameworkElement surface, string label)
        {
            Layout(surface);
            var button = Desc<System.Windows.Controls.Primitives.ToggleButton>(surface)
                .Single(b => b.IsVisible && b.DataContext is FilterChip chip && chip.Label == label);
            button.BringIntoView();
            button.IsChecked = button.IsChecked != true;
            Layout(surface);
        }
        try
        {
            host.Show();
            planner.ShowGoalPageCommand.Execute(null);
            Toggle(planning, "无核心盟约");
            Assert.Equal(48, planner.LockSuggestions.Count);
            planner.LockSearchText = "空弦";
            Assert.Equal("空弦", Assert.Single(planner.LockSuggestions).Name);
            Assert.Empty(planner.LockedOperators);
            Layout(planning);
            planning.LockSearchBox.BringIntoView();
            Layout(planning);
            Save(planning, GameShopTheme.Closure, night, "missing-manual");
            planner.ShowAutoPageCommand.Execute(null);
            Toggle(planning, "无附加盟约");
            Assert.Empty(planner.LockSuggestions);
            Toggle(planning, "无核心盟约");
            planner.LockSearchText = "烈夏";
            Assert.Equal("烈夏", Assert.Single(planner.LockSuggestions).Name);
            Layout(planning);
            Save(planning, GameShopTheme.Closure, night, "missing-auto");

            var deploy = new DeployView();
            host.Content = deploy;
            var pool = new DeployPoolState { Extra = ["烈夏", "空弦"] };
            pool.SetCount("空弦", 1);
            deploy.Initialize(operators.ToDictionary(o => o.Name), covenants, null, new TrackingViewModel(),
                new SettingsManager(TestOutput.Directory), pool, false);
            deploy.ShowPage(true);
            Toggle(deploy, "无附加盟约");
            var animation = System.Diagnostics.Stopwatch.StartNew();
            while (animation.ElapsedMilliseconds < 400) { Layout(deploy); Thread.Sleep(10); }
            Save(deploy, GameShopTheme.Closure, night, "missing-pool-result");
            Assert.Equal(1, deploy.PoolCovenantFilters.Items.Cast<FilterChip>().Last().Count);
            Assert.Contains(Desc<TextBlock>(deploy.PoolGroups), t => t.Text == "烈夏");
            Assert.DoesNotContain(Desc<TextBlock>(deploy.PoolGroups), t => t.Text == "空弦");
            deploy.PoolFilterList.SelectedIndex = 1;
            Layout(deploy);
            Assert.Equal(0, deploy.PoolCovenantFilters.Items.Cast<FilterChip>().Last().Count);
            Save(deploy, GameShopTheme.Closure, night, "missing-pool");

            manage.Show();
            manage.FilterToggle.IsChecked = true;
            Toggle(manage, "无附加盟约");
            Assert.Equal(32, manage.ChoicesPanel.Children.Cast<Border>().Count(c => c.IsVisible));
            var selected = manage.ChoicesPanel.Children.Cast<Border>().First(c => c.IsVisible);
            var selectedCheck = Desc<CheckBox>(selected).Single();
            selectedCheck.IsChecked = true;
            Toggle(manage, "无核心盟约");
            Assert.True(manage.EmptyHint.IsVisible);
            Toggle(manage, "无附加盟约");
            Assert.Equal(43, manage.ChoicesPanel.Children.Cast<Border>().Count(c => c.IsVisible));
            Assert.Equal(true, selectedCheck.IsChecked);
            Save((FrameworkElement)manage.Content, GameShopTheme.Closure, night, "missing-custom");
        }
        finally { manage.Close(); host.Close(); }
    }

    public static void Verify(GameShopTheme theme, bool night)
    {
        ThemeManager.Apply(theme, night);
        var store = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "data"));
        var operators = store.LoadOperatorsAsync().GetAwaiter().GetResult();
        var custom = store.LoadCustomOperatorsAsync().GetAwaiter().GetResult();
        operators.AddRange(custom);
        var pith = custom.Single(o => o.Name == "Pith(卫戍协议)");
        pith.IsSelectedCustom = true;
        var vm = new OperatorBrowseViewModel { SelectedOperatorSource = OperatorSourceFilter.Default, SearchText = PlannerStrategies.PithName };
        vm.LoadOperators(operators);
        var tracking = new TrackingViewModel();
        var view = new OperatorBrowseView { DataContext = vm, IsTrackedCheck = tracking.IsTracked };
        view.TrackingRequested += (name, type) => tracking.AddTracking(name, type);
        view.UntrackingRequested += tracking.RemoveTracking;
        var host = new Window { Content = view, Width = 360, Height = 850, Opacity = 0, ShowActivated = false,
            ShowInTaskbar = false, Left = -20000, Top = -20000, WindowStyle = WindowStyle.None };
        host.Show();
        try
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            view.UpdateLayout();
            var support = vm.FilteredOperators.Single();
            support.Name.Should().Be(PlannerStrategies.PithName);
            var card = Desc<OperatorCard>(view).Single(c => ((Operator)c.DataContext).Name == support.Name);
            ((Button)card.FindName("TrackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            tracking.IsTracked(support.Name).Should().BeTrue();
            Save(view, theme, night, "support");
            var details = new ContentControl { Content = support,
                ContentTemplate = (DataTemplate)Application.Current.FindResource("OperatorDetailsTemplate") };
            var tooltip = new ToolTip { Content = details, DataContext = support };
            tooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            tooltip.Arrange(new Rect(tooltip.DesiredSize));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            details.UpdateLayout();
            details.ActualWidth.Should().BeGreaterThanOrEqualTo(464);
            Desc<TextBlock>(details).Select(t => t.Text).Should().Contain(support.Name);
            Desc<TextBlock>(details).SelectMany(t => t.Inlines.OfType<Run>()).Select(r => r.Text)
                .Should().Contain("战术咏唱·双型");
            Save(details, theme, night, "support-details");

            ((ListBox)view.FindName("OperatorSourceList")).SelectedValue = OperatorSourceFilter.Custom;
            ((TextBox)view.FindName("SearchBox")).Text = "Pith";
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            view.UpdateLayout();
            vm.FilteredOperators.Should().Equal(pith);
            Desc<OperatorCard>(view).Should().Contain(c => ((Operator)c.DataContext).Name == pith.Name);
            pith.AdditionalCovenants.Should().Equal("协防干员");
            pith.Normal.Skill.Should().BeNull();
            Save(view, theme, night, "custom-pith");
        }
        finally { host.Close(); }
    }

    public static void VerifyLayouts(bool night)
    {
        ThemeManager.Apply(GameShopTheme.Closure, night);
        var vm = new OperatorBrowseViewModel { SelectedOperatorSource = OperatorSourceFilter.Default };
        var operators = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "data")).LoadOperatorsAsync().GetAwaiter().GetResult();
        vm.LoadOperators(operators);
        var view = new OperatorBrowseView { DataContext = vm };
        string? tracked = null, banned = null;
        view.TrackingRequested += (name, _) => tracked = name;
        view.BanToggleRequested += (name, _) => banned = name;
        var host = new Window { Content = view, Width = 400, Height = 580, WindowStyle = WindowStyle.None,
            ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        var button = (Button)view.FindName("LayoutButton");
        void Layout() { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); view.UpdateLayout(); }
        void Cycle() { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(); }
        try
        {
            host.Show();
            Layout();
            var tierRow = Desc<ItemsControl>(view).Single(c => ReferenceEquals(c.ItemsSource, vm.TierChips));
            var filters = (ToggleButton)view.FindName("FilterToggle");
            Assert.True(tierRow.IsVisible);
            Assert.False(filters.IsChecked);
            var source = (ListBox)view.FindName("OperatorSourceList");
            source.SelectedValue = OperatorSourceFilter.Custom;
            Layout();
            Assert.All(vm.TierChips, chip => Assert.Equal(0, chip.Count));
            source.SelectedValue = OperatorSourceFilter.Default;
            Layout();
            Assert.Equal(6, tierRow.Items.Count);
            foreach (var tier in new[] { 5, 6 })
            {
                var chip = vm.TierChips.Single(c => c.Value == tier.ToString());
                var tile = Desc<ToggleButton>(tierRow).Single(b => ReferenceEquals(b.DataContext, chip));
                tile.IsChecked = true;
                Layout();
                Assert.Equal(operators.Count(op => op.Tier == tier), vm.MatchedCount);
                Assert.Equal(vm.MatchedCount, chip.Count);
                Assert.All(vm.FilteredOperators, op => Assert.Equal(tier, op.Tier));
                filters.IsChecked = true;
                Layout();
                Assert.True(tierRow.IsVisible);
                Assert.Empty(Desc<ItemsControl>((Border)view.FindName("FilterContentPanel"))
                    .Where(c => ReferenceEquals(c.ItemsSource, vm.TierChips)));
                filters.IsChecked = false;
                tile.IsChecked = false;
                Layout();
            }
            for (int columns = 1; columns <= 3; columns++)
            {
                Layout();
                var cards = Desc<OperatorCard>(view).ToList();
                Assert.InRange(cards.Count, columns, vm.MatchedCount - 1);
                var first = cards[0];
                var location = first.TranslatePoint(new Point(), view);
                for (int i = 0; i < columns; i++)
                {
                    Assert.Equal(columns, cards[i].CatalogColumns);
                    Assert.Equal(location.Y, cards[i].TranslatePoint(new Point(), view).Y);
                    if (i > 0) Assert.True(cards[i].TranslatePoint(new Point(), view).X > location.X);
                }
                Assert.True(cards[columns].TranslatePoint(new Point(), view).Y > location.Y);
                Assert.Equal(columns == 3 ? Visibility.Collapsed : Visibility.Visible,
                    ((ItemsControl)first.FindName("CovenantChips")).Visibility);
                Save(view, GameShopTheme.Closure, night, $"layout-{columns}");
                if (columns < 3) Cycle();
            }
            var items = (ItemsControl)view.FindName("OperatorListControl");
            var scroll = Desc<ScrollViewer>(items).First();
            scroll.ScrollToVerticalOffset(650); Layout();
            for (int step = 0; step < 3; step++)
            {
                var anchor = Desc<OperatorCard>(items).First(c =>
                {
                    var y = c.TranslatePoint(new Point(), scroll).Y;
                    return y + c.ActualHeight > 0 && y < scroll.ViewportHeight;
                });
                var op = anchor.DataContext;
                var before = anchor.TranslatePoint(new Point(), scroll).Y;
                Cycle();
                var after = Desc<OperatorCard>(items).Single(c => ReferenceEquals(c.DataContext, op))
                    .TranslatePoint(new Point(), scroll).Y;
                Assert.InRange(Math.Abs(after - before), 0, 1);
            }
            vm.SearchText = "纯烬艾雅法拉"; Layout();
            var selected = Desc<OperatorCard>(view).Single();
            foreach (string name in new[] {"TrackButton", "BanButton"})
                ((Button)selected.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(vm.SearchText, tracked); Assert.Equal(vm.SearchText, banned);
            host.Width = 280; Layout();
            Save(view, GameShopTheme.Closure, night, "compact-narrow");
            Cycle();
            Assert.Equal("纯烬艾雅法拉", vm.SearchText);
            Assert.Equal(1, Desc<OperatorCard>(view).Single().CatalogColumns);
        }
        finally { host.Close(); }
    }

    private static IEnumerable<T> Desc<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var nested in Desc<T>(child)) yield return nested;
        }
    }

    private static void Save(FrameworkElement view, GameShopTheme theme, bool night, string suffix)
    {
        var directory = Path.Combine(TestOutput.Directory, "operator-catalogue");
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, $"{theme}-{night}-{suffix}.png"));
        png.Save(file);
    }
}
