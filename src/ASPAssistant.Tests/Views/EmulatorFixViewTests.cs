using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ASPAssistant.App.Views;
using ASPAssistant.App.Windows;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Interop;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

internal static class EmulatorFixViewTests
{
    public static void VerifyEquipmentPurchase()
    {
        const string gear = "虹彩活血饮料";
        Operator[] operators = [new() { Name = "角峰" }];
        Equipment[] equipment = [new() { Name = gear }];
        foreach (var manual in new[] { false, true })
        {
            var folder = Path.Combine(Path.GetTempPath(), "equipment-purchase-" + Guid.NewGuid().ToString("N"));
            var settings = new SettingsManager(folder);
            var tracking = new TrackingViewModel();
            if (manual) tracking.AddTracking(gear, TrackingType.Equipment);
            else tracking.TrackLineup(new Lineup { Id = "test", Slots = [new() { OperatorName = "角峰", Equipments = [gear] }] });
            var calculator = new CovenantCalculator(operators, [], new Dictionary<string, string>());
            var view = new TrackingView { DataContext = tracking };
            view.SetOperators(operators); view.SetEquipment(equipment); view.SetCovenantCalculator(calculator);
            view.DeployPage.Initialize(operators.ToDictionary(o => o.Name), [], null, tracking, settings, new(), false);
            var host = new Window { Content = view, Height = 900, Width = 400, Opacity = 0,
                ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
            host.Show();
            ((ListBox)view.FindName("CompletionFilterList")).SelectedIndex = 2;
            view.DeployPage.ApplyRecognizedPurchases([gear]);
            foreach (var theme in Enum.GetValues<GameShopTheme>())
            foreach (var night in new[] { false, true })
            foreach (var width in new[] { 280, 360, 400 })
            {
                ASPAssistant.App.ThemeManager.Apply(theme, night);
                host.Width = width;
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                view.UpdateLayout();
                var cell = Descendants<ASPAssistant.App.Controls.EquipmentCell>((ItemsControl)view.FindName("EquipmentCells")).Single();
                cell.IsAcquired.Should().BeTrue();
                var button = (Button)cell.FindName("AcquireButton");
                button.Content.Should().Be("已获取");
                button.IsEnabled.Should().BeFalse();
                button.TransformToAncestor(view).TransformBounds(new Rect(button.RenderSize)).Right.Should().BeLessThanOrEqualTo(width + 1);
            }
            settings.SaveTrackingStateAsync(tracking.CaptureState()).GetAwaiter().GetResult();
            var restored = new TrackingViewModel();
            restored.LoadState(settings.LoadTrackingStateAsync(calculator.EquipmentRules).GetAwaiter().GetResult());
            restored.TrackedEquipment.Single().IsAcquired.Should().BeTrue();
            view.DeployPage.ApplyRecognizedPurchases([gear]);
            tracking.TrackedEquipment.Single().IsAcquired.Should().BeTrue();
            host.Close();
        }
    }

    public static void VerifyConnectionLayout()
    {
        var appearance = new SidebarAppearance();
        WindowUiScale.Initialize(appearance);
        var dialog = new GameConnectionWindow(new ScreenCaptureService(), new SettingsManager(Path.Combine(Path.GetTempPath(), "connection-layout")))
        { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
        dialog.Show();
        ((RadioButton)dialog.FindName("AdbMode")).IsChecked = true;
        ((System.Windows.Controls.Primitives.ToggleButton)dialog.FindName("ManualToggle")).IsChecked = true;
        var output = Path.Combine(TestOutput.Directory, "emulator-connection");
        Directory.CreateDirectory(output);
        var content = (FrameworkElement)dialog.Content;
        foreach (var theme in Enum.GetValues<GameShopTheme>())
        foreach (var night in new[] { false, true })
        {
            ASPAssistant.App.ThemeManager.Apply(theme, night);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            dialog.UpdateLayout();
            content.ActualHeight.Should().BeLessThan(900);
            foreach (var name in new[] { "WindowSelector", "DisplaySelector", "FollowFocusedGameToggle", "ApplyButton" })
            {
                var element = (FrameworkElement)dialog.FindName(name);
                element.IsVisible.Should().BeTrue();
                var bounds = element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
                bounds.Right.Should().BeLessThanOrEqualTo(content.ActualWidth + 1);
                bounds.Bottom.Should().BeLessThanOrEqualTo(content.ActualHeight + 1);
            }
            Save($"{theme}-{night}.png");
        }

        var body = (ScrollViewer)dialog.FindName("ConnectionBody");
        var apply = (Button)dialog.FindName("ApplyButton");
        var cancel = (Button)dialog.FindName("CancelButton");
        var status = (Border)dialog.FindName("StatusStrip");
        var statusTitle = (TextBlock)dialog.FindName("StatusTitle");
        var statusText = (TextBlock)dialog.FindName("StatusText");
        var preview = (Border)dialog.FindName("PreviewFrame");
        appearance.UiScale = 1.25;
        WindowUiScale.Refresh(appearance);
        dialog.MaxHeight = 380;
        foreach (var night in new[] { false, true })
        {
            ASPAssistant.App.ThemeManager.Apply(GameShopTheme.Closure, night);
            var path = (TextBox)dialog.FindName("AdbPathBox");
            path.Text = "adb.exe";
            ((ComboBox)dialog.FindName("WindowSelector")).SelectedIndex = -1;
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            statusTitle.Text.Should().Be("还没选游戏窗口");
            path.Text = "  ";
            Descendants<Button>(dialog).Single(b => Equals(b.Content, "刷新屏幕")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            statusTitle.Text.Should().Be("刷新屏幕失败");
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            statusTitle.Text.Should().Be("测试画面失败");
            statusText.Text.Should().Contain("ADB 路径为空").And.Contain("发现设备").And.Contain("浏览…");
            body.ScrollToTop();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var applyPosition = Bounds(apply);
            var cancelPosition = Bounds(cancel);
            applyPosition.Bottom.Should().BeLessThanOrEqualTo(content.ActualHeight + 1);
            cancelPosition.Bottom.Should().BeLessThanOrEqualTo(content.ActualHeight + 1);
            body.ComputedVerticalScrollBarVisibility.Should().Be(Visibility.Visible);
            var bar = (System.Windows.Controls.Primitives.ScrollBar)body.Template.FindName("PART_VerticalScrollBar", body);
            bar.IsVisible.Should().BeTrue();
            body.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.MouseWheelEvent });
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            body.VerticalOffset.Should().BeGreaterThan(0);
            body.ScrollToBottom();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Bounds(status).Bottom.Should().BeLessThan(Bounds(body).Bottom);
            Bounds(apply).Should().Be(applyPosition);
            Bounds(cancel).Should().Be(cancelPosition);
            var background = ((SolidColorBrush)dialog.Background).Color;
            var tint = ((SolidColorBrush)status.Background).Color;
            var opacity = tint.A / 255.0;
            var composed = Color.FromRgb(
                (byte)Math.Round(tint.R * opacity + background.R * (1 - opacity)),
                (byte)Math.Round(tint.G * opacity + background.G * (1 - opacity)),
                (byte)Math.Round(tint.B * opacity + background.B * (1 - opacity)));
            foreach (var text in new[] { statusTitle, statusText })
            {
                var foreground = Luminance(((SolidColorBrush)text.Foreground).Color);
                var surface = Luminance(composed);
                ((Math.Max(foreground, surface) + .05) / (Math.Min(foreground, surface) + .05)).Should().BeGreaterThanOrEqualTo(4.5);
            }
            Save($"Closure-{night}-compact-error.png");

            preview.Visibility = Visibility.Visible;
            body.ScrollToBottom();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Bounds(preview).Bottom.Should().BeLessThan(Bounds(body).Bottom);
            preview.Visibility = Visibility.Collapsed;
        }
        dialog.Close();

        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));

        void Save(string name)
        {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            var surface = new DrawingVisual();
            using (var drawing = surface.RenderOpen()) drawing.DrawRectangle(dialog.Background, null, new Rect(content.RenderSize));
            bitmap.Render(surface);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name));
            encoder.Save(file);
        }

        static double Luminance(Color color)
        {
            static double Linear(byte value) => value / 255.0 <= .04045
                ? value / 255.0 / 12.92 : Math.Pow((value / 255.0 + .055) / 1.055, 2.4);
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
    }

    public static void VerifyPicker()
    {
        var operators = new List<Operator> { new() { Name = "最终", Tier = 6, CoreCovenants = ["乌萨斯"] },
            new() { Name = "过渡", Tier = 2 }, new() { Name = "其他", Tier = 3 } };
        operators.AddRange(Enumerable.Range(1, 6).Select(t => new Operator { Name = "扩展" + t, Tier = t, CoreCovenants = ["乌萨斯"] }));
        var tracking = new TrackingViewModel();
        tracking.TrackLineup(new Lineup { Slots = [new() { OperatorName = "最终" }], EarlySlots = [new() { OperatorName = "过渡" }] });
        var view = new DeployView();
        var state = new DeployPoolState();
        view.Initialize(operators.ToDictionary(o => o.Name), [], null, tracking,
            new SettingsManager(Path.Combine(Path.GetTempPath(), "picker-exclusions-" + Guid.NewGuid().ToString("N"))), state, false);
        var host = new Window { Content = view, Width = 400, Height = 900, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        host.Show();
        Exception? error = null;
        var checkedPicker = false;
        Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            var picker = Application.Current.Windows.OfType<OperatorPickerWindow>().Single();
            try
            {
                checkedPicker = true;
                ((ListBox)picker.FindName("OperatorsList")).Items.Cast<Operator>().Select(o => o.Name).Should().Equal("其他");
                ((ListBox)picker.FindName("ScopeList")).Visibility.Should().Be(Visibility.Collapsed);
            }
            catch (Exception exception) { error = exception; }
            finally { picker.Close(); }
        }, DispatcherPriority.Loaded);
        Descendants<Button>(view).Single(b => Equals(b.Content, "＋ 添加")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        checkedPicker.Should().BeTrue();
        if (error is not null) throw error;
        var editor = new OperatorPickerWindow(operators, "", new HashSet<string>(), new("后期", "", new HashSet<string>(),
            new Dictionary<string, int>(), _ => new Dictionary<string, int>(), [], []));
        ((ListBox)editor.FindName("ScopeList")).Visibility.Should().Be(Visibility.Collapsed);
        tracking.AddTracking("追踪装备", TrackingType.Equipment);
        view.ApplyRecognizedPurchases(["最终", "其他", "追踪装备", "未追踪装备"]);
        state.IsOwned("最终").Should().BeTrue();
        state.IsOwned("其他").Should().BeTrue();
        state.IsElite("最终").Should().BeFalse();
        state.CountOf("最终").Should().Be(1);
        tracking.TrackedOperators.Single(e => e.Name == "最终").IsAcquired.Should().BeTrue();
        tracking.TrackedEquipment.Single(e => e.Name == "追踪装备").IsAcquired.Should().BeTrue();
        tracking.TrackedEquipment.Should().NotContain(e => e.Name == "未追踪装备");
        tracking.CaptureState().PurchasedEquipment.Should().BeEquivalentTo("追踪装备", "未追踪装备");
        view.ApplyRecognizedPurchases(["最终", "追踪装备"]);
        state.CountOf("最终").Should().Be(1);
        tracking.TrackedEquipment.Single(e => e.Name == "追踪装备").IsAcquired.Should().BeTrue();
        tracking.ClearAllTracking();
        tracking.TrackLineup(new Lineup { Slots = [new() { OperatorName = "最终", Equipments = ["未追踪装备"] }] });
        tracking.TrackedEquipment.Single().IsAcquired.Should().BeTrue();
        editor.Close(); host.Close();
    }

    public static void VerifyPanel()
    {
        var folder = Path.Combine(Path.GetTempPath(), "panel-layer-" + Guid.NewGuid().ToString("N"));
        var calculator = new CovenantCalculator([], [], new Dictionary<string, string>());
        var panel = new SidePanelWindow(new(), new(), new(), new(), new(), new(),
            new(new LineupStore(Path.Combine(folder, "lineups"), calculator.EquipmentRules), calculator),
            new(calculator.EquipmentRules, Path.Combine(folder, "share")), [], [], [], new(folder), GameShopTheme.Terminal, new())
        { Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        var game = new Window { Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        var other = new Window { Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        panel.Show(); game.Show(); other.Show();
        var hwnd = new WindowInteropHelper(panel).Handle;
        var gameHwnd = new WindowInteropHelper(game).Handle;
        panel.Topmost.Should().BeFalse();
        panel.UpdateTopmost(gameHwnd, gameHwnd);
        panel.Topmost.Should().BeTrue();
        var dialog = new Window { Owner = panel, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        dialog.Show();
        panel.UpdateTopmost(gameHwnd, new WindowInteropHelper(dialog).Handle);
        panel.Topmost.Should().BeTrue();
        panel.UpdateTopmost(gameHwnd, new WindowInteropHelper(other).Handle);
        panel.Topmost.Should().BeFalse();
        (User32.GetWindowLong(hwnd, User32.GWL_EXSTYLE) & 8).Should().Be(0);
        panel.UpdateTopmost(gameHwnd, hwnd);
        panel.Topmost.Should().BeTrue("Issue 32: focusing the assistant restores topmost after another app was active");
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        // Verify the visible ordering of both windows in the owned group.
        var precedingOther = new List<IntPtr>();
        for (var window = User32.GetWindow(new WindowInteropHelper(other).Handle, 3); window != IntPtr.Zero; window = User32.GetWindow(window, 3)) precedingOther.Add(window);
        precedingOther.Should().Contain(hwnd, "the focused assistant must appear above the other app");
        var dialogHwnd = new WindowInteropHelper(dialog).Handle;
        precedingOther.Should().Contain(dialogHwnd);
        precedingOther.IndexOf(hwnd).Should().BeLessThan(precedingOther.IndexOf(dialogHwnd),
            "walking upward from the other app must reach the owner before its dialog");
        ((User32.GetWindowLong(hwnd, User32.GWL_EXSTYLE)
            | User32.GetWindowLong(new WindowInteropHelper(dialog).Handle, User32.GWL_EXSTYLE)) & 8).Should().Be(8);
        panel.UpdateTopmost(gameHwnd, new WindowInteropHelper(other).Handle);
        // An owned popup can remain topmost while its owner has already yielded to another app.
        User32.SetWindowLayer(dialogHwnd, new IntPtr(-1));
        panel.UpdateTopmost(gameHwnd, dialogHwnd);
        panel.Topmost.Should().BeTrue("an owned dialog also restores topmost from the inactive state");
        panel.UpdateTopmost(gameHwnd, gameHwnd);
        panel.UpdateTopmost(IntPtr.Zero, gameHwnd);
        panel.Topmost.Should().BeFalse();
        dialog.Close();

        var work = User32.GetMonitorGeometry(300, 300).WorkArea;
        var scale = User32.GetWindowDpiScale(hwnd);
        var start = new RECT { Left = work.Left + 80, Top = work.Top + 100, Right = work.Left + 80 + (int)(310 * scale), Bottom = work.Top + 100 + (int)(400 * scale) };
        User32.SetWindowBounds(hwnd, start);
        User32.SendMessage(hwnd, User32.WM_ENTERSIZEMOVE, IntPtr.Zero, IntPtr.Zero);
        var near = start; near.Left = work.Left + 5; near.Right = near.Left + start.Width;
        User32.SetWindowBounds(hwnd, near);
        User32.SendMessage(hwnd, User32.WM_EXITSIZEMOVE, IntPtr.Zero, IntPtr.Zero);
        User32.ReadWindowRect(hwnd).Left.Should().Be(work.Left);
        var control = (CheckBox)panel.FindName("FreeSidePanelMovementToggle");
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!control.IsEnabled && DateTime.UtcNow < timeout)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        control.IsEnabled.Should().BeTrue();
        var saved = new SettingsManager(folder).LoadSidePanelPositionAsync().GetAwaiter().GetResult();
        saved.Placement!.Left.Should().Be(work.Left);
        // Resizing near an edge must preserve the user's exact position.
        User32.SendMessage(hwnd, User32.WM_ENTERSIZEMOVE, IntPtr.Zero, IntPtr.Zero);
        near.Right += 30;
        User32.SetWindowBounds(hwnd, near);
        User32.SendMessage(hwnd, User32.WM_EXITSIZEMOVE, IntPtr.Zero, IntPtr.Zero);
        User32.ReadWindowRect(hwnd).Left.Should().Be(near.Left);
        panel.Close(); game.Close(); other.Close();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
