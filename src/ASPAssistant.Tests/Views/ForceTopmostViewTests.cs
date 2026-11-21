using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ASPAssistant.App;
using ASPAssistant.App.Windows;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Interop;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

internal static class ForceTopmostViewTests
{
    public static void Verify(GameShopTheme theme, bool night)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var folder = Path.Combine(Path.GetTempPath(), "force-topmost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var settings = new SettingsManager(folder);
        var calculator = new CovenantCalculator([], [], new Dictionary<string, string>());
        var panel = new SidePanelWindow(new(), new(), new(), new(), new(), new(),
            new(new LineupStore(Path.Combine(folder, "lineups"), calculator.EquipmentRules), calculator),
            new(calculator.EquipmentRules, Path.Combine(folder, "share")), [], [], [], settings, theme, new())
        { Opacity = 1, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, Height = 800 };
        var game = new Window { Topmost = true, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        var other = new Window { Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        var dialog = new Window { Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        var output = Path.Combine(TestOutput.Directory, "force-topmost");
        Directory.CreateDirectory(output);
        try
        {
            panel.Show(); game.Show(); other.Show();
            dialog.Owner = panel;
            ThemeManager.Apply(theme, night); Pump();
            var hwnd = new WindowInteropHelper(panel).Handle;
            var gameHwnd = new WindowInteropHelper(game).Handle;
            var foreground = new WindowInteropHelper(other).Handle;
            var toggle = (CheckBox)panel.FindName("ForceTopmostToggle");
            var pin = (ToggleButton)panel.FindName("PinTopmostButton");
            var settingsButton = (Button)panel.FindName("SettingsButton");
            panel.TopmostPreferenceChanged += () => panel.UpdateTopmost(gameHwnd, foreground);
            toggle.IsChecked.Should().BeFalse();
            pin.IsChecked.Should().BeFalse();
            panel.UpdateTopmost(gameHwnd, foreground);
            panel.Topmost.Should().BeFalse();
            var timing = new Dictionary<string, double>();
            foreach (var enabled in new[] { false, true })
            {
                toggle.IsChecked = enabled; Wait(() => toggle.IsEnabled);
                panel.UpdateTopmost(gameHwnd, foreground);
                var samples = new List<double>();
                for (var sample = 0; sample < 5; sample++)
                {
                    var watch = Stopwatch.StartNew();
                    for (var i = 0; i < 1000; i++) panel.UpdateTopmost(gameHwnd, foreground);
                    samples.Add(watch.Elapsed.TotalMilliseconds);
                }
                timing[enabled ? "forced-1000-polls-ms" : "default-1000-polls-ms"] = samples.Order().ElementAt(2);
            }
            toggle.IsChecked = false; Wait(() => toggle.IsEnabled);
            var beforeFocus = User32.GetForegroundWindow();
            pin.IsChecked = true;
            Wait(() => toggle.IsEnabled && toggle.IsChecked == true);
            panel.Topmost.Should().BeTrue("the pin updates the window immediately while another window has focus");
            pin.ToolTip.Should().Be("强制置顶：已开启（点击关闭）");
            Above(hwnd, gameHwnd).Should().BeTrue();
            User32.GetForegroundWindow().Should().Be(beforeFocus, "pinning must not activate the side panel");
            var saved = Await(settings.LoadSidePanelPositionAsync());
            saved.ForceTopmost.Should().BeTrue();
            saved.FreeMovement.Should().BeTrue();
            saved.Placement.Should().BeNull();

            // Reproduce an already-topmost game being raised, then clicking another window.
            User32.SetWindowLayer(gameHwnd, new IntPtr(-1));
            Above(gameHwnd, hwnd).Should().BeTrue();
            panel.UpdateTopmost(gameHwnd, gameHwnd);
            Pump();
            Above(hwnd, gameHwnd).Should().BeTrue();
            User32.SetWindowLayer(gameHwnd, new IntPtr(-1));
            panel.UpdateTopmost(gameHwnd, foreground);
            Pump();
            Above(hwnd, gameHwnd).Should().BeTrue();
            User32.IsWindowVisible(hwnd).Should().BeTrue();
            User32.GetForegroundWindow().Should().Be(beforeFocus);
            dialog.Show();
            panel.UpdateTopmost(gameHwnd, new WindowInteropHelper(dialog).Handle);
            Above(new WindowInteropHelper(dialog).Handle, hwnd).Should().BeTrue();
            dialog.Close();

            settingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var surface = (Border)panel.FindName("SettingsPanel");
            Wait(() => surface.Opacity >= 0.99999 && surface.RenderTransform is TranslateTransform move && Math.Abs(move.X) < 0.01);
            ((TabControl)panel.FindName("SettingsCategoryList")).SelectedIndex = 1; Pump();
            var root = (Border)panel.FindName("SidebarRoot");
            if (theme == GameShopTheme.Closure)
                VerifyFocusSettings(panel, settings, gameHwnd, foreground, root, output, night);
            foreach (var scale in new[] { 0.75, 1.0, 1.5 })
            foreach (var width in new[] { 280, 400 })
            {
                ((ScaleTransform)root.LayoutTransform).ScaleX = scale;
                ((ScaleTransform)root.LayoutTransform).ScaleY = scale;
                panel.MinWidth = 280 * scale;
                panel.MaxWidth = 400 * scale;
                panel.Width = width * scale;
                panel.Height = 600;
                ((FrameworkElement)toggle.Parent).BringIntoView(); Pump();
                var pinBounds = pin.TransformToAncestor(root).TransformBounds(new Rect(pin.RenderSize));
                var settingsBounds = settingsButton.TransformToAncestor(root).TransformBounds(new Rect(settingsButton.RenderSize));
                pinBounds.Right.Should().BeLessThanOrEqualTo(settingsBounds.Left);
                pinBounds.Left.Should().BeGreaterThanOrEqualTo(0);
                settingsBounds.Right.Should().BeLessThanOrEqualTo(root.ActualWidth);
                var title = (TextBlock)panel.FindName("BrandTitleText");
                var status = (Border)panel.FindName("StatusChip");
                var titleBounds = title.TransformToAncestor(root).TransformBounds(new Rect(title.RenderSize));
                var statusBounds = status.TransformToAncestor(root).TransformBounds(new Rect(status.RenderSize));
                titleBounds.Right.Should().BeLessThanOrEqualTo(statusBounds.Left + 0.5);
                statusBounds.Right.Should().BeLessThanOrEqualTo(pinBounds.Left + 0.5);
                var toggleBounds = toggle.TransformToAncestor(root).TransformBounds(new Rect(toggle.RenderSize));
                toggleBounds.Right.Should().BeLessThanOrEqualTo(root.ActualWidth);
                toggleBounds.Bottom.Should().BeLessThanOrEqualTo(root.ActualHeight);
                var rowBounds = ((FrameworkElement)toggle.Parent).TransformToAncestor(root)
                    .TransformBounds(new Rect(((FrameworkElement)toggle.Parent).RenderSize));
                rowBounds.Bottom.Should().BeLessThanOrEqualTo(root.ActualHeight);
                Save((FrameworkElement)VisualTreeHelper.GetParent(root), Path.Combine(output, $"{theme}-{night}-{width}-{scale * 100}.png"));
            }
            toggle.IsChecked = false; Wait(() => toggle.IsEnabled);
            pin.IsChecked.Should().BeFalse();
            panel.Topmost.Should().BeFalse();
            Above(foreground, hwnd).Should().BeTrue("the other app must cover the unpinned sidebar after focus leaves the game");
            User32.IsWindowVisible(hwnd).Should().BeTrue("losing focus must not hide the sidebar");
            Pump();
            Above(foreground, hwnd).Should().BeTrue("the other app must stay above after WPF processes its messages");
            var layerMessages = 0;
            HwndSourceHook layerHook = (IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message == 0x46) layerMessages++;
                return IntPtr.Zero;
            };
            var source = HwndSource.FromHwnd(hwnd);
            source.AddHook(layerHook);
            try
            {
                for (var i = 0; i < 1000; i++) panel.UpdateTopmost(gameHwnd, foreground);
                layerMessages.Should().Be(0, "stable unfocused polls must not rearrange the sidebar");
                Above(foreground, hwnd).Should().BeTrue();
            }
            finally { source.RemoveHook(layerHook); }
            timing["unfocused-stable-layer-messages"] = layerMessages;
            foreach (var otherTopmost in new[] { false, true })
            {
                other.Topmost = otherTopmost;
                for (var cycle = 0; cycle < 10; cycle++)
                {
                    panel.UpdateTopmost(gameHwnd, gameHwnd); Pump();
                    panel.Topmost.Should().BeTrue();
                    panel.UpdateTopmost(gameHwnd, foreground); Pump();
                    panel.Topmost.Should().BeFalse();
                    (User32.GetWindowLong(hwnd, User32.GWL_EXSTYLE) & 8).Should().Be(0);
                    Above(foreground, hwnd).Should().BeTrue();
                    User32.IsWindowVisible(hwnd).Should().BeTrue();
                }
            }
            other.Topmost = false;
            Await(settings.LoadSidePanelPositionAsync()).ForceTopmost.Should().BeFalse();
            Save((FrameworkElement)VisualTreeHelper.GetParent(root), Path.Combine(output, $"{theme}-{night}-off.png"));
            pin.IsChecked = true; Wait(() => toggle.IsEnabled);
            game.WindowState = WindowState.Minimized; Pump();
            panel.UpdateTopmost(gameHwnd, foreground);
            panel.Topmost.Should().BeFalse();
            panel.SetGameMinimized(true);
            panel.WindowState.Should().Be(WindowState.Minimized);
            game.WindowState = WindowState.Normal;
            panel.SetGameMinimized(false); Pump();
            panel.UpdateTopmost(gameHwnd, foreground);
            panel.Topmost.Should().BeTrue();
            panel.IsAssistantHidden = true;
            panel.UpdateTopmost(gameHwnd, foreground);
            panel.Topmost.Should().BeFalse();
            panel.IsAssistantHidden = false;
            panel.UpdateTopmost(gameHwnd, foreground);
            panel.Topmost.Should().BeTrue();
            game.Close();
            panel.UpdateTopmost(gameHwnd, foreground);
            panel.Topmost.Should().BeFalse();
            panel.UpdateTopmost(IntPtr.Zero, foreground);
            panel.Topmost.Should().BeFalse();

            var restored = new SidePanelWindow(new(), new(), new(), new(), new(), new(),
                new(new LineupStore(Path.Combine(folder, "lineups"), calculator.EquipmentRules), calculator),
                new(calculator.EquipmentRules, Path.Combine(folder, "share")), [], [], [], settings, theme,
                Await(settings.LoadSidePanelPositionAsync()))
            { Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                restored.Show(); Pump();
                ((CheckBox)restored.FindName("ForceTopmostToggle")).IsChecked.Should().BeTrue();
                ((ToggleButton)restored.FindName("PinTopmostButton")).IsChecked.Should().BeTrue();
                restored.UpdateTopmost(IntPtr.Zero, foreground);
                restored.Topmost.Should().BeFalse();
            }
            finally { restored.Close(); }
            File.WriteAllText(Path.Combine(output, $"timing-{theme}-{night}.json"), JsonSerializer.Serialize(timing));
        }
        finally { dialog.Close(); panel.Close(); game.Close(); other.Close(); Directory.Delete(folder, true); }
    }

    private static void VerifyFocusSettings(SidePanelWindow panel, SettingsManager settings, IntPtr game,
        IntPtr other, FrameworkElement root, string output, bool night)
    {
        var focus=(CheckBox)panel.FindName("FocusLossToggle");
        var mode=(ComboBox)panel.FindName("FocusLossSelector");
        var overlaySwitch=(CheckBox)panel.FindName("HideOverlayWithAssistantToggle");
        var tabs=(TabControl)panel.FindName("SettingsCategoryList");
        var hwnd=new WindowInteropHelper(panel).Handle;
        focus.IsChecked.Should().BeFalse();
        overlaySwitch.IsChecked.Should().BeFalse();
        mode.Visibility.Should().Be(Visibility.Collapsed);
        using var normal=new HwndSource(new HwndSourceParameters("Second normal window") {
            WindowStyle=unchecked((int)0x90000000), PositionX=-20000,PositionY=-20000,Width=20,Height=20 });
        var overlay=new Window { ShowActivated=false,ShowInTaskbar=false,Opacity=0,Left=-20000,Top=-20000 };
        var owned=new Window { Owner=panel,ShowActivated=false,ShowInTaskbar=false,Opacity=0,Left=-20000,Top=-20000 };
        try
        {
            overlay.Show();
            using var tray=new AssistantTray(panel,overlay);
            focus.IsChecked=true; Wait(()=>focus.IsEnabled);
            mode.SelectedIndex.Should().Be(0);
            panel.UpdateTopmost(game,game);
            panel.UpdateTopmost(game,other);
            panel.Topmost.Should().BeFalse();
            Above(other,hwnd).Should().BeTrue();
            Above(normal.Handle,hwnd).Should().BeTrue();
            User32.IsWindowVisible(hwnd).Should().BeTrue();
            panel.UpdateTopmost(game,game); panel.Topmost.Should().BeTrue();
            mode.BringIntoView(); Pump();
            Save((FrameworkElement)VisualTreeHelper.GetParent(root),Path.Combine(output,$"focus-loss-{night}.png"));
            mode.SelectedIndex=1; Wait(()=>mode.IsEnabled);
            panel.UpdateTopmost(game,other);
            panel.IsAssistantHidden.Should().BeTrue();
            panel.SuppressGameOverlay.Should().BeFalse();
            panel.UpdateTopmost(game,game);
            panel.IsAssistantHidden.Should().BeFalse();
            owned.Show(); panel.UpdateTopmost(game,new WindowInteropHelper(owned).Handle);
            panel.IsAssistantHidden.Should().BeFalse();
            owned.Close();
            tabs.SelectedIndex=0; Pump();
            overlaySwitch.IsChecked=true; Wait(()=>overlaySwitch.IsEnabled);
            overlaySwitch.BringIntoView(); Pump();
            Save((FrameworkElement)VisualTreeHelper.GetParent(root),Path.Combine(output,$"tracking-overlay-{night}.png"));
            panel.UpdateTopmost(game,other); panel.SuppressGameOverlay.Should().BeTrue();
            panel.UpdateTopmost(game,game); panel.SuppressGameOverlay.Should().BeFalse();
            tray.Toggle();
            panel.UpdateTopmost(game,other); panel.UpdateTopmost(game,game);
            panel.IsAssistantHidden.Should().BeTrue();
            panel.SuppressGameOverlay.Should().BeTrue();
            tray.Restore();
            var saved=Await(settings.LoadSidePanelPositionAsync());
            saved.HideOverlayWithAssistant.Should().BeTrue();
            saved.FocusLossEnabled.Should().BeTrue();
            saved.TrayOnFocusLoss.Should().BeTrue();
            focus.IsChecked=false; Wait(()=>focus.IsEnabled);
            overlaySwitch.IsChecked=false; Wait(()=>overlaySwitch.IsEnabled);
            tabs.SelectedIndex=1; Pump();
        }
        finally { owned.Close(); overlay.Close(); }
    }

    private static bool Above(IntPtr upper, IntPtr lower)
    {
        for (var window = User32.GetWindow(lower, 3); window != IntPtr.Zero; window = User32.GetWindow(window, 3))
            if (window == upper) return true;
        return false;
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Wait(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(5)) { Pump(); Thread.Sleep(5); }
        condition().Should().BeTrue("the UI change completes within five seconds");
    }
    private static T Await<T>(Task<T> task)
    {
        Wait(() => task.IsCompleted);
        return task.GetAwaiter().GetResult();
    }
    private static void Save(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
