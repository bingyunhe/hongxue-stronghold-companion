using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ASPAssistant.App;
using ASPAssistant.App.Views;
using ASPAssistant.App.Windows;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Interop;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

internal static class RecognitionHotkeyViewTests
{
    public static void Verify(GameShopTheme theme, bool night)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var folder = Path.Combine(Path.GetTempPath(), "recognition-keys-" + Guid.NewGuid().ToString("N"));
        var settings = new SettingsManager(folder);
        var calculator = new CovenantCalculator([], [], new Dictionary<string, string>());
        var panel = new SidePanelWindow(new OperatorBrowseViewModel(), new EquipmentBrowseViewModel(), new TrackingViewModel(),
            new GameStateViewModel(), new BanViewModel(), new EnemyViewModel(),
            new LineupViewModel(new LineupStore(Path.Combine(folder, "lineups"), calculator.EquipmentRules), calculator),
            new LineupShareService(calculator.EquipmentRules, Path.Combine(folder, "share")), [], [], [], settings,
            theme, new SidePanelPositionSettings { FreeMovement = true })
        { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Left = -20000, Top = -20000, Height = 900, Width = 360 };
        using var blocker = new HwndSource(new HwndSourceParameters("recognition key test"));
        var keys = new List<AssistantHotkey>();
        try
        {
            for (uint key = 0x70; key < 0x7B && keys.Count < 4; key++)
                if (User32.RegisterHotKey(blocker.Handle, (int)key, 7 | 0x4000, key)) keys.Add(new(7, key));
            keys.Should().HaveCount(4);
            for (var i = 0; i < 3; i++) User32.UnregisterHotKey(blocker.Handle, (int)keys[i].VirtualKey);
            panel.Show(); ThemeManager.Apply(theme, night); Pump();
            var hwnd = new WindowInteropHelper(panel).Handle;
            var scans = 0; var hides = 0;
            panel.ManualScanRequested += () => scans++;
            panel.ToggleAssistantVisibilityRequested += () => hides++;
            void Dispatch(int id) => User32.SendMessage(hwnd, User32.WM_HOTKEY, new IntPtr(id), IntPtr.Zero);
            Await(settings.SaveRecognitionHotkeyAsync(keys[0]));
            Await(panel.InitRecognitionHotkeyAsync());
            Await(panel.SaveAssistantHotkeyAsync(keys[1]));
            var status = (TextBlock)panel.FindName("RecognitionHotkeyStatus");
            var box = (TextBox)panel.FindName("RecognitionHotkeyBox");
            status.Text.Should().Be("快捷键已启用");
            Await(panel.InitGuideHotkeyAsync());
            Await(panel.SaveGuideHotkeyAsync(keys[0]));
            var guideStatus = (TextBlock)panel.FindName("GuideHotkeyStatus");
            guideStatus.Text.Should().Contain("快速识别").And.Contain("冲突");
            Await(panel.SaveGuideHotkeyAsync(keys[1]));
            guideStatus.Text.Should().Contain("助手").And.Contain("冲突");
            Await(panel.SaveGuideHotkeyAsync(keys[2]));
            Await(panel.SaveGuideHotkeyAsync(keys[3]));
            guideStatus.Text.Should().Contain("占用");
            Await(settings.LoadGuideHotkeyAsync()).Should().Be(keys[2]);
            Dispatch(panel.GuideShortcutId);
            ((TextBlock)panel.FindName("ToastText")).Text.Should().Be("请先追踪一个阵容");
            Await(panel.SaveGuideHotkeyAsync(null));
            var originalId = panel.RecognitionShortcutId;
            Dispatch(originalId); scans.Should().Be(1); hides.Should().Be(0);
            Dispatch(panel.AssistantShortcutId); hides.Should().Be(1); scans.Should().Be(1);

            Await(panel.SaveRecognitionHotkeyAsync(keys[1]));
            status.Text.Should().Contain("冲突");
            Await(settings.LoadRecognitionHotkeyAsync()).Should().Be(keys[0]);
            Await(panel.SaveRecognitionHotkeyAsync(keys[3]));
            status.Text.Should().Contain("占用").And.Contain("原设置保留");
            Await(settings.LoadRecognitionHotkeyAsync()).Should().Be(keys[0]);
            using (var locked = new FileStream(Path.Combine(folder, "recognition-hotkey.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Await(panel.SaveRecognitionHotkeyAsync(keys[2]));
                status.Text.Should().Contain("保存失败");
            }
            Await(settings.LoadRecognitionHotkeyAsync()).Should().Be(keys[0]);
            Dispatch(originalId); scans.Should().Be(2);
            Await(panel.SaveRecognitionHotkeyAsync(keys[2]));
            Await(settings.LoadRecognitionHotkeyAsync()).Should().Be(keys[2]);
            Dispatch(originalId); scans.Should().Be(2, "the old message id must no longer trigger a scan");
            Dispatch(panel.RecognitionShortcutId); scans.Should().Be(3);
            var scanButton = (Button)((RunBanView)panel.FindName("RunBanView")).FindName("ManualScanButton");
            scanButton.ToolTip.ToString().Should().Contain(box.Text);

            ((Button)panel.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            ((TabControl)panel.FindName("SettingsCategoryList")).SelectedIndex = 2; Pump();
            var section = (FrameworkElement)panel.FindName("QuickRecognitionPanel");
            section.BringIntoView(); Pump();
            box.Focus(); Pump();
            box.IsKeyboardFocusWithin.Should().BeTrue();
            Dispatch(panel.RecognitionShortcutId); scans.Should().Be(3, "recording a shortcut must not start recognition");
            Dispatch(panel.AssistantShortcutId); hides.Should().Be(1);
            status.Text.Should().Contain("冲突");
            ((Button)panel.FindName("ClearRecognitionHotkeyButton")).Focus(); Pump();
            panel.ReleaseRecognitionHotkey();
            Await(panel.InitRecognitionHotkeyAsync());
            status.Text.Should().Be("快捷键已启用");
            foreach (var width in new[] { 280, 360, 400 })
            {
                panel.Width = width; Pump(); section.BringIntoView(); Pump();
                Desc<TextBlock>(section).Select(t => t.Text).Should().Contain("窗口显隐", "隐藏 / 显示助手", "隐藏 / 显示攻略讲解", "对局识别", "快速识别");
                foreach (var input in Desc<TextBox>(section))
                {
                    input.ActualHeight.Should().Be(28);
                    input.Padding.Should().Be(new Thickness(8, 3, 8, 3));
                    var bounds = input.TransformToAncestor(section).TransformBounds(new Rect(0, 0, input.ActualWidth, input.ActualHeight));
                    bounds.Right.Should().BeLessThanOrEqualTo(section.ActualWidth + 0.5);
                }
                Save(section, theme, night, width);
            }
            ((Button)panel.FindName("ClearRecognitionHotkeyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Await(settings.LoadRecognitionHotkeyAsync()).Should().BeNull();
            box.Text.Should().Contain("未设置");
            Dispatch(panel.RecognitionShortcutId); scans.Should().Be(3);
            scanButton.ToolTip.ToString().Should().Contain("未设置快捷键");
            Await(settings.LoadAssistantHotkeyAsync()).Should().Be(keys[1]);
        }
        finally
        {
            panel.ReleaseGuideHotkey(); panel.ReleaseRecognitionHotkey(); panel.ReleaseAssistantHotkey();
            foreach (var key in keys) User32.UnregisterHotKey(blocker.Handle, (int)key.VirtualKey);
            panel.Close(); Directory.Delete(folder, true);
        }
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Await(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < until) Pump();
        task.IsCompleted.Should().BeTrue(); task.GetAwaiter().GetResult();
    }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static IEnumerable<T> Desc<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var nested in Desc<T>(child)) yield return nested;
        }
    }
    private static void Save(FrameworkElement view, GameShopTheme theme, bool night, int width)
    {
        var until = DateTime.UtcNow.AddMilliseconds(300);
        while (DateTime.UtcNow < until) { Pump(); Thread.Sleep(5); }
        var directory = Path.Combine(TestOutput.Directory, "recognition-hotkey"); Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(view.RenderSize);
            context.DrawRectangle((Brush)view.FindResource("PanelBackgroundBrush"), null, bounds);
            context.DrawRectangle(new VisualBrush(view), null, bounds);
        }
        bitmap.Render(drawing);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4 + 3] > 0).Should().BeGreaterThan(pixels.Length / 40, "the preview must contain the rendered settings cards");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, $"{theme}-{night}-{width}.png")); encoder.Save(file);
    }
}
