using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ASPAssistant.App;
using ASPAssistant.App.Windows;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Interop;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

public class ApplicationExitTests
{
    [Theory]
    [InlineData("button")]
    [InlineData("system")]
    [InlineData("dialog")]
    [InlineData("tray")]
    public Task CloseBehavior_ExitFinishesProcessWithHiddenOverlay(string action) => RunProbe(action);

    [Fact]
    public Task StartupResources_LoadFromApplication() => RunProbe("resources");

    private static async Task RunProbe(string action)
    {
        var host = Path.ChangeExtension(typeof(AppExitProbe).Assembly.Location, ".exe");
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add("--close-behavior-probe");
        start.ArgumentList.Add(action);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            process.ExitCode.Should().Be(0, await output + await error);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }
}

internal static class AppExitProbe
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--close-behavior-probe" || args[1] is not ("button" or "system" or "dialog" or "resources" or "tray")) return 2;
        var folder = Path.Combine(Path.GetTempPath(), "close-exit-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            if (args[1] == "resources")
            {
                var application = new ASPAssistant.App.App();
                application.InitializeComponent();
                return application.TryFindResource("AspaLogo") is not null
                    && application.TryFindResource("DialogPrimaryButton") is not null ? 0 : 27;
            }
            var app = new ProbeApplication(args[1], folder);
            foreach (var resource in new[] { "Themes/Terminal.Dark.xaml", "Styles.xaml", "Logo.xaml" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"/HongxueCompanion;component/Resources/{resource}", UriKind.Relative) });
            var code = app.Run();
            return app.ExitObserved ? code : 25;
        }
        catch (Exception error)
        {
            // Resource loading failures in this isolated process must fail the test without a fatal desktop dialog.
            Console.Error.WriteLine(error);
            return 28;
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class ProbeApplication(string action, string folder) : ASPAssistant.App.App
    {
        public bool ExitObserved { get; private set; }
        private AssistantTray? _tray;

        protected override void OnStartup(StartupEventArgs e)
            => Dispatcher.BeginInvoke(CreateWindows, DispatcherPriority.ApplicationIdle);

        private void CreateWindows()
        {
            var calculator = new CovenantCalculator([], [], new Dictionary<string, string>());
            var panel = new SidePanelWindow(new(), new(), new(), new(), new(), new(),
                new(new LineupStore(Path.Combine(folder, "lineups"), calculator.EquipmentRules), calculator),
                new(calculator.EquipmentRules, Path.Combine(folder, "share")), [], [], [], new(folder), GameShopTheme.Terminal, new(),
                closeBehavior: action == "tray" ? MainWindowCloseBehavior.MinimizeToTray :
                    action == "dialog" ? MainWindowCloseBehavior.Ask : MainWindowCloseBehavior.Exit)
            { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
            MainWindow = panel;
            panel.Show();
            var overlay = new Window { Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
            overlay.Show(); overlay.Hide();
            _tray = new AssistantTray(panel, overlay);
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timeout.Tick += (_, _) => { timeout.Stop(); Shutdown(24); };
            timeout.Start();
            Dispatcher.BeginInvoke(() =>
            {
                if (action == "dialog")
                    Dispatcher.BeginInvoke(() =>
                    {
                        var dialog = Windows.OfType<CloseBehaviorDialog>().Single();
                        dialog.Opacity = 0;
                        ((CheckBox)dialog.FindName("RememberChoice")).IsChecked = true;
                        ((Button)dialog.FindName("ExitButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }, DispatcherPriority.Loaded);
                if (action == "tray")
                {
                    // Let the probe's outer catch report UI failures without the production error dialog.
                    Dispatcher.UnhandledExceptionFilter += (_, error) => error.RequestCatch = false;
                    WindowUiScale.Initialize(new SidebarAppearance { UiScale = 1.25 });
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(panel).Handle;
                    _tray.Toggle();
                    User32.SendMessage(hwnd, 0x8000 + 73, IntPtr.Zero, (IntPtr)0x202);
                    if (panel.IsAssistantHidden) { Shutdown(29); return; }
                    _tray.Toggle();
                    var menu = (ContextMenu)typeof(AssistantTray).GetField("_menu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(_tray)!;
                    void OpenFromTray(int notification, Action next) => Dispatcher.BeginInvoke(() =>
                    {
                        User32.SendMessage(hwnd, 0x8000 + 73, IntPtr.Zero, (IntPtr)notification);
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (!panel.IsAssistantHidden || !menu.IsOpen || !menu.IsLoaded || menu.ActualWidth <= 0
                                || Math.Abs(menu.LayoutTransform.Value.M11 - 1.25) > .02)
                            {
                                Console.Error.WriteLine($"notification={notification:X}, hidden={panel.IsAssistantHidden}, open={menu.IsOpen}, loaded={menu.IsLoaded}, width={menu.ActualWidth}, scale={menu.LayoutTransform.Value.M11}");
                                Shutdown(30);
                                return;
                            }
                            next();
                        }, DispatcherPriority.ApplicationIdle);
                    }, DispatcherPriority.ApplicationIdle);
                    OpenFromTray(0x7B, () =>
                    {
                        menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "打开助手")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                        if (panel.IsAssistantHidden || menu.IsOpen) { Shutdown(31); return; }
                        _tray.Toggle();
                        OpenFromTray(0x205, () => menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "退出程序"))
                            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
                    });
                    return;
                }
                if (action == "system")
                    User32.SendMessage(new System.Windows.Interop.WindowInteropHelper(panel).Handle, 0x0112, (IntPtr)0xF060, IntPtr.Zero);
                else ((Button)panel.FindName("CloseAssistantButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, DispatcherPriority.ApplicationIdle);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (action == "dialog" && File.ReadAllText(Path.Combine(folder, "close-behavior.json")) != "\"Exit\"")
                e.ApplicationExitCode = 26;
            _tray?.Dispose();
            base.OnExit(e);
            ExitObserved = true;
        }
    }
}
