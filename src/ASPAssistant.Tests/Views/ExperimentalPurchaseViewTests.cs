using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json;
using System.Windows.Threading;
using ASPAssistant.App;
using ASPAssistant.App.Windows;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

internal static class ExperimentalPurchaseViewTests
{
    public static void Verify(GameShopTheme theme, bool night)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var folder = Path.Combine(Path.GetTempPath(), "purchase-toggle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var settings = new SettingsManager(folder);
        var calculator = new CovenantCalculator([], [], new Dictionary<string, string>());
        var panel = new SidePanelWindow(new(), new(), new(), new(), new(), new(),
            new(new LineupStore(Path.Combine(folder, "lineups"), calculator.EquipmentRules), calculator),
            new(calculator.EquipmentRules, Path.Combine(folder, "share")), [], [], [], settings, theme, new() { FreeMovement = true })
        { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, Width = 280, Height = 720 };
        var events = new List<bool>();
        panel.AutomaticPurchaseTrackingChanged += events.Add;
        panel.InitAutomaticPurchaseTracking(Task.Run(settings.LoadAutomaticPurchaseTrackingAsync).GetAwaiter().GetResult());
        var speedEvents = new List<PurchaseRecognitionSpeed>();
        panel.PurchaseRecognitionSpeedChanged += speedEvents.Add;
        panel.InitPurchaseRecognitionSpeed(Task.Run(settings.LoadPurchaseRecognitionSpeedAsync).GetAwaiter().GetResult());
        try
        {
            panel.Show(); ThemeManager.Apply(theme, night); Pump();
            ((Button)panel.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            var toggle = (CheckBox)panel.FindName("AutomaticPurchaseTrackingToggle");
            var peer = (CheckBox)panel.FindName("MultiLineupTrackingToggle");
            var selector = (ListBox)panel.FindName("PurchaseSpeedSelector");
            var speedControls = (StackPanel)panel.FindName("PurchaseSpeedControls");
            selector.SelectedIndex.Should().Be(0); selector.IsEnabled.Should().BeFalse();
            selector.Items.Cast<ListBoxItem>().Select(item => item.Content).Should().Equal("标准", "快速", "极速");
            toggle.IsChecked.Should().BeFalse(); events.Should().BeEmpty();
            toggle.Style.Should().BeSameAs(peer.Style);
            System.Windows.Automation.AutomationProperties.GetName(toggle).Should().Be("自动标记购买（实验性）");
            foreach (var width in new[] { 280, 360, 400 })
            {
                panel.Width = width;
                foreach (var enabled in new[] { true, false })
                {
                    toggle.IsChecked = enabled;
                    var until = DateTime.UtcNow.AddSeconds(5);
                    while (!toggle.IsEnabled && DateTime.UtcNow < until) Pump();
                    toggle.IsEnabled.Should().BeTrue();
                    Task.Run(settings.LoadAutomaticPurchaseTrackingAsync).GetAwaiter().GetResult().Should().Be(enabled);
                    events.Last().Should().Be(enabled);
                    selector.IsEnabled.Should().Be(enabled);
                    if (enabled)
                    {
                        foreach (var index in new[] { 1, 2, 0 })
                        {
                            selector.SelectedIndex = index;
                            var savedUntil = DateTime.UtcNow.AddSeconds(5);
                            while (!speedControls.IsEnabled && DateTime.UtcNow < savedUntil) Pump();
                            speedControls.IsEnabled.Should().BeTrue();
                            var expected = index switch { 1 => PurchaseRecognitionSpeed.Fast, 2 => PurchaseRecognitionSpeed.Fastest, _ => PurchaseRecognitionSpeed.Standard };
                            speedEvents.Last().Should().Be(expected);
                            Task.Run(settings.LoadPurchaseRecognitionSpeedAsync).GetAwaiter().GetResult().Should().Be(expected);
                            selector.IsEnabled.Should().BeTrue();
                        }
                        selector.SelectedIndex = 2;
                        while (!speedControls.IsEnabled) Pump();
                    }
                    selector.SelectedIndex.Should().Be(2, "disabling keeps the chosen speed");
                    toggle.ActualWidth.Should().BeApproximately(peer.ActualWidth, .5);
                    toggle.ActualHeight.Should().BeApproximately(peer.ActualHeight, .5);
                    var row = (DockPanel)VisualTreeHelper.GetParent(toggle);
                    foreach (var text in Desc<TextBlock>(row))
                    {
                        var bounds = text.TransformToAncestor(row).TransformBounds(new Rect(text.RenderSize));
                        bounds.Right.Should().BeLessThanOrEqualTo(row.ActualWidth + .5);
                    }
                    Save(row, speedControls, theme, night, width, enabled);
                }
            }
            events.Should().Equal(true, false, true, false, true, false);
            var hint = (TextBlock)panel.FindName("PurchaseSpeedHint");
            var foreground = ((SolidColorBrush)hint.Foreground).Color;
            var background = ((SolidColorBrush)panel.FindResource("CardBackgroundBrush")).Color;
            static double Channel(byte value) { var n = value / 255.0; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); }
            static double Luminance(Color color) => .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
            var a = Luminance(foreground); var b = Luminance(background);
            var ratio = (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
            ratio.Should().BeGreaterThanOrEqualTo(4.5);
            File.WriteAllText(Path.Combine(TestOutput.Directory, "experimental-purchases", $"contrast-{theme}-{night}.json"), JsonSerializer.Serialize(new { Hint = ratio }));
        }
        finally { panel.Close(); Directory.Delete(folder, true); }
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<T> Desc<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var nested in Desc<T>(child)) yield return nested;
        }
    }
    private static void Save(DockPanel row, StackPanel speedControls, GameShopTheme theme, bool night, int width, bool enabled)
    {
        var dir = Path.Combine(TestOutput.Directory, "experimental-purchases"); Directory.CreateDirectory(dir);
        var card = (Border)((StackPanel)row.Parent).Parent;
        var controls = row.TransformToAncestor(card).TransformBounds(new Rect(row.RenderSize));
        controls.Union(speedControls.TransformToAncestor(card).TransformBounds(new Rect(speedControls.RenderSize)));
        card.BringIntoView(new Rect(0, controls.Top, card.ActualWidth, controls.Height));
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(240) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
        var root = (FrameworkElement)Window.GetWindow(card)!.Content;
        root.UpdateLayout();
        var full = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        full.Render(root);
        var transform = card.TransformToAncestor(root);
        var shownControls = transform.TransformBounds(controls);
        shownControls.Top.Should().BeGreaterThanOrEqualTo(0);
        shownControls.Bottom.Should().BeLessThanOrEqualTo(root.ActualHeight);
        var bounds = transform.TransformBounds(new Rect(0, controls.Top, card.ActualWidth, controls.Height));
        bounds.Intersect(new Rect(0, 0, full.PixelWidth, full.PixelHeight));
        var crop = new Int32Rect((int)Math.Floor(bounds.Left), (int)Math.Floor(bounds.Top),
            (int)Math.Ceiling(bounds.Right) - (int)Math.Floor(bounds.Left), (int)Math.Ceiling(bounds.Bottom) - (int)Math.Floor(bounds.Top));
        var bitmap = new CroppedBitmap(full, crop);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(dir, $"{theme}-{night}-{width}-{enabled}.png")); encoder.Save(file);
    }
}
