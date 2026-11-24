using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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

public partial class ThemeAndHighlightTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TooltipSettings_ControlsNativeDetailsAndKeepsMapGuide(bool night) => ui.RunAsync(() =>
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hover-settings-" + Guid.NewGuid().ToString("N"))).FullName;
        var data = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "data"));
        var operators = data.LoadOperatorsAsync().GetAwaiter().GetResult();
        var (bonds, jobs) = data.LoadCovenantsAsync().GetAwaiter().GetResult();
        var calc = new CovenantCalculator(operators, bonds, jobs);
        var settings = new SettingsManager(dir);
        File.WriteAllText(Path.Combine(dir, "side-panel-position.json"), "{}");
        var initial = settings.LoadSidePanelPositionAsync().GetAwaiter().GetResult();
        var browse = new OperatorBrowseViewModel();
        browse.LoadOperators(operators);
        var tracking = new TrackingViewModel();
        var panel = new SidePanelWindow(browse, new(), tracking, new(), new(), new(),
            new LineupViewModel(new LineupStore(dir, calc.EquipmentRules), calc), new(calc.EquipmentRules, dir),
            operators, [], data.LoadStrategiesAsync().GetAwaiter().GetResult(), settings, GameShopTheme.Closure, initial, covenants: bonds)
        { Width = 390, Height = 690, Top = 20, Left = 20, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        var dirShots = Directory.CreateDirectory(Path.Combine(TestOutput.Directory, "tooltip-settings")).FullName;
        void Pump() => panel.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void WaitForRender()
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        ToolTipEventArgs Opening(FrameworkElement owner)
        {
            var args = (ToolTipEventArgs)Activator.CreateInstance(typeof(ToolTipEventArgs),
                BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { true }, null)!;
            owner.RaiseEvent(args);
            return args;
        }
        void Hover(FrameworkElement owner, Window window, bool right = true)
        {
            ToolTipService.GetInitialShowDelay(owner).Should().Be(250);
            ToolTipService.GetBetweenShowDelay(owner).Should().Be(800);
            Opening(owner).Handled.Should().BeFalse();
            var tip = (ToolTip)owner.ToolTip;
            tip.Content.Should().BeSameAs(panel.PlannerVm.CovenantDetails.Single(c => c.Name == "精准"));
            tip.PlacementTarget = owner;
            tip.IsOpen = true; WaitForRender();
            Descendants<TextBlock>(tip).Should().Contain(t => t.Text == "精准");
            var gap = right ? tip.PointToScreen(new Point()).X - window.PointToScreen(new Point(window.ActualWidth, 0)).X
                : window.PointToScreen(new Point()).X - tip.PointToScreen(new Point(tip.ActualWidth, 0)).X;
            gap.Should().BeApproximately(8 * VisualTreeHelper.GetDpi(window).DpiScaleX, 2);
            Save(tip, Path.Combine(dirShots, $"covenant-{window.GetType().Name}-{(right ? "right" : "left")}-{night}.png"));
        }
        void Saved()
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!panel.HoverOptions.IsEnabled && timer.Elapsed.TotalSeconds < 5) Pump();
            panel.HoverOptions.IsEnabled.Should().BeTrue();
        }
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(panel.Dispatcher));
        try
        {
            panel.Show(); Pump(); ThemeManager.Apply(GameShopTheme.Closure, night);
            panel.HoverEnabledToggle.IsChecked.Should().BeTrue();
            panel.HoverSide.SelectedIndex.Should().Be(0);
            panel.SettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            panel.SettingsCategoryList.SelectedIndex = 1; Pump();
            panel.HoverSettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            panel.HoverOptions.IsVisible.Should().BeTrue();
            panel.HoverSide.SelectedIndex = 1; Saved();
            panel.HoverHelp.BringIntoView(); WaitForRender();
            Save((FrameworkElement)panel.Content, Path.Combine(dirShots, $"settings-enabled-{night}.png"));
            panel.SettingsPanel.Visibility = Visibility.Collapsed;
            panel.OperatorView.FilterToggle.IsChecked = true; Pump();
            var chip = Descendants<ToggleButton>(panel.OperatorView).First(b => PanelToolTip.GetCovenant(b) == "精准");
            panel.Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - panel.Width) / 2; Pump();
            Hover(chip, panel);
            ((ToolTip)chip.ToolTip).IsOpen = false;
            panel.Left = SystemParameters.WorkArea.Right - panel.ActualWidth; Pump();
            Hover(chip, panel, false);
            panel.HoverSide.SelectedIndex = 0; Saved();
            Hover(chip, panel, false);
            panel.HoverSide.SelectedIndex = 1; Saved();
            var work = SystemParameters.WorkArea;
            var picker = new OperatorPickerWindow(operators, "", new HashSet<string>()) { Owner = panel, Width = 580, Height = 520,
                Opacity = 0, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = Math.Max(work.Left + 8, work.Right - 580 - 420), Top = work.Top + 60 };
            picker.Show(); Pump();
            var other = Descendants<ToggleButton>(picker).First(b => PanelToolTip.GetCovenant(b) == "精准");
            Hover(other, picker);
            panel.HoverEnabledToggle.IsChecked = false; Saved(); Pump();
            ((ToolTip)other.ToolTip).IsOpen.Should().BeFalse();
            Opening(other).Handled.Should().BeTrue(); Opening(panel.SettingsButton).Handled.Should().BeTrue();
            ((ToolTip)other.ToolTip).ClearValue(ToolTip.PlacementTargetProperty);
            picker.Close();
            var saved = Task.Run(settings.LoadSidePanelPositionAsync).GetAwaiter().GetResult();
            saved.HoverEnabled.Should().BeFalse(); saved.HoverOnRight.Should().BeTrue();
            panel.HoverSide.IsEnabled.Should().BeFalse();
            panel.SettingsPanel.Visibility = Visibility.Visible;
            panel.HoverHelp.BringIntoView(); WaitForRender();
            Save((FrameworkElement)panel.Content, Path.Combine(dirShots, $"settings-disabled-{night}.png"));
            tracking.TrackLineup(new Lineup { Name = "攻略例外", Slots = [new() { OperatorName = operators[0].Name }],
                EditorStages = [new() { Id = "final", Name = "成型", IsFinal = true, OperationIdea = "先保留资金" }] });
            panel.TrackingView.DeployPage.MapGuideButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            var guide = Application.Current.Windows.OfType<TrackingGuideWindow>().Single();
            guide.IsVisible.Should().BeTrue();
            guide.Notes.Text.Should().Be("先保留资金");
            panel.HoverEnabledToggle.IsChecked = true; Saved(); Pump();
            panel.HoverEnabledToggle.IsChecked = false; Saved(); Pump();
            guide.IsVisible.Should().BeTrue();
            guide.Close();
            panel.SettingsPanel.Visibility = Visibility.Collapsed;
            panel.CodexSegment.SelectedIndex = 2; WaitForRender();
            var reference = (ASPAssistant.App.Views.ReferenceCatalogueView)panel.ReferenceHost.Content;
            var portrait = Descendants<Button>(reference.Entries).First();
            portrait.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            reference.DetailsPopup.IsOpen.Should().BeFalse();
            panel.HoverEnabledToggle.IsChecked = true; Saved();
            portrait.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            reference.DetailsPopup.IsOpen.Should().BeTrue();
            reference.PinnedLabel.Text.Should().Be("已固定");
            panel.HoverEnabledToggle.IsChecked = false; Saved();
            reference.DetailsPopup.IsOpen.Should().BeFalse();
        }
        finally
        {
            panel.Close(); Pump();
            PanelToolTip.SetPreferences(true, false);
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }, $"TooltipSettings-{night}");
}
