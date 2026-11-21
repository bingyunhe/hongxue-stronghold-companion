// Added for Hongxue Stronghold Companion on 2026-11-17; see NOTICE.
using System.Windows;
using System.Diagnostics;
using ASPAssistant.App.Controls;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ASPAssistant.App;
using ASPAssistant.App.Views;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

internal static class TrackingStageViewTests
{
    public static void VerifyTierBadges(bool night)
    {
        ThemeManager.Apply(GameShopTheme.Closure, night);
        var data = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "data"));
        var defaults = data.LoadOperatorsAsync().GetAwaiter().GetResult();
        var custom = data.LoadCustomOperatorsAsync().GetAwaiter().GetResult().First();
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.SetResourceReference(Panel.BackgroundProperty, "CardBackgroundBrush");
        foreach (var op in defaults.GroupBy(o=>o.Tier).OrderBy(g=>g.Key).Select(g=>g.First()).Append(custom))
            panel.Children.Add(new ContentPresenter { Content=op, ContentTemplate=(DataTemplate)panel.FindResource("TrackingTierBadge") });
        var host = new Window { Content=panel, Width=220, Height=58, WindowStyle=WindowStyle.None,
            Left=-20000, Top=-20000, ShowActivated=false, ShowInTaskbar=false };
        host.SetResourceReference(Control.BackgroundProperty, "CardBackgroundBrush");
        try
        {
            host.Show(); Pump();
            Assert.Equal(new[]{"Ⅰ","Ⅱ","Ⅲ","Ⅳ","Ⅴ","Ⅵ","Ⅴ/Ⅵ"},
                Descendants<TextBlock>(panel).Select(text=>text.Text));
            Capture(host, $"tier-badges-Closure-{night}");
        }
        finally { host.Close(); }
    }

    public static void Verify(GameShopTheme theme, bool night)
    {
        ThemeManager.Apply(theme, night);
        var data = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "data"));
        var operators = data.LoadOperatorsAsync().GetAwaiter().GetResult();
        var (equipment, _) = data.LoadEquipmentAsync().GetAwaiter().GetResult();
        var (covenants, map) = data.LoadCovenantsAsync().GetAwaiter().GetResult();
        var pool = new DeployPoolState(); pool.SetOwned("蓝毒", true);
        var vm = new TrackingViewModel(); vm.SetDeployPool(pool);
        vm.TrackLineup(new Lineup { Id = "sample", Name = "阶段演示", SklandGuide = new() { Stages =
        [new() { Id = "final", Name = "成型阵容", IsFinal = true, RecommendedEquipments = ["人事部文档"], Slots = [new() { OperatorName = "蓝毒", IsElite = true }, new() { OperatorName = "塔露拉" }] },
         new() { Id = "early", Name = "第1–3回合", StartRound = 1, EndRound = 3, RecommendedEquipments = ["坚守盾牌", "维式重锤"], Slots = [new() { OperatorName = "蓝毒" }, new() { OperatorName = "冻河", Equipments = ["坚守盾牌"] }] },
         new() { Id = "mid", Name = "名称很长的第5–8回合过渡阶段", StartRound = 5, EndRound = 8, Slots = [new() { OperatorName = "冻河" }] },
         new() { Id = "empty", Name = "空白自定义阶段" }] } });
        var view = new TrackingView { DataContext = vm };
        view.SetOperators(operators); view.SetEquipment(equipment); view.SetCovenantCalculator(new(operators, covenants, map));
        view.DeployPage.Initialize(operators.ToDictionary(o => o.Name), covenants, null, vm,
            new SettingsManager(Path.Combine(Path.GetTempPath(), "pool-stage-ui-" + Guid.NewGuid().ToString("N"))), pool, false);
        var host = new Window { Content = view, Width = 280, Height = 900, WindowStyle = WindowStyle.None,
            ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        host.SetResourceReference(Control.BackgroundProperty, "PanelBackgroundBrush");
        var selector = (ComboBox)view.FindName("StageSelector");
        var automatic = (CheckBox)view.FindName("AutoStageToggle");
        var cells = (ItemsControl)view.FindName("FinalCells");
        var positions = new Dictionary<int, (double Card, double Filter, double Stage)>();
        double Top(string name) => ((FrameworkElement)view.FindName(name)).TransformToAncestor(view).Transform(new Point()).Y;
        try
        {
            host.Show(); Pump();
            selector.Visibility.Should().Be(Visibility.Visible); selector.Items.Count.Should().Be(4);
            vm.SelectedStageChoice!.StageId.Should().Be("final"); automatic.IsChecked.Should().BeFalse();
            ((ListBox)view.FindName("CompletionFilterList")).SelectedIndex = 2;
            selector.SelectedItem = vm.StageChoices.Single(c => c.StageId == "early"); Pump();
            cells.Items.Cast<TrackedCell>().Select(c => c.Name).Should().BeEquivalentTo("蓝毒", "冻河");
            vm.DisplayEquipment.Select(e => e.Name).Should().BeEquivalentTo("坚守盾牌", "维式重锤");
            vm.DisplayEquipment.Single(e => e.Name == "坚守盾牌").EquipmentKind.Should().Be(TrackedEquipmentKind.Planned);
            vm.DisplayEquipment.Single(e => e.Name == "维式重锤").EquipmentKind.Should().Be(TrackedEquipmentKind.Candidate);
            var finalToggle = (Button)view.FindName("FinalToggle");
            var gearToggle = (Button)view.FindName("EquipmentToggle");
            var gearCells = (ItemsControl)view.FindName("EquipmentCells");
            finalToggle.TransformToAncestor(view).Transform(new Point()).X.Should().BeLessThan(selector.TransformToAncestor(view).Transform(new Point()).X);
            finalToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            gearCells.Visibility.Should().Be(Visibility.Visible); cells.Visibility.Should().Be(Visibility.Collapsed);
            gearToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            gearCells.Visibility.Should().Be(Visibility.Collapsed);
            ((TextBlock)view.FindName("EquipmentCount")).Text.Should().Be("2 件");
            vm.SyncDeployPool(); Pump();
            cells.Visibility.Should().Be(Visibility.Collapsed); gearCells.Visibility.Should().Be(Visibility.Collapsed);
            Capture(host, $"{theme}-{night}-shopping-folded");
            finalToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            gearToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            vm.Economy = new(2, 10, 3, false, null, true); automatic.IsChecked = true; Pump();
            vm.AutomaticStageSwitch.Should().BeTrue();
            foreach (var width in new[] { 280, 400 })
            {
                host.Width = width; Pump();
                var bounds = automatic.TransformToAncestor(view).TransformBounds(new Rect(automatic.RenderSize));
                bounds.Right.Should().BeLessThanOrEqualTo(view.ActualWidth);
                selector.ActualWidth.Should().BeGreaterThan(60);
                positions[width] = (((FrameworkElement)view.FindName("TrackingStatusCard")).ActualHeight, Top("CompletionFilterList"), Top("StageSelector"));
                var allToggle = (FrameworkElement)view.FindName("AllStagesToggle");
                var lastFilter = (ListBoxItem)((ListBox)view.FindName("CompletionFilterList")).Items[2];
                var lastBounds = lastFilter.TransformToAncestor(view).TransformBounds(new Rect(lastFilter.RenderSize));
                allToggle.TransformToAncestor(view).Transform(new Point()).X.Should().BeGreaterThanOrEqualTo(lastBounds.Right);
                Capture(host, $"{theme}-{night}-{width}-auto");
            }
            vm.Economy = new(5, 9, 3, false, null, true); host.Width = 280; Pump();
            vm.SelectedStageChoice.StageId.Should().Be("mid");
            cells.Items.Cast<TrackedCell>().Should().ContainSingle().Which.Name.Should().Be("冻河");
            Capture(host, $"{theme}-{night}-long-stage");
            selector.SelectedItem = vm.StageChoices.Single(c => c.StageId == "empty"); Pump();
            automatic.IsChecked.Should().BeFalse();
            ((StackPanel)view.FindName("FinalSection")).Visibility.Should().Be(Visibility.Visible);
            ((Button)view.FindName("ClearAllButton")).Visibility.Should().Be(Visibility.Visible);
            foreach (var width in new[] { 280, 400 })
            {
                host.Width = width; Pump();
                ((FrameworkElement)view.FindName("TrackingStatusCard")).ActualHeight.Should().BeApproximately(positions[width].Card, .1);
                Top("CompletionFilterList").Should().BeApproximately(positions[width].Filter, .1);
                Top("StageSelector").Should().BeApproximately(positions[width].Stage, .1);
                Capture(host, $"{theme}-{night}-{width}-empty-stage");
            }
            var overview = (System.Windows.Controls.Primitives.ToggleButton)view.FindName("AllStagesToggle");
            overview.IsChecked = true; host.Width = 280; Pump();
            vm.ShowAllStages.Should().BeTrue(); automatic.IsChecked.Should().BeFalse();
            vm.SelectedStageChoice.StageId.Should().Be("empty");
            cells.Items.Cast<TrackedCell>().Select(c => c.Name).Should().BeEquivalentTo("蓝毒", "塔露拉", "冻河");
            vm.DisplayEquipment.Select(e => e.Name).Should().BeEquivalentTo("人事部文档", "坚守盾牌", "维式重锤");
            Capture(host, $"{theme}-{night}-all-stages");
            ((ListBox)view.FindName("CompletionFilterList")).SelectedIndex = 1; Pump();
            cells.Items.Cast<object>().Should().BeEmpty("the full plan requires elite for the already owned operator");
            overview.IsChecked = false; ((ListBox)view.FindName("CompletionFilterList")).SelectedIndex = 2; Pump();
            vm.SelectedStageChoice.StageId.Should().Be("empty");
            File.WriteAllText(Path.Combine(TestOutput.Directory, $"tracking-header-{theme}-{night}.json"), JsonSerializer.Serialize(positions.ToDictionary(p => p.Key, p => new { p.Value.Card, p.Value.Filter, p.Value.Stage })));
            var pages = (ListBox)view.FindName("TrackingPageList"); pages.SelectedIndex = 1; host.Width = 400; Pump();
            var deploy = view.DeployPage;
            var poolSelector = (ComboBox)deploy.FindName("PoolStageSelector");
            var poolAutomatic = (CheckBox)deploy.FindName("PoolAutomaticToggle");
            var poolOverview = (System.Windows.Controls.Primitives.ToggleButton)deploy.FindName("PoolAllStagesToggle");
            var poolToggle = (Button)deploy.FindName("PoolTrackedToggle");
            var body = (StackPanel)deploy.FindName("PoolTrackedBody");
            poolSelector.Items.Count.Should().Be(4);
            ((TextBlock)deploy.FindName("PoolStageTitle")).Visibility.Should().Be(Visibility.Collapsed);
            poolOverview.IsChecked = true; Pump();
            ((System.Windows.Controls.Primitives.ToggleButton)view.FindName("AllStagesToggle")).IsChecked.Should().BeTrue();
            Descendants<Avatar>(body).Select(a => a.Text).Should().BeEquivalentTo("蓝毒", "塔露拉", "冻河");
            Descendants<Avatar>(body).Select(a => a.Text).Should().OnlyHaveUniqueItems();
            var activeId = vm.SelectedStageChoice.StageId; bool auto = vm.AutomaticStageSwitch;
            poolToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); body.Visibility.Should().Be(Visibility.Collapsed);
            poolOverview.IsChecked = false; poolOverview.IsChecked = true; Pump();
            body.Visibility.Should().Be(Visibility.Collapsed);
            vm.SelectedStageChoice.StageId.Should().Be(activeId); vm.AutomaticStageSwitch.Should().Be(auto);
            poolSelector.Visibility.Should().Be(Visibility.Visible);
            Capture(host, $"{theme}-{night}-status-folded");
            poolToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Capture(host, $"{theme}-{night}-status-all-stages");
            poolSelector.SelectedItem = vm.StageChoices.Single(c => c.StageId == "early"); Pump();
            vm.SelectedStageChoice.StageId.Should().Be("early"); vm.AutomaticStageSwitch.Should().BeFalse();
            ((TrackingStageChoice)selector.SelectedItem).StageId.Should().Be("early");
            poolOverview.IsChecked = false; Pump();
            Descendants<Avatar>(body).Select(a => a.Text).Should().BeEquivalentTo("蓝毒", "冻河");
            ((TextBlock)deploy.FindName("PoolTrackedCount")).Text.Should().Be("1/2");
            foreach (int width in new[] { 280, 400 })
            {
                host.Width = width; Pump();
                poolSelector.ActualWidth.Should().BeGreaterThan(60);
                var autoBounds = poolAutomatic.TransformToAncestor(deploy).TransformBounds(new Rect(poolAutomatic.RenderSize));
                autoBounds.Right.Should().BeLessThanOrEqualTo(deploy.ActualWidth + .1);
                var overviewBounds = poolOverview.TransformToAncestor(deploy).TransformBounds(new Rect(poolOverview.RenderSize));
                overviewBounds.Right.Should().BeLessThanOrEqualTo(deploy.ActualWidth + .1);
                Capture(host, $"{theme}-{night}-{width}-status-stage");
            }
            vm.Economy = new(2, 10, 3, false, null, true); poolAutomatic.IsChecked = true; Pump();
            vm.AutomaticStageSwitch.Should().BeTrue();
            vm.Economy = new(5, 10, 3, false, null, true); Pump();
            ((TrackingStageChoice)poolSelector.SelectedItem).StageId.Should().Be("mid");
            pool.IsOwned("蓝毒").Should().BeTrue(); pool.IsElite("蓝毒").Should().BeFalse();
            var before = JsonSerializer.Serialize(vm.CaptureState());
            int stagePublications = 0; vm.StageStateChanged += () => stagePublications++;
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++) poolToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            double foldingMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            for (int i = 0; i < 10; i++) { poolOverview.IsChecked = true; poolOverview.IsChecked = false; }
            double scopeMs = watch.Elapsed.TotalMilliseconds;
            stagePublications.Should().Be(0);
            JsonSerializer.Serialize(vm.CaptureState()).Should().Be(before);
            File.WriteAllText(Path.Combine(TestOutput.Directory, $"pool-fold-{theme}-{night}-performance.json"), JsonSerializer.Serialize(new
                { FoldClicks = 1000, FoldMilliseconds = foldingMs, OverviewSwitches = 20, OverviewMilliseconds = scopeMs,
                    StagePublications = stagePublications, Note = "Control event/model path; excludes rendering frames and OCR. Collapse only changes body visibility." }));
            pages.SelectedIndex = 0; host.Width = 280; Pump();
            vm.ClearAllTracking(); Pump();
            selector.Visibility.Should().Be(Visibility.Collapsed);
            ((Button)view.FindName("ClearAllButton")).Visibility.Should().Be(Visibility.Hidden);
            Top("CompletionFilterList").Should().BeApproximately(positions[280].Filter, .1);
        }
        finally { host.Close(); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var raw = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); raw.Render(window);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            drawing.DrawImage(raw, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var directory = Path.Combine(TestOutput.Directory, "tracking-stages"); Directory.CreateDirectory(directory);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name + ".png")); png.Save(file);
    }
}
