using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ASPAssistant.App.Windows;
using ASPAssistant.Core;

namespace ASPAssistant.App;

internal sealed class AssistantTray : IDisposable
{
    private const int CallbackMessage = 0x8000 + 73;
    private readonly SidePanelWindow _panel;
    private readonly Window _overlay;
    private readonly HwndSource _source;
    private readonly uint _taskbarCreated;
    private readonly List<(Window Window, IntPtr Handle)> _hiddenWindows = [];
    private readonly ContextMenu _menu = new() { Placement = PlacementMode.MousePoint };
    private NotifyIconData _icon;
    private bool _added;
    private bool _focusHideRequested;
    private bool _hiddenByFocus;
    public event Action? VisibilityChanged;

    public AssistantTray(SidePanelWindow panel, Window overlay)
    {
        _panel = panel;
        _overlay = overlay;
        _source = HwndSource.FromHwnd(new WindowInteropHelper(panel).Handle);
        _source.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        if (_taskbarCreated == 0) AppLogger.Warn("Tray", "无法注册任务栏恢复通知，托盘恢复可使用快捷键");
        _icon = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _source.Handle, Id = 1,
            Flags = 1 | 2 | 4, CallbackMessage = CallbackMessage,
            Tip = "鸿雪·卫戍助手 · 左键打开，右键更多", Info = "", InfoTitle = ""
        };
        var open = new MenuItem { Header = "打开助手" };
        open.Click += (_, _) => Restore();
        var exit = new MenuItem { Header = "退出程序" };
        exit.Click += (_, _) => Application.Current.Shutdown();
        _menu.Items.Add(open);
        _menu.Items.Add(exit);
        var icons = new IntPtr[1];
        if (ExtractIconEx(Path.Combine(AppContext.BaseDirectory, "HongxueCompanion.exe"), 0, null, icons, 1) == 1)
            _icon.Icon = icons[0];
        panel.ToggleAssistantVisibilityRequested += Toggle;
        panel.FocusHideRequested += OnFocusHideRequested;
        panel.TrayNoticeRequested += ShowTrayNotice;
    }

    public void Toggle()
    {
        if (_panel.IsAssistantHidden) { Restore(); return; }
        Hide(false);
    }

    private void OnFocusHideRequested(bool hide)
    {
        if (_focusHideRequested == hide) return;
        _focusHideRequested = hide;
        if (hide && !_panel.IsAssistantHidden) Hide(true);
        else if (!hide && _hiddenByFocus) Restore();
    }

    private void Hide(bool forFocus)
    {
        if (!AddIcon())
        {
            _panel.ShowToast("无法创建托盘图标，助手保持显示，请稍后重试");
            return;
        }
        _added = true;
        // 保持 WPF 模态状态，避免 Hide() 提前结束 ShowDialog() 并丢失编辑内容。
        _hiddenWindows.Clear();
        foreach (Window window in Application.Current.Windows)
        {
            if (window == _overlay || window is TrackingGuideWindow || (!window.IsVisible && window != _panel)) continue;
            _hiddenWindows.Add((window, new WindowInteropHelper(window).Handle));
        }
        _hiddenByFocus = forFocus;
        _panel.IsAssistantHidden = true;
        _panel.GuideTrayChanged(true);
        foreach (var window in _hiddenWindows) ShowWindow(window.Handle, 0);
        VisibilityChanged?.Invoke();
    }

    public void Restore()
    {
        if (!_panel.IsAssistantHidden) return;
        _hiddenByFocus = false;
        _panel.IsAssistantHidden = false;
        foreach (var window in _hiddenWindows)
        {
            if (window.Window == _panel && !window.Window.IsVisible)
            {
                var activate = _panel.ShowActivated;
                _panel.ShowActivated = false;
                _panel.Show();
                _panel.ShowActivated = activate;
            }
            if (window.Window.IsVisible && PresentationSource.FromVisual(window.Window) is HwndSource source
                && source.Handle == window.Handle)
                ShowWindow(window.Handle, 8); // SW_SHOWNA：保留各窗口原先的最小化状态。
        }
        _hiddenWindows.Clear();
        _panel.GuideTrayChanged(false);
        RemoveIcon();
        VisibilityChanged?.Invoke();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage && _added)
        {
            switch (lParam.ToInt64() & 0xFFFF)
            {
                case 0x205: // WM_RBUTTONUP
                case 0x7B:  // WM_CONTEXTMENU (also sent for keyboard activation)
                    if (!_menu.IsOpen)
                    {
                        SetForegroundWindow(hwnd);
                        _menu.IsOpen = true;
                    }
                    handled = true;
                    break;
                case 0x202: // WM_LBUTTONUP
                case 0x400: // NIN_SELECT
                case 0x401: // NIN_KEYSELECT
                    Restore();
                    handled = true;
                    break;
            }
        }
        if (_taskbarCreated != 0 && (uint)msg == _taskbarCreated && _panel.IsAssistantHidden)
        {
            _added = AddIcon();
            if (!_added)
            {
                Restore();
                _panel.ShowToast("托盘图标恢复失败，已显示助手");
            }
        }
        return IntPtr.Zero;
    }

    private void ShowTrayNotice(string message)
    {
        var notice = _icon;
        notice.Flags = 0x10; // NIF_INFO
        notice.InfoTitle = "攻略讲解"; notice.Info = message;
        ShellNotifyIcon(1, ref notice);
    }

    private bool AddIcon()
    {
        if (_icon.Icon == IntPtr.Zero || !ShellNotifyIcon(0, ref _icon)) return false;
        _icon.TimeoutOrVersion = 4;
        if (ShellNotifyIcon(4, ref _icon)) return true;
        ShellNotifyIcon(2, ref _icon);
        return false;
    }

    private void RemoveIcon()
    {
        _menu.IsOpen = false;
        if (_added) ShellNotifyIcon(2, ref _icon);
        _added = false;
    }

    public void Dispose()
    {
        _panel.ToggleAssistantVisibilityRequested -= Toggle;
        _panel.FocusHideRequested -= OnFocusHideRequested;
        _panel.TrayNoticeRequested -= ShowTrayNotice;
        if (!_source.IsDisposed) _source.RemoveHook(WndProc);
        RemoveIcon();
        if (_icon.Icon != IntPtr.Zero) DestroyIcon(_icon.Icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[] small, uint count);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
}
