using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ASPAssistant.App;
using ASPAssistant.App.Controls;
using ASPAssistant.App.Windows;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

internal static class GameStartSettingsViewTests
{
    public static void VerifyCurrentRunFilterPreference(bool night)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var directory = Directory.CreateTempSubdirectory("current-run-filter-").FullName;
        var settings = new SettingsManager(directory);
        var calculator = new CovenantCalculator([], [], new Dictionary<string, string>());
        var panel = new SidePanelWindow(new(), new(), new(), new(), new(), new(),
            new(new LineupStore(Path.Combine(directory, "lineups"), calculator.EquipmentRules), calculator),
            new(calculator.EquipmentRules, Path.Combine(directory, "share")), [], [], [], settings,
            GameShopTheme.Closure, new SidePanelPositionSettings { FreeMovement = true },
            appearance: new SidebarAppearance { Theme = GameShopTheme.Closure, Night = new() { [GameShopTheme.Closure] = night } })
        { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, Width = 280, Height = 600 };
        try
        {
            panel.InitAutomaticCurrentRunFilters(Complete(settings.LoadAutomaticCurrentRunFiltersAsync()));
            panel.Show();
            ThemeManager.Apply(GameShopTheme.Closure, night);
            OpenSettings(panel);
            var toggle = (CheckBox)panel.FindName("AutomaticCurrentRunFiltersToggle");
            var row = (DockPanel)toggle.Parent;
            toggle.IsChecked.Should().BeFalse();
            row.BringIntoView();
            Wait(() => toggle.IsVisible);
            Save(panel, $"Closure-{night}-current-run-filter-off");

            panel.OperatorBrowseVm.SetAvailableCovenants(["炎"]);
            panel.PlannerVm.SetAvailableCovenants(["炎"]);
            panel.OperatorBrowseVm.OnlyCurrentRun = true;
            panel.ApplyAutomaticCurrentRunFilters();
            panel.OperatorBrowseVm.OnlyCurrentRun.Should().BeTrue("the disabled setting preserves a manual selection");
            panel.PlannerVm.OnlyCurrentRun.Should().BeFalse();

            panel.OperatorBrowseVm.OnlyCurrentRun = false;
            toggle.IsChecked = true;
            Wait(() => toggle.IsEnabled);
            Complete(new SettingsManager(directory).LoadAutomaticCurrentRunFiltersAsync()).Should().BeTrue();
            panel.OperatorBrowseVm.OnlyCurrentRun.Should().BeFalse("changing the setting does not immediately change the view");
            panel.PlannerVm.OnlyCurrentRun.Should().BeFalse();

            for (var recognition = 0; recognition < 2; recognition++)
            {
                panel.ApplyAutomaticCurrentRunFilters();
                panel.OperatorBrowseVm.OnlyCurrentRun.Should().BeTrue();
                panel.PlannerVm.OnlyCurrentRun.Should().BeTrue();
                panel.OperatorBrowseVm.OnlyCurrentRun = panel.PlannerVm.OnlyCurrentRun = false;
            }

            ((Slider)panel.FindName("UiScaleSlider")).Value = 125;
            row.BringIntoView(); Pump();
            var bounds = row.TransformToAncestor(panel).TransformBounds(new Rect(row.RenderSize));
            bounds.Right.Should().BeLessThanOrEqualTo(panel.ActualWidth + 1);
            bounds.Top.Should().BeGreaterThanOrEqualTo(0);
            bounds.Bottom.Should().BeLessThanOrEqualTo(panel.ActualHeight + 1);
            Save(panel, $"Closure-{night}-current-run-filter-125");

            toggle.IsChecked = false;
            Wait(() => toggle.IsEnabled);
            Complete(new SettingsManager(directory).LoadAutomaticCurrentRunFiltersAsync()).Should().BeFalse();
            panel.ApplyAutomaticCurrentRunFilters();
            panel.OperatorBrowseVm.OnlyCurrentRun.Should().BeFalse();
            panel.PlannerVm.OnlyCurrentRun.Should().BeFalse();
        }
        finally { panel.Close(); Directory.Delete(directory, true); }
    }

    public static void Verify(GameShopTheme theme, bool night)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var directory = Path.Combine(Path.GetTempPath(), "game-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new SettingsManager(directory);
        var operators = Enumerable.Range(1, 4).Select(i => new Operator { Name = $"火{i}", CoreCovenants = ["炎"] }).ToList();
        CovenantInfo[] covenants = [new() { Name = "炎", BondType = "SEASON", ActivateCount = 3 }];
        var calculator = new CovenantCalculator(operators, covenants, new Dictionary<string, string>());
        SidePanelWindow Create(GameStartSettings navigation, SidebarAppearance appearance)
        {
            var panel = new SidePanelWindow(new OperatorBrowseViewModel(), new EquipmentBrowseViewModel(), new TrackingViewModel(),
                new GameStateViewModel(), new BanViewModel(), new EnemyViewModel(),
                new LineupViewModel(new LineupStore(Path.Combine(directory, "lineups"), calculator.EquipmentRules), calculator),
                new LineupShareService(calculator.EquipmentRules, Path.Combine(directory, "share")), operators, [], [], settings, theme,
                new SidePanelPositionSettings(), new LineupPlanner(operators, covenants), covenants, appearance: appearance)
                { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, Height = 800 };
            panel.InitGameStartSettings(navigation);
            panel.Show(); ThemeManager.Apply(theme, night); Pump();
            panel.OperatorBrowseVm.SetAvailableCovenants(["炎"]);
            return panel;
        }
        var appearance = new SidebarAppearance { Theme = theme, Night = new() { [theme] = night } };
        SidePanelWindow? current = Create(new(), appearance);
        try
        {
            var panel = current;
            OpenSettings(panel);
            var startupToggle = (CheckBox)panel.FindName("StartupInformationToggle");
            panel.InitStartupInformation(Complete(settings.LoadStartupInformationAsync()));
            startupToggle.IsChecked.Should().BeFalse();
            startupToggle.IsChecked = true;
            Wait(() => startupToggle.IsEnabled);
            Complete(new SettingsManager(directory).LoadStartupInformationAsync()).Should().BeTrue();
            var roundToggle = (CheckBox)panel.FindName("AutomaticRoundEnemiesToggle");
            panel.InitAutomaticRoundEnemies(Complete(settings.LoadAutomaticRoundEnemiesAsync()));
            roundToggle.IsChecked.Should().BeFalse();
            ((ScrollViewer)panel.FindName("SettingsScroll")).ScrollToEnd(); Pump();
            roundToggle.IsVisible.Should().BeTrue();
            startupToggle.IsVisible.Should().BeTrue();
            Save(panel, $"{theme}-{night}-round-enemy-setting");
            roundToggle.IsChecked = true;
            Wait(() => roundToggle.IsEnabled);
            Complete(settings.LoadAutomaticRoundEnemiesAsync()).Should().BeTrue();
            roundToggle.IsChecked = false;
            Wait(() => roundToggle.IsEnabled);
            ((Button)panel.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var settingsView = (Border)panel.FindName("SettingsPanel");
            Wait(() => settingsView.Visibility == Visibility.Collapsed);
            var runTab = (TabItem)panel.FindName("RunInfoTab");
            ((TabControl)panel.FindName("MainTabs")).SelectedItem = runTab;
            panel.EnemyVm.RoundEnemies = new(1, []);
            Pump();
            var enemyView = (FrameworkElement)panel.FindName("EnemyView");
            // The selected tab's scale viewport can be replaced on the loaded pass.
            // Capture the steady state of the actual visual ancestors after layout.
            for (DependencyObject? element = enemyView; element is not null && element != panel; element = VisualTreeHelper.GetParent(element))
                if (element is FrameworkElement ancestor && ancestor.HasAnimatedProperties)
                    ancestor.BeginAnimation(UIElement.OpacityProperty, null);
            Pump();
            Desc<TextBlock>(enemyView).Should().Contain(t => t.Text.Contains("第 1 回合 · 待确认") && t.IsVisible);
            Save(panel, $"{theme}-{night}-round-enemy-pending");
            panel.EnemyVm.RoundEnemies = new(1, ["源石虫", "普通囚犯"]);
            Pump();
            Desc<TextBlock>(enemyView).Should().Contain(t => t.Text.Contains("源石虫、普通囚犯") && t.IsVisible);
            Save(panel, $"{theme}-{night}-round-enemy-read");
            OpenSettings(panel);
            var with = (ComboBox)panel.FindName("GameStartWithLineups");
            var without = (ComboBox)panel.FindName("GameStartWithoutLineups");
            var fixedLineups = (ToggleButton)panel.FindName("GameStartFixLineups");
            var fixedPlanner = (ToggleButton)panel.FindName("GameStartFixPlanner");
            var card = (Border)panel.FindName("GameStartCard");
            void Saved() => Wait(() => card.IsEnabled);
            void Fix(ToggleButton button) { button.IsChecked = button.IsChecked != true; button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Saved(); }
            int Segment() => ((ListBox)panel.FindName("LineupsSegment")).SelectedIndex;
            void Navigate() { panel.NavigateAfterGameStart(); Pump(); }
            with.SelectedIndex.Should().Be(0); without.SelectedIndex.Should().Be(0);
            File.Exists(Path.Combine(directory, "game-start.json")).Should().BeFalse("initializing controls must not overwrite preferences");
            var themeList = (ListBox)panel.FindName("GameThemeSelector");
            themeList.Items.Count.Should().Be(3);
            var themeRows = themeList.Items.Cast<ListBoxItem>().ToList();
            for (int i = 1; i < themeRows.Count; i++)
            {
                var previous = themeRows[i - 1].TransformToAncestor(themeList).TransformBounds(new Rect(themeRows[i - 1].RenderSize));
                var next = themeRows[i].TransformToAncestor(themeList).TransformBounds(new Rect(themeRows[i].RenderSize));
                next.Top.Should().BeGreaterThanOrEqualTo(previous.Bottom - 1, "mode-theme options keep their vertical arrangement");
            }
            var themeTexts = Desc<TextBlock>(themeList).Select(t => t.Text).ToArray();
            themeTexts.Should().Contain("卫戍协议终端").And.Contain("深灰机壳 · 青绿描边");
            foreach (int width in new[] { 280, 400 })
            {
                panel.Width = width; card.BringIntoView(); Pump();
                foreach (var control in new FrameworkElement[] { with, without, fixedLineups, fixedPlanner })
                {
                    var bounds = control.TransformToAncestor(card).TransformBounds(new Rect(control.RenderSize));
                    bounds.Left.Should().BeGreaterThanOrEqualTo(0); bounds.Right.Should().BeLessThanOrEqualTo(card.ActualWidth + 1);
                }
                Save(panel, $"{theme}-{night}-{width}-conditional");
            }
            with.SelectedIndex = 1; Saved(); without.SelectedIndex = 1; Saved();
            Fix(fixedLineups); fixedPlanner.IsChecked.Should().BeFalse();
            ((TextBlock)panel.FindName("GameStartEffectiveTitle")).Text.Should().Contain("待确认");
            Fix(fixedPlanner); fixedLineups.IsChecked.Should().BeFalse();
            ((TextBlock)panel.FindName("GameStartEffectiveTitle")).Text.Should().Contain("规划 · 目标");
            card.BringIntoView(); Pump(); Save(panel, $"{theme}-{night}-fixed");
            var usable = new Lineup { Id = "usable", Name = "可用阵容", Slots = [new() { OperatorName = "火2" }, new() { OperatorName = "火3" }, new() { OperatorName = "火4" }], TargetCovenants = [new() { Name = "炎" }] };
            var pending = new Lineup { Id = "pending", Name = "待确认阵容", Slots = [new() { OperatorName = "火1" }, new() { OperatorName = "火2" }, new() { OperatorName = "火3" }], TargetCovenants = [new() { Name = "炎" }] };
            panel.LineupVm.Lineups.Add(usable); panel.LineupVm.Lineups.Add(pending); panel.BanVm.SetBans(["火1"]);
            Navigate(); Segment().Should().Be(1); panel.PlannerVm.ShowGoals.Should().BeTrue("fixed planning ignores available saved lineups");
            Fix(fixedPlanner); Navigate(); Segment().Should().Be(0);
            panel.LineupVm.SelectedAvailabilityFilter.Should().Be(LineupAvailabilityFilter.Pending);
            panel.LineupVm.VisibleLineups.Select(l => l.Id).Should().Equal("pending");
            with.SelectedIndex = 0; Saved(); Navigate();
            panel.LineupVm.SelectedAvailabilityFilter.Should().Be(LineupAvailabilityFilter.Available);
            panel.LineupVm.VisibleLineups.Select(l => l.Id).Should().Equal("usable");
            panel.LineupVm.Lineups.Remove(usable); Navigate(); Segment().Should().Be(0, "a pending lineup counts for the saved-lineup branch");
            panel.LineupVm.SelectedAvailabilityFilter.Should().Be(LineupAvailabilityFilter.Available, "the configured destination remains explicit even when empty");
            panel.LineupVm.Lineups.Clear(); Navigate(); Segment().Should().Be(1); panel.PlannerVm.ShowGoals.Should().BeTrue();
            Fix(fixedLineups); Navigate(); Segment().Should().Be(0, "fixed saved-lineup navigation also works with no saved lineups");
            Fix(fixedLineups); without.SelectedIndex = 0; Saved(); Navigate(); panel.PlannerVm.ShowAuto.Should().BeTrue();
            Complete(panel.PlannerVm.PendingAutoPlan!);
            panel.LineupVm.Lineups.Add(usable); panel.BanVm.SetCovenantBans(["炎"]); Navigate();
            panel.LineupVm.Recommendations.Should().OnlyContain(r => r.Kind == RecommendationKind.Broken);
            Segment().Should().Be(1); panel.PlannerVm.ShowAuto.Should().BeTrue();
            panel.BanVm.ClearBans(); Navigate(); Segment().Should().Be(0);
            var tracking = JsonSerializer.Serialize(panel.TrackingVm.CaptureState());
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++) panel.ShowLineupsSegment(false);
            var baselineMs = watch.Elapsed.TotalMilliseconds; watch.Restart();
            for (int i = 0; i < 100; i++) panel.NavigateAfterGameStart();
            var conditionalMs = watch.Elapsed.TotalMilliseconds;
            Fix(fixedPlanner); Navigate(); Complete(panel.PlannerVm.PendingAutoPlan!);
            var cached = panel.PlannerVm.PendingAutoPlan; watch.Restart();
            for (int i = 0; i < 100; i++) panel.NavigateAfterGameStart();
            var automaticMs = watch.Elapsed.TotalMilliseconds;
            panel.PlannerVm.PendingAutoPlan.Should().BeSameAs(cached, "repeated navigation reuses the existing automatic candidates");
            JsonSerializer.Serialize(panel.TrackingVm.CaptureState()).Should().Be(tracking);
            File.WriteAllText(Path.Combine(TestOutput.Directory, $"game-start-{theme}-{night}-performance.json"),
                JsonSerializer.Serialize(new { Navigations = 100, BaselineMs = baselineMs, ConditionalMs = conditionalMs, FixedAutomaticMs = automaticMs, SameAutomaticSearch = true }));
            with.SelectedIndex = 1; Saved(); without.SelectedIndex = 1; Saved();
            ((TabControl)panel.FindName("SettingsCategoryList")).SelectedIndex = 1; Pump();
            var scaleInput = (TextBox)panel.FindName("UiScaleValueBox");
            var slider = (Slider)panel.FindName("UiScaleSlider");
            void EnterScale(string value)
            {
                scaleInput.Text = value;
                scaleInput.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
                Wait(() => slider.IsEnabled); Pump();
            }
            EnterScale("120");
            Complete(settings.LoadSidebarAppearanceAsync()).UiScale.Should().Be(1.2);
            ((ComboBox)panel.FindName("UiScaleTargetSelector")).SelectedIndex = 2; Pump();
            var follow = (CheckBox)panel.FindName("UiScaleFollowToggle");
            follow.IsChecked = false; follow.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Wait(() => slider.IsEnabled);
            EnterScale("83");
            var persisted = Complete(new SettingsManager(directory).LoadSidebarAppearanceAsync());
            persisted.UiScale.Should().Be(1.2); persisted.ScaleOverrides[UiScaleTarget.Tracking].Should().Be(0.83);
            persisted.ScaleOverrides.Should().NotContainKey(UiScaleTarget.Codex);
            var navigation = Complete(new SettingsManager(directory).LoadGameStartSettingsAsync());
            navigation.Should().Be(new GameStartSettings { WithLineups = GameStartLineupPage.Pending, WithoutLineups = GameStartPlannerPage.Goals, FixedTarget = GameStartFixedTarget.Planner });
            panel.Close(); current = null;
            current = Create(navigation, persisted); panel = current;
            ((ScaleTransform)((Border)panel.FindName("SidebarRoot")).LayoutTransform).ScaleX.Should().Be(1.2);
            ((TextBox)panel.FindName("UiScaleValueBox")).Text.Should().Be("120");
            ((TabControl)panel.FindName("MainTabs")).SelectedIndex = 1; Pump();
            ((TabItem)((TabControl)panel.FindName("MainTabs")).Items[1]).Content.Should().BeOfType<ScaledViewport>()
                .Which.ContentScale.Should().BeApproximately(0.83 / 1.2, 0.0001);
            ((ToggleButton)panel.FindName("GameStartFixPlanner")).IsChecked.Should().BeTrue();
            ((ComboBox)panel.FindName("GameStartWithLineups")).SelectedIndex.Should().Be(1);
            ((ComboBox)panel.FindName("GameStartWithoutLineups")).SelectedIndex.Should().Be(1);
            panel.NavigateAfterGameStart(); panel.PlannerVm.ShowGoals.Should().BeTrue();
            foreach (var page in new[] { (FrameworkElement)((TabItem)((TabControl)panel.FindName("MainTabs")).SelectedItem).Content,
                (FrameworkElement)panel.FindName("PlannerView") })
            {
                page.BeginAnimation(UIElement.OpacityProperty, null); page.Opacity = 1;
            }
            Save(panel, $"{theme}-{night}-restored-scale");
            OpenSettings(panel); ((Border)panel.FindName("GameStartCard")).BringIntoView(); Pump();
            var file = Path.Combine(directory, "game-start.json"); File.Move(file, file + ".saved"); Directory.CreateDirectory(file);
            var fixedButton = (ToggleButton)panel.FindName("GameStartFixLineups"); fixedButton.IsChecked = true;
            fixedButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Wait(() => ((Border)panel.FindName("GameStartCard")).IsEnabled);
            ((TextBlock)panel.FindName("ToastText")).Text.Should().Contain("保存失败");
            Wait(() => ((Border)panel.FindName("ToastBorder")).Opacity > 0.95);
            Save(panel, $"{theme}-{night}-save-error");
        }
        finally { current?.Close(); Directory.Delete(directory, true); }
    }

    private static void OpenSettings(SidePanelWindow panel)
    {
        ((Button)panel.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var settings = (Border)panel.FindName("SettingsPanel");
        Wait(() => settings.Opacity > 0.999 && settings.RenderTransform is TranslateTransform transform && Math.Abs(transform.X) < 0.01);
        settings.BeginAnimation(UIElement.OpacityProperty, null); settings.Opacity = 1;
        var move = (TranslateTransform)settings.RenderTransform; move.BeginAnimation(TranslateTransform.XProperty, null); move.X = 0;
        ((TabControl)panel.FindName("SettingsCategoryList")).SelectedIndex = 0; Pump();
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Wait(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(10)) { Pump(); Thread.Sleep(2); }
        ready().Should().BeTrue("the UI operation finishes");
    }
    private static T Complete<T>(Task<T> task) { Wait(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    private static void Complete(Task task) { Wait(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static IEnumerable<T> Desc<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T value) yield return value;
            foreach (var nested in Desc<T>(child)) yield return nested;
        }
    }
    private static void Save(SidePanelWindow panel, string name)
    {
        panel.UpdateLayout(); Pump(); var content = (FrameworkElement)panel.Content;
        var surface = (FrameworkElement)VisualTreeHelper.GetParent(content);
        var image = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(surface); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        var folder = Path.Combine(TestOutput.Directory, "game-start-settings"); Directory.CreateDirectory(folder);
        using var file = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(file);
    }
}
