// Modified for ASP Assistant Arknights on 2026-11-11; see NOTICE.
using System.Diagnostics;
using ASPAssistant.Core.Interop;
using ASPAssistant.Core.Models;
using MaaFramework.Binding;
using MaaFramework.Binding.Buffers;

namespace ASPAssistant.Core.Services;

public enum GameConnectionState { Pc, Connecting, Connected, Failed }
public sealed record GameConnectionStatus(GameConnectionState State, string Message, string? CaptureDetails = null);

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
    private GameConnectionStatus _status = new(GameConnectionState.Pc, "PC 窗口截图");
    private int _screenWidth;
    private int _screenHeight;
    private string _windowTitle;
    private IntPtr _lastForeground;
    private int _refreshDisplay;
    private DateTime _nextDisplayRead;
    private AdbGameDisplay? _display;
    private readonly Func<GameConnectionSettings, IReadOnlyList<AdbGameDisplay>> _findDisplays;
    private sealed record DisplaySelection(AdbGameDisplay Display, bool UniquePackage);
    private Task<DisplaySelection>? _displayRead;
    private long _displayReadRevision;
    private IntPtr _confirmedWindow;
    private bool _awaitingFocusedDisplay;
    private bool _uniquePackage;
    private bool _usingMuMuEnhanced;
    private string? _enhancedFallbackReason;
    private string? _lastEnhancedFailure;
    private DateTime _enhancedRetryAfter;

    public long CaptureRevision => Volatile.Read(ref _captureRevision);
    public AdbGameDisplay? CurrentDisplay => _display;
    internal Task? PendingDisplayRead => _displayRead;

    public GameConnectionSettings Settings { get; private set; }
    public bool IsAdb => Settings.Source == GameCaptureSource.Adb;
    public GameConnectionStatus ConnectionStatus => Volatile.Read(ref _status);
    public string Status => ConnectionStatus.Message;

    private void SetStatus(GameConnectionState state, string message, string? captureDetails = null)
        => Volatile.Write(ref _status, new GameConnectionStatus(state, message, captureDetails));

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
        if (IsAdb) SetStatus(GameConnectionState.Connecting, "等待 ADB 连接");
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
    internal bool RefreshDisplay()
    {
        lock (_captureLock)
        {
            if (_disposed) return false;
            if (_displayRead is null && (_display is null || DateTime.UtcNow >= _nextDisplayRead || Volatile.Read(ref _refreshDisplay) != 0))
            {
                Interlocked.Exchange(ref _refreshDisplay, 0);
                var settings = Settings;
                _displayReadRevision = CaptureRevision;
                _displayRead = Task.Run(() =>
                {
                    var displays = _findDisplays(settings);
                    var display = AdbGameScreens.Select(displays, settings);
                    return new DisplaySelection(display, displays.Count(d => d.PackageName == display.PackageName) == 1);
                });
            }
            if (_displayRead is { IsCompleted: true } read)
            {
                _displayRead = null;
                var selection = read.GetAwaiter().GetResult();
                var display = selection.Display;
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
                        SetStatus(GameConnectionState.Connecting, "窗口已关联，等待 Android 焦点切换…");
                        return false;
                    }
                    if (_display is null || _display.DisplayId != display.DisplayId || _display.PackageName != display.PackageName
                        || _display.PhysicalDisplayId != display.PhysicalDisplayId
                        || Settings.PreferMuMuEnhancedCapture && Settings.Emulator == EmulatorKind.MuMu
                            && _uniquePackage != selection.UniquePackage)
                    {
                        _controller?.Dispose(); _controller = null;
                        _usingMuMuEnhanced = false;
                        Interlocked.Exchange(ref _captureRevision, Interlocked.Increment(ref _nextRevision));
                    }
                    _display = display;
                    _uniquePackage = selection.UniquePackage;
                    _confirmedWindow = _window;
                    _awaitingFocusedDisplay = false;
                    if (_window != IntPtr.Zero) _windowTitle = User32.GetWindowTitle(_window);
                    Settings = Settings with { AndroidPackageName = display.PackageName, AndroidDisplayId = display.DisplayId, WindowTitle = _windowTitle };
                }
            }
            if (_awaitingFocusedDisplay || _display is null)
            {
                SetStatus(GameConnectionState.Connecting, "窗口已关联，正在后台确认游戏屏幕…");
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

    /// <summary>Wait for the initial display query when confirming a connection in the dialog.</summary>
    public async Task<byte[]?> CaptureForConnectionTestAsync(int maxWidth)
    {
        var frame = await Task.Run(() => CaptureAdbScreen(maxWidth)).ConfigureAwait(false);
        if (frame is not null) return frame;

        Task? pending;
        lock (_captureLock) pending = _displayRead;
        if (pending is null) return null;
        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (_captureLock) SetStatus(GameConnectionState.Failed, "确认游戏屏幕超时，请检查模拟器与连接地址");
            return null;
        }
        catch
        {
            // The next capture consumes the completed task and reports its error normally.
        }
        return await Task.Run(() => CaptureAdbScreen(maxWidth)).ConfigureAwait(false);
    }

    /// <summary>Captures the selected ADB device; its dimensions also bound information-panel scrolling.</summary>
    public byte[]? CaptureAdbScreen(int maxWidth)
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
                if (!RefreshDisplay()) return null;
                var captureRevision = CaptureRevision;
                if (_controller is null)
                {
                    SetStatus(GameConnectionState.Connecting, "正在连接游戏屏幕…");
                    ConnectCaptureController();
                }

                byte[]? png;
                int width, height;
                long captureTimeMs;
                try
                {
                    var started = Stopwatch.GetTimestamp();
                    png = ReadControllerFrame(_controller!, maxWidth, out width, out height);
                    captureTimeMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (png is null && _usingMuMuEnhanced)
                        throw new IOException($"MuMu 增强截图返回不支持的画面尺寸 {width}×{height}");
                }
                catch (Exception ex) when (_usingMuMuEnhanced)
                {
                    // The framework selects one screencap unit at link time; it cannot switch units
                    // after a later failure. Replace it with an Encode-only controller explicitly.
                    RememberEnhancedFailure("MuMu 增强截图画面不可用", ex);
                    _controller?.Dispose();
                    _controller = null;
                    _usingMuMuEnhanced = false;
                    _controller = ConnectController(AdbScreencapMethods.Encode, null);
                    var started = Stopwatch.GetTimestamp();
                    png = ReadControllerFrame(_controller, maxWidth, out width, out height);
                    captureTimeMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }
                _screenWidth = width;
                _screenHeight = height;
                if (png is null)
                {
                    SetStatus(GameConnectionState.Failed, $"当前画面 {width}×{height}，请设置为横屏 16∶9");
                    return null;
                }
                FindGameWindow();
                if (captureRevision != CaptureRevision) return null;
                var backend = _usingMuMuEnhanced ? "MuMu 增强截图" : _enhancedFallbackReason is null
                    ? "普通 ADB" : $"普通 ADB（{_enhancedFallbackReason}）";
                var captureDetails = $"{backend} · 截图耗时 · {captureTimeMs} ms";
                SetStatus(GameConnectionState.Connected,
                    $"ADB 已连接 · {backend} · {_display!.Label} · {width}×{height} · 截图耗时 · {captureTimeMs} ms", captureDetails);
                _retryAfter = default;
                return png;
            }
            catch (Exception ex)
            {
                SetStatus(GameConnectionState.Failed, $"{ex.Message}（5 秒后重试）");
                AppLogger.Warn("AdbCapture", Status);
                _controller?.Dispose();
                _controller = null;
                _usingMuMuEnhanced = false;
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

    private void ConnectCaptureController()
    {
        _usingMuMuEnhanced = false;
        _enhancedFallbackReason = null;
        if (Settings.PreferMuMuEnhancedCapture && Settings.Emulator == EmulatorKind.MuMu)
        {
            if (_display!.DisplayId != 0)
                _enhancedFallbackReason = "增强截图仅支持模拟器主屏幕（屏幕 0）";
            else if (!_uniquePackage)
                _enhancedFallbackReason = "存在同包名的多个游戏屏幕";
            else if (DateTime.UtcNow < _enhancedRetryAfter)
                _enhancedFallbackReason = _lastEnhancedFailure;
            else
            {
                var config = EmulatorDiscovery.FindMuMuCaptureConfig(Settings.AdbPath, Settings.AdbSerial, out var reason);
                if (config is null) RememberEnhancedFailure(reason);
                else
                {
                    try
                    {
                        // Only MuMu extras are enabled in this config, so a successful controller
                        // using this single method really is the MuMu backend.
                        _controller = ConnectController(AdbScreencapMethods.EmulatorExtras, config);
                        _usingMuMuEnhanced = true;
                        _enhancedRetryAfter = default;
                        return;
                    }
                    catch (Exception ex)
                    {
                        RememberEnhancedFailure("MuMu 增强截图连接失败", ex);
                    }
                }
            }
        }
        _controller = ConnectController(AdbScreencapMethods.Encode, null);
    }

    private void RememberEnhancedFailure(string reason, Exception? exception = null)
    {
        _lastEnhancedFailure = _enhancedFallbackReason = reason;
        // A fallback controller remains in use. On a later reconnect or display change,
        // avoid immediately retrying an unavailable SDK over and over.
        _enhancedRetryAfter = DateTime.UtcNow.AddMinutes(1);
        if (exception is not null) AppLogger.Warn("AdbCapture", $"{reason}：{exception.Message}");
    }

    private MaaAdbController ConnectController(AdbScreencapMethods method, MuMuCaptureConfig? mumu)
    {
        MaaAdbController? candidate = null;
        try
        {
            candidate = new MaaAdbController(Settings.AdbPath, Settings.AdbSerial,
                method, AdbInputMethods.AdbShell, AdbGameScreens.ControllerConfig(_display!, mumu),
                agentPath: Path.Combine(AppContext.BaseDirectory, "MaaAgentBinary"), link: LinkOption.None);
            if (candidate.LinkStart().Wait() != MaaJobStatus.Succeeded)
                throw new IOException("ADB 连接失败，请检查设备与连接地址");
            candidate.SetOption_ScreenshotUseRawSize(true);
            return candidate;
        }
        catch
        {
            candidate?.Dispose();
            throw;
        }
    }

    private static byte[]? ReadControllerFrame(MaaAdbController controller, int maxWidth, out int width, out int height)
    {
        if (controller.Screencap().Wait() != MaaJobStatus.Succeeded)
            throw new IOException("ADB 截图失败，请检查模拟器与连接地址");
        using var image = new MaaImageBuffer();
        if (!controller.GetCachedImage(image) || image.IsEmpty)
            throw new IOException("ADB 未返回有效画面");
        width = image.Width;
        height = image.Height;
        if (!WindowGeometry.IsSupportedGameAspectRatio(width, height)) return null;
        if (width > maxWidth)
            image.TryResize(maxWidth, (int)Math.Round(height * (double)maxWidth / width));
        if (!image.TryGetEncodedData(out byte[]? png) || png is null)
            throw new IOException("ADB 画面编码失败");
        return png;
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
    {
        lock (_captureLock)
        {
            if (_disposed || _controller is null) return false;
            FindGameWindow();
            var revision = CaptureRevision;
            if (_awaitingFocusedDisplay) return false;
            var display = AdbGameScreens.Select(AdbGameScreens.Find(Settings), Settings);
            FindGameWindow();
            if (revision != CaptureRevision || _display is null || display.DisplayId != _display.DisplayId || display.PackageName != _display.PackageName
                || display.PhysicalDisplayId != _display.PhysicalDisplayId)
            {
                Interlocked.Exchange(ref _refreshDisplay, 1);
                return false;
            }
            return _controller.Swipe((int)(_screenWidth * .35), (int)(_screenHeight * .72),
                (int)(_screenWidth * .35), (int)(_screenHeight * .64), 200).Wait() == MaaJobStatus.Succeeded;
        }
    }
}
