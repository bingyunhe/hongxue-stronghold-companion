// Modified for ASP Assistant Arknights on 2026-11-11; see NOTICE.
using ASPAssistant.Core.Interop;
using ASPAssistant.Core.Models;
using MaaFramework.Binding;
using MaaFramework.Binding.Buffers;

namespace ASPAssistant.Core.Services;

/// <summary>Shares the selected screenshot source and desktop viewport with all consumers.</summary>
public sealed class GameConnectionService : IDisposable
{
    private readonly object _captureLock = new();
    private readonly object _windowLock = new();
    private static long _nextRevision;
    private long _captureRevision = Interlocked.Increment(ref _nextRevision);
    private MaaAdbController? _controller;
    private DateTime _retryAfter;
    private IntPtr _window;
    private uint _windowProcessId;
    private bool _disposed;
    private string _status = "PC 窗口截图";
    private int _screenWidth;
    private int _screenHeight;
    private string _windowTitle;
    private IntPtr _lastForeground;
    private int _refreshDisplay;
    private DateTime _nextDisplayRead;
    private AdbGameDisplay? _display;
    private readonly Func<GameConnectionSettings, IReadOnlyList<AdbGameDisplay>> _findDisplays;
    private Task<AdbGameDisplay>? _displayRead;
    private long _displayReadRevision;
    private IntPtr _confirmedWindow;
    private bool _awaitingFocusedDisplay;

    public long CaptureRevision => Volatile.Read(ref _captureRevision);
    public AdbGameDisplay? CurrentDisplay => _display;
    internal Task? PendingDisplayRead => _displayRead;

    public GameConnectionSettings Settings { get; private set; }
    public bool IsAdb => Settings.Source == GameCaptureSource.Adb;
    public string Status => Volatile.Read(ref _status);

    public GameConnectionService(GameConnectionSettings? settings = null, IntPtr selectedWindow = default)
        : this(settings ?? new(), selectedWindow, AdbGameScreens.Find) { }

    internal GameConnectionService(GameConnectionSettings settings, IntPtr selectedWindow,
        Func<GameConnectionSettings, IReadOnlyList<AdbGameDisplay>> findDisplays)
    {
        Settings = settings;
        _findDisplays = findDisplays;
        _windowTitle = Settings.WindowTitle;
        _window = selectedWindow;
        if (_window != IntPtr.Zero)
        {
            User32.GetWindowThreadProcessId(_window, out _windowProcessId);
            _windowTitle = User32.GetWindowTitle(_window);
        }
        if (IsAdb) _status = "等待 ADB 连接";
    }

    public IntPtr FindGameWindow() => FindGameWindow(User32.GetForegroundWindow());

    internal IntPtr FindGameWindow(IntPtr foreground)
    {
        if (!IsAdb) return User32.FindArknightsWindow();
        lock (_windowLock)
        {
            if (Settings.FollowFocusedGame && foreground != _lastForeground)
            {
                _lastForeground = foreground;
                // Desktop association is immediate; Android screen confirmation runs separately.
                if (foreground != IntPtr.Zero && foreground != _window && _window != IntPtr.Zero && User32.IsWindow(_window))
                {
                    User32.GetWindowThreadProcessId(foreground, out var processId);
                    if (processId == _windowProcessId && GetWindowKind(foreground) == GetWindowKind(_window)
                        && GetWindowKind(foreground) != EmulatorKind.Unknown)
                    {
                        _window = foreground;
                        _windowTitle = User32.GetWindowTitle(foreground);
                        Settings = Settings with { WindowTitle = _windowTitle };
                        _awaitingFocusedDisplay = _window != _confirmedWindow;
                        Interlocked.Exchange(ref _captureRevision, Interlocked.Increment(ref _nextRevision));
                        Interlocked.Exchange(ref _refreshDisplay, 1);
                    }
                }
            }
            if (_window != IntPtr.Zero && User32.IsWindow(_window))
            {
                User32.GetWindowThreadProcessId(_window, out var processId);
                if (processId == _windowProcessId && User32.GetWindowTitle(_window) == _windowTitle)
                    return _window;
            }
            // A closed instance must not silently attach to another one with the same title.
            if (_window != IntPtr.Zero) return IntPtr.Zero;
            var matches = FindGameWindows().Where(w => w.Name == Settings.WindowTitle
                && (Settings.Emulator == EmulatorKind.Unknown || GetWindowKind(w.Handle) == Settings.Emulator)).ToList();
            if (matches.Count != 1) return IntPtr.Zero;
            _window = matches[0].Handle;
            User32.GetWindowThreadProcessId(_window, out _windowProcessId);
            return _window;
        }
    }

    // The injected query is the ADB process boundary; tests can hold it pending without starting an emulator.
    internal bool RefreshDisplay(bool waitForInitialDisplay = false)
    {
        lock (_captureLock)
        {
            if (_disposed) return false;
            if (_displayRead is null && (_display is null || DateTime.UtcNow >= _nextDisplayRead || Volatile.Read(ref _refreshDisplay) != 0))
            {
                Interlocked.Exchange(ref _refreshDisplay, 0);
                var settings = Settings;
                _displayReadRevision = CaptureRevision;
                _displayRead = Task.Run(() => AdbGameScreens.Select(_findDisplays(settings), settings));
            }
            if (_displayRead is { } read && (read.IsCompleted || waitForInitialDisplay))
            {
                _displayRead = null;
                var display = read.GetAwaiter().GetResult();
                lock (_windowLock)
                {
                    if (_displayReadRevision != CaptureRevision)
                    {
                        Interlocked.Exchange(ref _refreshDisplay, 1);
                        return false;
                    }
                    _nextDisplayRead = DateTime.UtcNow.AddSeconds(1);
                    if (_awaitingFocusedDisplay && (!display.IsFocused || _display is not null && _confirmedWindow != _window
                        && _display.DisplayId == display.DisplayId && _display.PackageName == display.PackageName && _display.PhysicalDisplayId == display.PhysicalDisplayId))
                    {
                        _status = "窗口已关联，等待 Android 焦点切换…";
                        return false;
                    }
                    if (_display is null || _display.DisplayId != display.DisplayId || _display.PackageName != display.PackageName
                        || _display.PhysicalDisplayId != display.PhysicalDisplayId)
                    {
                        _controller?.Dispose(); _controller = null;
                        Interlocked.Exchange(ref _captureRevision, Interlocked.Increment(ref _nextRevision));
                    }
                    _display = display;
                    _confirmedWindow = _window;
                    _awaitingFocusedDisplay = false;
                    if (_window != IntPtr.Zero) _windowTitle = User32.GetWindowTitle(_window);
                    Settings = Settings with { AndroidPackageName = display.PackageName, AndroidDisplayId = display.DisplayId, WindowTitle = _windowTitle };
                }
            }
            if (_awaitingFocusedDisplay || _display is null)
            {
                _status = "窗口已关联，正在后台确认游戏屏幕…";
                return false;
            }
            return true;
        }
    }

    public static IReadOnlyList<DesktopWindowInfo> FindGameWindows()
    {
        using var windows = MaaToolkit.Shared.Desktop.Window.Find();
        return windows.Where(w => User32.IsWindowVisible(w.Handle)
                && GetWindowKind(w.Handle) != EmulatorKind.Unknown)
            .Select(w => new DesktopWindowInfo(w.Handle, w.Name, w.ClassName,
                w.ScreencapMethod, w.MouseMethod, w.KeyboardMethod)).ToList();
    }

    public static EmulatorKind GetWindowKind(IntPtr window)
    {
        var display = User32.FindChildWindow(window, "nemudisplay");
        if (display != IntPtr.Zero && User32.GetAncestor(display, 2) == window) return EmulatorKind.MuMu;
        display = User32.FindChildWindow(window, "TheRender");
        return display != IntPtr.Zero && User32.GetAncestor(display, 2) == window ? EmulatorKind.LDPlayer : EmulatorKind.Unknown;
    }

    public RECT? GetViewport(IntPtr window)
    {
        if (window == IntPtr.Zero || User32.IsIconic(window)) return null;
        if (!IsAdb) return User32.GetClientRectScreen(window);
        var display = User32.FindChildWindow(window, "nemudisplay");
        if (display == IntPtr.Zero) display = User32.FindChildWindow(window, "TheRender");
        var bounds = display == IntPtr.Zero ? null : User32.GetClientRectScreen(display);
        return bounds is { Width: > 0, Height: > 0 } rect
            ? WindowGeometry.FitAspectRatio(rect, 16, 9) : null;
    }

    /// <summary>Captures the selected ADB device; its dimensions also bound information-panel scrolling.</summary>
    public byte[]? CaptureAdbScreen(int maxWidth, bool waitForInitialDisplay = false)
    {
        lock (_captureLock)
        {
            if (_disposed || !IsAdb || DateTime.UtcNow < _retryAfter) return null;
            try
            {
                FindGameWindow();
                if (_controller is null)
                {
                    if (!File.Exists(Settings.AdbPath))
                        throw new IOException("请选择有效的 adb.exe 路径");
                    if (string.IsNullOrWhiteSpace(Settings.AdbSerial))
                        throw new IOException("请填写 ADB 连接地址或选择发现的设备");
                }
                if (!RefreshDisplay(waitForInitialDisplay)) return null;
                var captureRevision = CaptureRevision;
                if (_controller is null)
                {
                    _status = "正在连接游戏屏幕…";
                    _controller = new MaaAdbController(Settings.AdbPath, Settings.AdbSerial,
                        AdbScreencapMethods.Encode, AdbInputMethods.AdbShell, AdbGameScreens.ControllerConfig(_display!),
                        agentPath: Path.Combine(AppContext.BaseDirectory, "MaaAgentBinary"), link: LinkOption.None);
                    if (_controller.LinkStart().Wait() != MaaJobStatus.Succeeded)
                        throw new IOException("ADB 连接失败，请检查设备与连接地址");
                    _controller.SetOption_ScreenshotUseRawSize(true);
                }

                if (_controller.Screencap().Wait() != MaaJobStatus.Succeeded)
                    throw new IOException("ADB 截图失败，请检查模拟器与连接地址");
                using var image = new MaaImageBuffer();
                if (!_controller.GetCachedImage(image) || image.IsEmpty)
                    throw new IOException("ADB 未返回有效画面");
                var width = image.Width;
                var height = image.Height;
                _screenWidth = width;
                _screenHeight = height;
                if (!WindowGeometry.IsSupportedGameAspectRatio(width, height))
                {
                    _status = $"当前画面 {width}×{height}，请设置为横屏 16∶9";
                    return null;
                }
                if (width > maxWidth)
                    image.TryResize(maxWidth, (int)Math.Round(height * (double)maxWidth / width));
                if (!image.TryGetEncodedData(out byte[]? png) || png is null)
                    throw new IOException("ADB 画面编码失败");
                FindGameWindow();
                if (captureRevision != CaptureRevision) return null;
                _status = $"ADB 已连接 · {_display!.Label} · {width}×{height}";
                _retryAfter = default;
                return png;
            }
            catch (Exception ex)
            {
                _status = waitForInitialDisplay ? ex.Message : $"{ex.Message}（5 秒后重试）";
                AppLogger.Warn("AdbCapture", _status);
                _controller?.Dispose();
                _controller = null;
                if (_display is not null)
                {
                    _display = null;
                    Interlocked.Exchange(ref _captureRevision, Interlocked.Increment(ref _nextRevision));
                }
                _retryAfter = DateTime.UtcNow.AddSeconds(5);
                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (_captureLock)
        {
            _disposed = true;
            _controller?.Dispose();
            _controller = null;
        }
    }

    public bool ScrollInformationPanelDown()
        => SendAdbGesture((width, height) => ((int)(width * .35), (int)(height * .72),
            (int)(width * .35), (int)(height * .64), 200));

    internal bool ScrollEnemyPanel(System.Drawing.Point point, System.Drawing.Size frame, long revision)
    {
        if (IsAdb)
            return SendAdbGesture((w, h) =>
            {
                int x = point.X * w / frame.Width, y = point.Y * h / frame.Height;
                return (x, y, x, y - h / 9, 220);
            }, revision);
        var window = FindGameWindow();
        if (CaptureRevision != revision || window == IntPtr.Zero || User32.GetClientRectScreen(window) is not { } bounds
            || Math.Abs(bounds.Width * (double)frame.Height / frame.Width - bounds.Height) > 2) return false;
        var client = new System.Drawing.Point(point.X * bounds.Width / frame.Width, point.Y * bounds.Height / frame.Height);
        return User32.CheckClickTarget(window, bounds.Left + client.X, bounds.Top + client.Y) == ClickResult.Sent
            && new MaaScrollService().TryScrollAtPoint(window, -120, client);
    }

    internal bool CanRetryCapturedPoint(System.Drawing.Point point, int width, int height)
    {
        var window = FindGameWindow();
        return IsAdb || (window != IntPtr.Zero && User32.GetClientRectScreen(window) is { } bounds
            && Math.Abs(bounds.Width * (double)height / width - bounds.Height) <= 2
            && User32.CheckClickTarget(window, bounds.Left + point.X * bounds.Width / width,
                bounds.Top + point.Y * bounds.Height / height) == ClickResult.Sent);
    }

    internal ClickResult ClickCapturedPoint(System.Drawing.Point point, int width, int height, long captureRevision)
    {
        var window = FindGameWindow();
        if (CaptureRevision != captureRevision) return ClickResult.StaleCapture;
        if (IsAdb)
            return SendAdbGesture((nativeWidth, nativeHeight) =>
            {
                int x = point.X * nativeWidth / width, y = point.Y * nativeHeight / height;
                return (x, y, x, y, 35);
            }, captureRevision) ? ClickResult.Sent : ClickResult.AdbUnconfirmed;
        if (window == IntPtr.Zero || User32.GetClientRectScreen(window) is not { } bounds) return ClickResult.MissingWindow;
        if (Math.Abs(bounds.Width * (double)height / width - bounds.Height) > 2) return ClickResult.SizeChanged;
        return User32.ClickClientPoint(window, bounds, point.X * bounds.Width / width, point.Y * bounds.Height / height);
    }

    private bool SendAdbGesture(Func<int, int, (int X1, int Y1, int X2, int Y2, int Duration)> gesture, long? expectedRevision = null)
    {
        lock (_captureLock)
        {
            if (_disposed || _controller is null) return false;
            FindGameWindow();
            var revision = CaptureRevision;
            if (_awaitingFocusedDisplay || expectedRevision is { } expected && expected != revision) return false;
            var display = AdbGameScreens.Select(AdbGameScreens.Find(Settings), Settings);
            FindGameWindow();
            if (revision != CaptureRevision || _display is null || display.DisplayId != _display.DisplayId || display.PackageName != _display.PackageName
                || display.PhysicalDisplayId != _display.PhysicalDisplayId)
            {
                Interlocked.Exchange(ref _refreshDisplay, 1);
                return false;
            }
            var (x1, y1, x2, y2, duration) = gesture(_screenWidth, _screenHeight);
            return _controller.Swipe(x1, y1, x2, y2, duration).Wait() == MaaJobStatus.Succeeded;
        }
    }
}
