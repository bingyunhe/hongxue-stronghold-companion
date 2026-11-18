// Modified for ASP Assistant Regulation on 2026-11-13; see NOTICE.
using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;
using ASPAssistant.App.Views;
using ASPAssistant.App.Controls;
using ASPAssistant.Core;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Interop;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;

namespace ASPAssistant.App.Windows;

public partial class SidePanelWindow : Window
{
    public OperatorBrowseViewModel OperatorBrowseVm { get; }
    public EquipmentBrowseViewModel EquipmentBrowseVm { get; }
    public TrackingViewModel TrackingVm { get; }
    public GameStateViewModel GameStateVm { get; }
    public BanViewModel BanVm { get; }
    public EnemyViewModel EnemyVm { get; }
    public LineupViewModel LineupVm { get; }
    public LineupPlannerViewModel PlannerVm { get; private set; } = null!;

    public event Action? ManualScanRequested;
    public event Action? GameConnectionRequested;
    public event Action? TopmostPreferenceChanged;
    public event Action<string, bool>? BanToggleRequested;

    private void OnProjectAddressNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Win32Exception) { ShowToast($"无法打开默认浏览器，请手动访问：{e.Uri.AbsoluteUri}"); }
    }

    private readonly LineupShareService _lineupShareService;
    private readonly IReadOnlyList<Operator> _allOperators;
    private readonly IReadOnlyList<Equipment> _allEquipment;
    private readonly IReadOnlyList<Strategy> _allStrategies;
    private readonly LineupPlanner _lineupPlanner;
    private readonly IReadOnlyList<CovenantInfo> _covenants;
    private readonly DispatcherTimer _toastHideTimer;

    private readonly UpdateService _updateService = new();
    private bool _isCheckingForUpdates;
    private bool _hasCheckedForUpdates;
    private readonly SettingsManager _settingsManager;
    private SidePanelPositionSettings _positionSettings;
    private RECT? _lastGameRect;
    private IntPtr _lastTopmostForeground;
    private MainWindowCloseBehavior _closeBehavior;
    private bool _closeRequestPending;
    public GameShopTheme GameTheme { get; private set; }
    public event Action<GameShopTheme>? GameThemeChanged;
    public event Action<bool>? AutomaticGameThemeChanged;
    public bool AutomaticGameTheme => AutomaticThemeToggle.IsChecked == true;
    private UpdateInfo? _pendingUpdate;
    private Task? _downloadTask;
    private bool _isDownloadReady;
    private UpdateDialog? _openUpdateDialog;
    private double _lastUpdateProgress;
    private string? _downloadError;

    // 用户意图追踪
    private double? _userHeightDip;       // 用户手动设置的逻辑高度，跨 DPI 时不反复取整
    private double _preferredWidthDip;
    private bool _isProgrammaticChange;   // 程序正在设置位置/尺寸，抑制事件响应
    private bool _isRestoring;            // 正在从最小化恢复，抑制事件响应
    private WindowState _previousWindowState;
    private WindowState? _stateBeforeGameMinimized;
    private bool _isUserMovingOrSizing;
    private RECT? _moveStartRect;

    // Global hotkey (Ctrl+Shift+/) for manual screenshot scan
    private const int HotkeyIdManualScan = 0x4153; // 'AS' — arbitrary unique app-scoped ID
    private HwndSource? _hwndSource;

    // 全屏状态追踪
    private bool _wasFullscreen;
    private bool _fullscreenBannerDismissed;

    public SidePanelWindow(
        OperatorBrowseViewModel operatorVm,
        EquipmentBrowseViewModel equipmentVm,
        TrackingViewModel trackingVm,
        GameStateViewModel gameStateVm,
        BanViewModel banVm,
        EnemyViewModel enemyVm,
        LineupViewModel lineupVm,
        LineupShareService lineupShareService,
        IReadOnlyList<Operator> allOperators,
        IReadOnlyList<Equipment> allEquipment,
        IReadOnlyList<Strategy> allStrategies,
        SettingsManager settingsManager,
        GameShopTheme gameTheme,
        SidePanelPositionSettings positionSettings,
        LineupPlanner? lineupPlanner = null,
        IReadOnlyList<CovenantInfo>? covenants = null,
        SidebarAppearance? appearance = null,
        MainWindowCloseBehavior closeBehavior = MainWindowCloseBehavior.Ask)
    {
        _appearance = appearance ?? new SidebarAppearance();
        OperatorBrowseVm = operatorVm;
        EquipmentBrowseVm = equipmentVm;
        TrackingVm = trackingVm;
        GameStateVm = gameStateVm;
        BanVm = banVm;
        EnemyVm = enemyVm;
        LineupVm = lineupVm;
        _lineupShareService = lineupShareService;
        _allOperators = allOperators;
        _allEquipment = allEquipment;
        _allStrategies = allStrategies;
        _lineupPlanner = lineupPlanner ?? new LineupPlanner(allOperators, covenants ?? []);
        _covenants = covenants ?? [];
        _settingsManager = settingsManager;
        _positionSettings = positionSettings;
        GameTheme = gameTheme;

        InitializeComponent();
        ConfigurationDirectoryText.Text = settingsManager.DirectoryPath;
        _closeBehavior = closeBehavior;
        CloseBehaviorSelector.SelectedIndex = (int)closeBehavior;
        UpdateCloseButtonTooltip();
        CloseBehaviorSelector.SelectionChanged += OnCloseBehaviorChanged;
        InitGameStartSettings(new());
        _preferredWidthDip = Width;
        if (positionSettings.Placement is { } placement)
        {
            _preferredWidthDip = Math.Clamp(placement.WidthDip, MinWidth, MaxWidth);
            Width = _preferredWidthDip;
            _userHeightDip = placement.HeightDip;
        }
        WindowUiScale.Register();
        for (int i = 0; i < MainTabs.Items.Count; i++)
            ((DependencyObject)((TabItem)MainTabs.Items[i]).Content).SetValue(WindowUiScale.PageTargetProperty, (UiScaleTarget)i);
        SyncUiScaleControls();
        ApplyUiScale();
        UiScaleSlider.ValueChanged += OnUiScaleValueChanged;
        CurrentVersionText.Text = $"当前版本  v{typeof(SidePanelWindow).Assembly.GetName().Version!.ToString(3)}";
        GameThemeSelector.SelectedIndex = (int)gameTheme;
        FollowModeThemeToggle.IsChecked = _appearance.FollowModeTheme;
        UiThemeSelector.SelectedIndex = (int)_appearance.Theme;
        UiThemeSelector.Visibility = _appearance.FollowModeTheme ? Visibility.Collapsed : Visibility.Visible;
        SyncDayNightSelector();
        DayNightSelector.SelectionChanged += OnDayNightChanged;
        FollowModeThemeToggle.Checked += OnFollowModeThemeChanged;
        FollowModeThemeToggle.Unchecked += OnFollowModeThemeChanged;
        trackingVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TrackingViewModel.TerminalLevel) or nameof(TrackingViewModel.IsTerminalLevelRestored))
                UpdateStatusChip();
        };
        UpdateStatusChip();
        MultiLineupTrackingToggle.IsChecked = trackingVm.TrackMultipleLineups;
        FreeSidePanelMovementToggle.IsChecked = positionSettings.FreeMovement;
        ForceTopmostToggle.IsChecked = positionSettings.ForceTopmost;
        UiThemeSelector.SelectionChanged += OnUiThemeChanged;
        GameThemeSelector.SelectionChanged += OnGameThemeChanged;
        MultiLineupTrackingToggle.Checked += OnMultipleLineupsChanged;
        MultiLineupTrackingToggle.Unchecked += OnMultipleLineupsChanged;
        FreeSidePanelMovementToggle.Checked += OnFreeSidePanelMovementChanged;
        FreeSidePanelMovementToggle.Unchecked += OnFreeSidePanelMovementChanged;
        ForceTopmostToggle.Checked += OnForceTopmostChanged;
        ForceTopmostToggle.Unchecked += OnForceTopmostChanged;

        _toastHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5),
        };
        _toastHideTimer.Tick += (_, _) => HideToast();

        SizeChanged     += OnSizeChanged;
        StateChanged    += OnStateChanged;
        Loaded += (_, _) =>
        {
            ApplyPageScale();
            if (_appearance.UiScale != 1) ApplyUiScale();
            if (!_positionSettings.FreeMovement || _positionSettings.Placement is not { } saved) return;
            _isProgrammaticChange = true;
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var work = User32.GetMonitorGeometry((int)saved.Left, (int)saved.Top).WorkArea;
                saved = saved with { WidthDip = _preferredWidthDip * _appearance.UiScale };
                // 第一次移动使 HWND 切换到记忆位置所在屏幕，再使用该屏的 DPI 恢复尺寸。
                User32.SetWindowBounds(hwnd, WindowGeometry.RestoreBounds(saved, work, User32.GetWindowDpiScale(hwnd)));
                User32.SetWindowBounds(hwnd, WindowGeometry.RestoreBounds(saved, work, User32.GetWindowDpiScale(hwnd)));
            }
            finally { _isProgrammaticChange = false; }
        };

        trackingVm.GameState = gameStateVm;

        OperatorView.DataContext = operatorVm;
        EquipmentView.DataContext = equipmentVm;
        TrackingView.SetOperators(allOperators);
        TrackingView.SetEquipment(allEquipment);
        TrackingView.SetCovenantCalculator(lineupVm.Calculator);
        TrackingView.DataContext = trackingVm;
        EnemyView.DataContext = enemyVm;
        RunBanView.DataContext = operatorVm;
        LineupView.DataContext = lineupVm;
        LineupView.SetTracking(trackingVm);
        bool CovenantBlocked(string name) => allOperators.Any(op => op.Name == name && CovenantBanRules.IsExcluded(op, banVm.BannedCovenants));
        bool Unavailable(string name) => banVm.IsBanned(name) || CovenantBlocked(name);
        operatorVm.SetBanChecker(Unavailable);
        WireLineupView();

        OperatorView.IsTrackedCheck = trackingVm.IsTracked;
        EquipmentView.IsTrackedCheck = trackingVm.IsTracked;

        OperatorView.IsBannedCheck = banVm.IsBanned;
        OperatorView.IsCovenantBannedCheck = CovenantBlocked;
        OperatorView.ManageCustomOperatorsRequested += async () =>
        {
            var dialog = new CustomOperatorSelectionWindow(allOperators.Where(op => op.IsCustom)) { Owner = this };
            await MinimizableDialog.ShowAsync(dialog, OperatorView);
            if (dialog.Result is null) return;
            try { await settingsManager.SaveCustomOperatorSelectionAsync(dialog.Result); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowToast($"自选干员保存失败：{ex.Message}");
                return;
            }
            var names = dialog.Result.ToHashSet(StringComparer.Ordinal);
            foreach (var op in allOperators.Where(op => op.IsCustom)) op.IsSelectedCustom = names.Contains(op.Name);
            operatorVm.LoadOperators(allOperators.ToList());
            PlannerVm.NotifyOperatorSelectionChanged();
            ShowToast($"已保存 {names.Count} 名自选干员");
        };

        OperatorView.TrackingRequested += (name, type) => trackingVm.AddTracking(name, type);
        OperatorView.UntrackingRequested += name => trackingVm.RemoveTracking(name);
        EquipmentView.TrackingRequested += (name, type) => trackingVm.AddTracking(name, type);
        EquipmentView.UntrackingRequested += name => trackingVm.RemoveTracking(name);
        TrackingView.RemoveTrackingRequested += name => trackingVm.RemoveTracking(name);
        TrackingView.ToggleAcquiredRequested += name => trackingVm.ToggleAcquired(name);
        TrackingView.ClearAllTrackingRequested += () =>
        {
            if (ConfirmDialog.Ask(this, "清空追踪", "清空所有干员和装备追踪目标？已拥有、精锐和数量记录会保留。", "清空追踪", danger: true))
                trackingVm.ClearAllTracking();
        };

        RunBanView.ClearBansRequested += () => banVm.ClearBans();
        RunBanView.ClearRunRequested += () =>
        {
            banVm.ClearRunBans();
            operatorVm.ClearCovenantHighlights();
            PlannerVm.OnlyCurrentRun = false;
        };
        RunBanView.ManualScanRequested += () => ManualScanRequested?.Invoke();
        OperatorView.BanToggleRequested += (name, banned) => BanToggleRequested?.Invoke(name, banned);

        banVm.BansChanged += () =>
        {
            Dispatcher.Invoke(() =>
            {
                OperatorView.RefreshBannedStates();
                operatorVm.NotifyBansChanged();
                UpdateBanSummary();
                UpdateLineupRunInfo();
            });
        };
        UpdateBanSummary();

        trackingVm.StageStateChanged += () =>
        {
            OperatorView.RefreshTrackingStates();
            EquipmentView.RefreshTrackingStates();
        };
        trackingVm.TrackedOperators.CollectionChanged += (_, _) =>
        {
            if (trackingVm.StageTargetsUpdating) return;
            OperatorView.RefreshTrackingStates();
            EquipmentView.RefreshTrackingStates();
        };
        trackingVm.TrackedEquipment.CollectionChanged += (_, _) =>
        {
            if (trackingVm.StageTargetsUpdating) return;
            OperatorView.RefreshTrackingStates();
            EquipmentView.RefreshTrackingStates();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource.AddHook(WndProc);
        SystemBorder.Hide(hwnd);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0112 && (wParam.ToInt64() & 0xFFF0) == 0xF060) // WM_SYSCOMMAND, SC_CLOSE.
        {
            handled = true;
            Dispatcher.BeginInvoke(new Action(async () => await RequestUserCloseAsync()));
            return IntPtr.Zero;
        }
        if (msg == User32.WM_ENTERSIZEMOVE)
        {
            _isUserMovingOrSizing = true;
            _moveStartRect = User32.ReadWindowRect(hwnd);
        }
        if (msg == User32.WM_EXITSIZEMOVE)
        {
            _isUserMovingOrSizing = false;
            var bounds = User32.ReadWindowRect(hwnd);
            if (_moveStartRect is { } start && bounds.Width == start.Width && bounds.Height == start.Height
                && (bounds.Left != start.Left || bounds.Top != start.Top))
            {
                var work = User32.GetMonitorGeometry(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2).WorkArea;
                User32.SetWindowBounds(hwnd, WindowGeometry.SnapBounds(bounds, work, User32.GetWindowDpiScale(hwnd), _lastGameRect));
            }
            _moveStartRect = null;
            var placement = CaptureSidePanelPlacement();
            _userHeightDip = placement.HeightDip;
            _positionSettings = _positionSettings with
            {
                Placement = placement,
                FollowOffsetDip = !_positionSettings.FreeMovement && _lastGameRect is { } game
                    ? WindowGeometry.CaptureFollowOffset(game, placement, User32.GetWindowDpiScale(hwnd))
                    : _positionSettings.FollowOffsetDip,
            };
            _ = SavePreferenceAsync(() => _settingsManager.SaveSidePanelPositionAsync(_positionSettings), FreeSidePanelMovementToggle);
        }
        if (msg == User32.WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            var hotkey = id == _assistantHotkeyId ? _assistantHotkey : id == _recognitionHotkeyId ? _recognitionHotkey : null;
            if (hotkey is null) return IntPtr.Zero;
            if (AssistantHotkeyBox.IsKeyboardFocusWithin) _ = SaveAssistantHotkeyAsync(hotkey);
            else if (RecognitionHotkeyBox.IsKeyboardFocusWithin) _ = SaveRecognitionHotkeyAsync(hotkey);
            else if (id == _assistantHotkeyId) ToggleAssistantVisibilityRequested?.Invoke();
            else ManualScanRequested?.Invoke();
            handled = true;
        }
        if (msg == 0x02E0) // Re-evaluate monitor bounds after WPF handles WM_DPICHANGED.
            Dispatcher.BeginInvoke(ApplyUiScale);
        return IntPtr.Zero;
    }

    /// <summary>对局信息 header: banned covenants, banned operators' portraits and the unusable count.</summary>
    private void UpdateBanSummary()
    {
        var byName = _allOperators.GroupBy(o => o.Name).ToDictionary(g => g.Key, g => g.First());
        var banned = BanVm.BannedNames.Concat(_allOperators.Where(op => CovenantBanRules.IsExcluded(op, BanVm.BannedCovenants)).Select(op => op.Name))
            .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => byName.TryGetValue(n, out var op) ? op : new Operator { Name = n }).ToList();
        RunBanView.SetBanSummary([.. BanVm.BannedCovenants], banned.Count, banned);
    }

    public void UpdateManualScanStatus(int pending)
        => RunBanView.UpdateManualScanStatus(pending);

    public void EnableCurrentRunFilters()
    {
        OperatorBrowseVm.OnlyCurrentRun = true;
        PlannerVm.OnlyCurrentRun = true;
    }

    /// <summary>Settings card: source in the title, live status below, icon tinted by state (PC grey, ADB green / red).</summary>
    public void UpdateGameConnectionStatus(bool isAdb, GameConnectionStatus status)
    {
        var failed = isAdb && status.State == GameConnectionState.Failed;
        var connected = isAdb && status.State == GameConnectionState.Connected;
        GameConnectionTitle.Text = isAdb ? "截图来源 · MuMu / ADB" : "截图来源 · PC 客户端";
        GameConnectionStatus.Text = status.Message;
        GameConnectionStatus.ToolTip = status.Message;
        GameConnectionGlyph.Text = !isAdb ? "PC" : failed ? "!" : connected ? "✓" : "…";
        var (fill, text) = !isAdb ? ("SectionBackgroundBrush", "TextSecondaryBrush")
            : failed ? ("DangerTintBrush", "DangerBrush")
            : connected ? ("SuccessTintBrush", "SuccessTextBrush") : ("AccentTintBrush", "AccentBrush");
        GameConnectionIcon.SetResourceReference(Border.BackgroundProperty, fill);
        GameConnectionGlyph.SetResourceReference(TextBlock.ForegroundProperty, text);
        GameConnectionStatus.SetResourceReference(TextBlock.ForegroundProperty, failed ? "DangerBrush" : connected ? "SuccessTextBrush" : "TextSecondaryBrush");
        GameConnectionButton.Content = failed ? "重新设置…" : "更改…";
    }

    private void OnGameConnection(object sender, RoutedEventArgs e) => GameConnectionRequested?.Invoke();

    public void SetBanProcessingMaskVisible(bool visible, string? message = null)
    {
        BanProcessingMask.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(message))
            BanProcessingMaskText.Text = message;
    }

    public async Task StartUpdateCheckAsync()
    {
        await Task.Delay(3000);
        await CheckForUpdatesAsync(isManual: false);
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(isManual: true);
    }

    internal async Task CheckForUpdatesAsync(bool isManual)
    {
        if (_isCheckingForUpdates || (!isManual && _hasCheckedForUpdates)) return;
        _hasCheckedForUpdates = true;

        if (_pendingUpdate is not null && (_downloadTask is { IsCompleted: false } || _isDownloadReady))
        {
            UpdateBanner.Visibility = Visibility.Visible;
            UpdateStatusText.Text = _isDownloadReady
                ? $"新版本 {_pendingUpdate.TagName} 已就绪，点击“立即重启”完成更新"
                : $"正在下载新版本 {_pendingUpdate.TagName}…";
            return;
        }

        if (_updateService.IsDevBuild)
        {
            UpdateStatusText.Text = "当前为开发测试版（0.0.0），不检查自动更新";
            return;
        }

        _isCheckingForUpdates = true;
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = "正在检查…";
        UpdateStatusText.Text = "正在检查更新…";
        try
        {
            var info = await _updateService.CheckForUpdateAsync();
            if (info is null)
            {
                UpdateStatusText.Text = _updateService.LastError is null
                    ? "当前已是最新版本"
                    : $"检查失败：{_updateService.LastError}，请稍后重试";
                if (_updateService.LastError is not null)
                    AppLogger.Warn("Update", $"Update check failed: {_updateService.LastError}");
                return;
            }

            if (!isManual && await _settingsManager.LoadSkippedVersionAsync() == info.TagName)
            {
                UpdateStatusText.Text = $"已跳过 {info.TagName}，可点击“检查更新”重新查看";
                return;
            }

            PresentAvailableUpdate(info);
        }
        finally
        {
            _isCheckingForUpdates = false;
            CheckUpdateButton.IsEnabled = true;
            CheckUpdateButton.Content = "检查更新";
        }
    }

    internal void PresentAvailableUpdate(UpdateInfo info)
    {
        _pendingUpdate = info;
        _downloadTask = null;
        _isDownloadReady = false;
        _downloadError = null;
        _lastUpdateProgress = 0;
        UpdateBannerText.Text = $"发现新版本 {info.TagName}";
        UpdateStatusText.Text = $"发现新版本 {info.TagName}，可查看说明并选择下载";
        UpdateActionIcon.Text = "↑ ";
        UpdateActionText.Text = "查看更新";
        UpdateBannerProgress.Visibility = Visibility.Collapsed;
        UpdateBannerPercent.Visibility = Visibility.Collapsed;
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void StartUpdateDownload()
    {
        if (_downloadTask is { IsCompleted: false } || _isDownloadReady) return;
        UpdateBannerText.Text = $"正在下载 {_pendingUpdate!.TagName}";
        UpdateStatusText.Text = $"正在下载新版本 {_pendingUpdate.TagName}…";
        UpdateActionText.Text = "查看进度";
        UpdateBannerProgress.Visibility = Visibility.Visible;
        _downloadTask = RunBackgroundDownloadAsync(_pendingUpdate);
    }

    private async Task RunBackgroundDownloadAsync(UpdateInfo info)
    {
        _lastUpdateProgress = 0;
        _downloadError = null;
        UpdateBannerProgress.Value = 0;
        UpdateBannerPercent.Text = "0%";
        UpdateBannerPercent.Visibility = Visibility.Visible;
        _openUpdateDialog?.ReportProgress(0);
        var progress = new Progress<double>(value =>
        {
            Dispatcher.Invoke(() =>
            {
                if (_downloadError is not null) return;
                _lastUpdateProgress = value;
                UpdateBannerProgress.Value = value * 100;
                UpdateBannerPercent.Text = $"{(int)Math.Round(value * 100)}%";
                _openUpdateDialog?.ReportProgress(value);
            });
        });

        try
        {
            await _updateService.DownloadAndApplyUpdateAsync(info, progress);

            _isDownloadReady = true;
            Dispatcher.Invoke(() =>
            {
                UpdateBannerProgress.Visibility = Visibility.Collapsed;
                UpdateBannerPercent.Text = "100%";
                UpdateBannerPercent.Visibility = Visibility.Visible;
                UpdateActionIcon.Text = "↻ ";
                UpdateActionText.Text = "立即重启";
                UpdateStatusText.Text = $"新版本 {info.TagName} 已就绪，点击“立即重启”完成更新（会自动关闭并重新打开）";
                _openUpdateDialog?.ReportProgress(1.0);
            });
        }
        catch (Exception ex)
        {
            var reason = UpdateService.DescribeError(ex);
            _downloadError = reason;
            AppLogger.Warn("Update", $"Download or apply failed: {reason}");
            Dispatcher.Invoke(() =>
            {
                UpdateBannerProgress.Visibility = Visibility.Collapsed;
                UpdateBannerPercent.Visibility = Visibility.Collapsed;
                UpdateBannerText.Text = "下载失败，查看详情";
                UpdateActionIcon.Text = "↺ ";
                UpdateActionText.Text = "重试";
                UpdateStatusText.Text = $"下载失败：{reason}，可在更新横幅中重试";
                _openUpdateDialog?.ReportFailure(reason);
            });
            _isDownloadReady = false;
        }
    }

    private void OnBannerAction(object sender, RoutedEventArgs e)
    {
        if (_isDownloadReady)
        {
            // 先让更新脚本接管，再退出本进程；脚本会等本进程结束后替换目录并启动新版。
            try
            {
                if (!_updateService.LaunchPendingUpdateScript())
                {
                    UpdateStatusText.Text = "更新文件已失效，请点击“检查更新”重新下载";
                    _isDownloadReady = false;
                    return;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            {
                AppLogger.Warn("Update", $"无法启动更新程序：{ex.Message}");
                UpdateStatusText.Text = $"无法启动更新程序：{ex.Message}。辅助未关闭，可点击“立即重启”重试。";
                return;
            }

            UpdateStatusText.Text = "正在退出以完成更新…";
            System.Windows.Application.Current.Shutdown();
            return;
        }

        if (_pendingUpdate is null) return;

        var dialog = new UpdateDialog(_pendingUpdate) { Owner = this };
        dialog.DownloadRequested += StartUpdateDownload;
        if (_downloadError is not null) dialog.ReportFailure(_downloadError);
        else if (_downloadTask is not null) dialog.ReportProgress(_lastUpdateProgress);
        _openUpdateDialog = dialog;
        try
        {
            dialog.ShowDialog();
        }
        finally
        {
            _openUpdateDialog = null;
        }
    }

    private async void OnSkipVersion(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null) return;
        if (_settingsManager is not null)
            await _settingsManager.SaveSkippedVersionAsync(_pendingUpdate.TagName);
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    public void SetGameMinimized(bool minimized)
    {
        WindowState target;
        if (minimized)
        {
            if (WindowState == WindowState.Minimized)
                return;
            _stateBeforeGameMinimized = WindowState;
            target = WindowState.Minimized;
        }
        else
        {
            if (_stateBeforeGameMinimized is not { } previous)
                return;
            _stateBeforeGameMinimized = null;
            if (WindowState != WindowState.Minimized)
                return;
            target = previous;
        }
        _isProgrammaticChange = true;
        try { WindowState = target; }
        finally { _isProgrammaticChange = false; }
    }

    public void ClearGamePosition() => _lastGameRect = null;

    public void UpdateTopmost(IntPtr game, IntPtr foreground)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var ownedFocus = foreground;
        while (ownedFocus != IntPtr.Zero && ownedFocus != hwnd)
            ownedFocus = User32.GetWindow(ownedFocus, 4); // GW_OWNER includes editor and selection dialogs.
        var topmost = game != IntPtr.Zero && User32.IsWindow(game) && !User32.IsIconic(game)
            && !IsAssistantHidden && WindowState == WindowState.Normal
            && (_positionSettings.ForceTopmost || foreground == game || ownedFocus == hwnd);
        // A topmost game can move above us when focus changes; raise without activating, once per transition.
        var refreshLayer = topmost && _positionSettings.ForceTopmost && foreground != _lastTopmostForeground;
        _lastTopmostForeground = foreground;
        if (!refreshLayer && Topmost == topmost && ((User32.GetWindowLong(hwnd, User32.GWL_EXSTYLE) & 8) != 0) == topmost) return;
        Topmost = topmost;
        var insertAfter = topmost ? new IntPtr(-1) : new IntPtr(-2);
        if (!topmost && ownedFocus != hwnd && foreground != IntPtr.Zero && User32.IsWindow(foreground)
            && (User32.GetWindowLong(foreground, User32.GWL_EXSTYLE) & 8) == 0)
            insertAfter = foreground;
        User32.SetWindowLayer(hwnd, insertAfter);
    }

    public void UpdatePosition(RECT gameRect, bool isFullscreen)
    {
        _lastGameRect = gameRect;
        if (IsAssistantHidden || WindowState != WindowState.Normal || _isUserMovingOrSizing) return;
        _isProgrammaticChange = true;
        try
        {
            if (isFullscreen != _wasFullscreen)
            {
                if (!_positionSettings.FreeMovement && _positionSettings.Placement is null)
                    _userHeightDip = null;
                _wasFullscreen    = isFullscreen;
                UpdateFullscreenBanner(isFullscreen);
            }

            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            var current = User32.ReadWindowRect(hwnd);
            if (_positionSettings.FreeMovement && _positionSettings.Placement is not null)
            {
                var work = User32.GetMonitorGeometry(current.Left + current.Width / 2, current.Top + current.Height / 2).WorkArea;
                User32.SetWindowBounds(hwnd, WindowGeometry.ClampBounds(current, work));
                return;
            }
            // Free movement without a saved place yet (first start): dock beside the game once, then remember it.
            var firstFreePlacement = _positionSettings.FreeMovement;
            var offset = _positionSettings.FollowOffsetDip;
            var scale = User32.GetWindowDpiScale(hwnd);
            var monitor = offset is null
                ? User32.GetMonitorGeometry(gameRect.Right - 1, gameRect.Top + gameRect.Height / 2)
                : User32.GetMonitorGeometry(
                    (int)Math.Clamp(gameRect.Right + offset.X * scale + current.Width / 2, int.MinValue, int.MaxValue),
                    (int)Math.Clamp(gameRect.Top + offset.Y * scale + current.Height / 2, int.MinValue, int.MaxValue));

            // 先将原生窗口移到目标屏，WPF 处理 DPI 变化后再读取该窗口的真实缩放。
            // 不对虚拟桌面的绝对坐标除以缩放率，避免不同 DPI 和负坐标屏幕错位。
            var centerX = current.Left + current.Width / 2;
            var centerY = current.Top + current.Height / 2;
            if (centerX < monitor.Bounds.Left || centerX >= monitor.Bounds.Right
                || centerY < monitor.Bounds.Top || centerY >= monitor.Bounds.Bottom)
            {
                var left = monitor.Bounds.Left + (monitor.Bounds.Width - current.Width) / 2;
                var top = monitor.Bounds.Top + (monitor.Bounds.Height - current.Height) / 2;
                User32.SetWindowBounds(hwnd, new RECT
                {
                    Left = left, Top = top, Right = left + current.Width, Bottom = top + current.Height,
                });
            }

            scale = User32.GetWindowDpiScale(hwnd);
            var target = offset is null
                ? WindowGeometry.SidePanelBounds(gameRect, monitor.WorkArea, scale, _preferredWidthDip * _appearance.UiScale, _userHeightDip, isFullscreen)
                : WindowGeometry.FollowBounds(gameRect, monitor.WorkArea, scale, _preferredWidthDip * _appearance.UiScale, _userHeightDip, isFullscreen, offset);
            User32.SetWindowBounds(hwnd, target);
            if (firstFreePlacement)
            {
                _positionSettings = _positionSettings with { Placement = CaptureSidePanelPlacement() };
                _ = SavePositionQuietlyAsync();
            }
        }
        finally
        {
            _isProgrammaticChange = false;
        }
    }

    private async Task SavePositionQuietlyAsync()
    {
        try { await _settingsManager.SaveSidePanelPositionAsync(_positionSettings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn("Settings", $"Could not save side panel position: {ex.Message}");
        }
    }

    private void UpdateFullscreenBanner(bool isFullscreen)
    {
        if (isFullscreen && !_fullscreenBannerDismissed)
            FullscreenBanner.Visibility = Visibility.Visible;
        else
            FullscreenBanner.Visibility = Visibility.Collapsed;
    }

    private void OnDismissFullscreenBanner(object sender, RoutedEventArgs e)
    {
        _fullscreenBannerDismissed = true;
        FullscreenBanner.Visibility = Visibility.Collapsed;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isUserMovingOrSizing || _isProgrammaticChange || _isRestoring) return;
        if (e.HeightChanged) _userHeightDip = e.NewSize.Height;
        if (e.WidthChanged) _preferredWidthDip = e.NewSize.Width / _appearance.UiScale;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
            _stateBeforeGameMinimized = null;
        if (_previousWindowState != WindowState.Normal && WindowState == WindowState.Normal)
        {
            _isRestoring = true;
            LayoutUpdated += ClearRestoringFlag;
        }
        _previousWindowState = WindowState;
    }

    private void ClearRestoringFlag(object? sender, EventArgs e)
    {
        _isRestoring = false;
        LayoutUpdated -= ClearRestoringFlag;
    }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnSettingsCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectedIndex is applied while the later page elements are still being initialized.
        if (!IsInitialized) return;
        int selected = SettingsCategoryList.SelectedIndex;
        GameSettingsPage.Visibility = selected == 0 ? Visibility.Visible : Visibility.Collapsed;
        InterfaceSettingsPage.Visibility = selected == 1 ? Visibility.Visible : Visibility.Collapsed;
        KeySettingsPage.Visibility = selected == 2 ? Visibility.Visible : Visibility.Collapsed;
        AboutSettingsPage.Visibility = selected == 3 ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroll.ScrollToTop();
    }

    private void OnToggleSettings(object sender, RoutedEventArgs e)
    {
        var open = SettingsPanel.Visibility != Visibility.Visible || SettingsPanel.Tag as string == "closing";
        HighlightSettingsButton(open);
        if (open)
        {
            SettingsPanel.Tag = null;
            SettingsPanel.Visibility = Visibility.Visible;
            PageTransition.SlideIn(SettingsPanel, fromX: 18);
        }
        else
        {
            SettingsPanel.Tag = "closing";
            PageTransition.SlideOut(SettingsPanel, toX: 18, () =>
            {
                if (SettingsPanel.Tag as string != "closing") return;
                SettingsPanel.Visibility = Visibility.Collapsed;
                SettingsPanel.Tag = null;
            });
        }
    }

    /// <summary>Switching tabs fades the new page in with a short upward slide.</summary>
    private void OnMainTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabs) || !IsLoaded) return;
        Dispatcher.BeginInvoke(ApplyPageScale, DispatcherPriority.Loaded);
        if (MainTabs.SelectedItem is TabItem { Content: FrameworkElement page }) PageTransition.SlideIn(page, fromY: 8);
    }

    /// <summary>The gear stays highlighted while the settings page is open.</summary>
    private void HighlightSettingsButton(bool open)
    {
        if (open)
        {
            SettingsButton.SetResourceReference(BackgroundProperty, "TitleChipBrush");
            SettingsButton.SetResourceReference(ForegroundProperty, "TitleDecorBrush");
        }
        else
        {
            SettingsButton.ClearValue(BackgroundProperty);
            SettingsButton.ClearValue(ForegroundProperty);
        }
    }

    private void UpdateStatusChip()
    {
        var level = TrackingVm.TerminalLevel;
        // "4?" = level saved by the last session, not yet seen on the shop's upgrade card.
        StatusTerminalText.Text = level <= 0 ? "–"
            : TrackingVm.IsTerminalLevelRestored && TrackingVm.ManualTerminalLevel is null ? $"{level}?" : level.ToString();
        StatusTerminalText.ToolTip = TrackingVm.TerminalLevelText;
    }

    // ── Appearance: 3 mode themes × day / night ────────────────────────────

    private SidebarAppearance _appearance;
    private bool _syncingAppearance;
    private bool _uiScaleChanged;
    private bool _syncingScaleControls;
    private bool _scaleInputDirty;
    private UiScaleTarget? _scaleEditorTarget;

    internal SidebarAppearance Appearance => _appearance;
    internal UiScaleTarget? ActiveScaleTarget => SettingsPanel.Visibility == Visibility.Visible ? null : (UiScaleTarget)MainTabs.SelectedIndex;

    private double EditedScale => _scaleEditorTarget is { } target ? _appearance.ScaleFor(target, _appearance.UiScale) : _appearance.UiScale;

    private void SyncUiScaleControls()
    {
        _syncingScaleControls = true;
        UiScaleSlider.Value = EditedScale * 100;
        UiScaleValueBox.Text = $"{EditedScale * 100:0}";
        UiScaleFollowToggle.Visibility = _scaleEditorTarget is null ? Visibility.Collapsed : Visibility.Visible;
        UiScaleFollowToggle.Content = _scaleEditorTarget == UiScaleTarget.OperatorPicker ? "跟随来源页面" : "跟随全局";
        UiScaleFollowToggle.IsChecked = _scaleEditorTarget is { } target && !_appearance.ScaleOverrides.ContainsKey(target);
        UiScaleValueBox.ToolTip = _scaleEditorTarget == UiScaleTarget.OperatorPicker && UiScaleFollowToggle.IsChecked == true
            ? "跟随来源时，实际比例在打开窗口时确定；输入数值可单独设置" : "支持75–150之间的整数百分比";
        ResetUiScaleButton.Content = _scaleEditorTarget is null ? "恢复100%" : "恢复跟随";
        UiScaleInputError.Visibility = Visibility.Collapsed;
        _scaleInputDirty = false;
        _syncingScaleControls = false;
    }

    private void ApplyPageScale()
    {
        if (!IsLoaded) return;
        var tab = (TabItem)MainTabs.SelectedItem;
        var ratio = _appearance.ScaleFor((UiScaleTarget)MainTabs.SelectedIndex, _appearance.UiScale) / _appearance.UiScale;
        if (tab.Content is ScaledViewport viewport)
        {
            if (ratio == 1) tab.Content = viewport.Detach();
            else viewport.ContentScale = ratio;
        }
        else if (ratio != 1)
        {
            var child = (FrameworkElement)tab.Content;
            tab.Content = null;
            tab.Content = new ScaledViewport(child, 278, 0) { ContentScale = ratio };
        }
    }

    private void ApplyUiScale()
    {
        var availableWidth = double.PositiveInfinity;
        RECT work = default;
        IntPtr hwnd = IntPtr.Zero;
        if (IsLoaded)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            var bounds = User32.ReadWindowRect(hwnd);
            work = User32.GetMonitorGeometry(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2).WorkArea;
            availableWidth = work.Width / User32.GetWindowDpiScale(hwnd);
        }
        var programmatic = _isProgrammaticChange;
        _isProgrammaticChange = true;
        try
        {
            MinWidth = 0;
            MaxWidth = Math.Min(400 * _appearance.UiScale, availableWidth);
            MinWidth = Math.Min(280 * _appearance.UiScale, availableWidth);
            Width = Math.Min(_preferredWidthDip * _appearance.UiScale, availableWidth);
            SidebarScaleTransform.ScaleX = SidebarScaleTransform.ScaleY = _appearance.UiScale;
            ApplyPageScale();
            WindowUiScale.Refresh(_appearance);
            if (IsLoaded && !_isUserMovingOrSizing)
            {
                if (!_positionSettings.FreeMovement && _lastGameRect is { } game)
                    UpdatePosition(game, _wasFullscreen);
                else
                    User32.SetWindowBounds(hwnd, WindowGeometry.ClampBounds(User32.ReadWindowRect(hwnd), work));
            }
        }
        finally { _isProgrammaticChange = programmatic; }
    }

    private void OnUiScaleValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingScaleControls) return;
        SetEditedScale(e.NewValue / 100);
        SyncUiScaleControls();
    }

    private void SetEditedScale(double scale)
    {
        if (_scaleEditorTarget is { } target) _appearance.ScaleOverrides[target] = scale;
        else _appearance.UiScale = scale;
        _uiScaleChanged = true;
        if (_scaleEditorTarget is null) ApplyUiScale();
        else { ApplyPageScale(); WindowUiScale.Refresh(_appearance); }
    }

    private async void OnUiScaleTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _syncingScaleControls) return;
        if (!TryApplyScaleInput())
        {
            _syncingScaleControls = true;
            UiScaleTargetSelector.SelectedIndex = _scaleEditorTarget is { } previous ? (int)previous + 1 : 0;
            _syncingScaleControls = false;
            return;
        }
        var saving = CommitUiScaleAsync();
        _scaleEditorTarget = UiScaleTargetSelector.SelectedIndex == 0 ? null : (UiScaleTarget)(UiScaleTargetSelector.SelectedIndex - 1);
        SyncUiScaleControls();
        await saving;
    }

    private void OnUiScaleInputChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncingScaleControls) _scaleInputDirty = true;
    }

    private bool TryApplyScaleInput()
    {
        if (!_scaleInputDirty) return true;
        var text = UiScaleValueBox.Text.Trim();
        if (text.EndsWith('%') || text.EndsWith('％')) text = text[..^1].TrimEnd();
        if (!int.TryParse(text, out var percent) || percent is < 75 or > 150)
        {
            UiScaleInputError.Visibility = Visibility.Visible;
            return false;
        }
        SetEditedScale(percent / 100.0);
        SyncUiScaleControls();
        return true;
    }

    private async void OnUiScaleInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { SyncUiScaleControls(); e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (TryApplyScaleInput()) await CommitUiScaleAsync();
    }

    private async void OnUiScaleInputLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (TryApplyScaleInput()) await CommitUiScaleAsync();
    }

    private async void OnUiScaleFollowChanged(object sender, RoutedEventArgs e)
    {
        if (_scaleEditorTarget is not { } target) return;
        if (UiScaleFollowToggle.IsChecked == true) _appearance.ScaleOverrides.Remove(target);
        else _appearance.ScaleOverrides[target] = EditedScale;
        _uiScaleChanged = true;
        SyncUiScaleControls();
        ApplyPageScale();
        WindowUiScale.Refresh(_appearance);
        await CommitUiScaleAsync();
    }

    // Preview while dragging; persist only the completed gesture, including keyboard and focus changes.
    private async Task CommitUiScaleAsync()
    {
        if (!_uiScaleChanged) return;
        _uiScaleChanged = false;
        await SaveAppearanceAsync(UiScaleSlider);
    }

    private async void OnUiScaleDragCompleted(object sender, DragCompletedEventArgs e) => await CommitUiScaleAsync();
    private async void OnUiScaleMouseUp(object sender, MouseButtonEventArgs e) => await CommitUiScaleAsync();
    private async void OnUiScaleKeyUp(object sender, KeyEventArgs e) => await CommitUiScaleAsync();
    private async void OnUiScaleLostFocus(object sender, KeyboardFocusChangedEventArgs e) => await CommitUiScaleAsync();

    private async void OnResetUiScale(object sender, RoutedEventArgs e)
    {
        if (_scaleEditorTarget is { } target)
        {
            _appearance.ScaleOverrides.Remove(target);
            _uiScaleChanged = true;
            ApplyPageScale();
            WindowUiScale.Refresh(_appearance);
        }
        else SetEditedScale(1);
        SyncUiScaleControls();
        await CommitUiScaleAsync();
    }

    private GameShopTheme CurrentUiTheme => _appearance.ThemeFor(GameTheme);

    private void SyncDayNightSelector()
    {
        _syncingAppearance = true;
        DayNightSelector.SelectedIndex = _appearance.IsNight(CurrentUiTheme) ? 1 : 0;
        _syncingAppearance = false;
    }

    private void ApplyAppearance()
    {
        ThemeManager.Apply(CurrentUiTheme, _appearance.IsNight(CurrentUiTheme));
        SyncDayNightSelector();
    }

    private Task SaveAppearanceAsync(UIElement control)
        => SavePreferenceAsync(() => _settingsManager.SaveSidebarAppearanceAsync(_appearance), control);

    private async void OnDayNightChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingAppearance || DayNightSelector.SelectedIndex < 0) return;
        _appearance.Night[CurrentUiTheme] = DayNightSelector.SelectedIndex == 1;
        ApplyAppearance();
        await SaveAppearanceAsync(DayNightSelector);
    }

    private async void OnFollowModeThemeChanged(object sender, RoutedEventArgs e)
    {
        _appearance.FollowModeTheme = FollowModeThemeToggle.IsChecked == true;
        UiThemeSelector.Visibility = _appearance.FollowModeTheme ? Visibility.Collapsed : Visibility.Visible;
        ApplyAppearance();
        await SaveAppearanceAsync(FollowModeThemeToggle);
    }

    private SidePanelPlacement CaptureSidePanelPlacement()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var rect = User32.ReadWindowRect(hwnd);
        return new SidePanelPlacement(rect.Left, rect.Top, _preferredWidthDip, rect.Height / User32.GetWindowDpiScale(hwnd));
    }

    private async void OnFreeSidePanelMovementChanged(object sender, RoutedEventArgs e)
    {
        var enabled = FreeSidePanelMovementToggle.IsChecked == true;
        if (enabled == _positionSettings.FreeMovement) return; // 重置按钮同步复选框时不重新记录偏移。
        var placement = CaptureSidePanelPlacement();
        _userHeightDip = placement.HeightDip;
        _positionSettings = _positionSettings with
        {
            FreeMovement = enabled,
            Placement = placement,
            FollowOffsetDip = !enabled && _lastGameRect is { } game
                ? WindowGeometry.CaptureFollowOffset(game, placement, User32.GetWindowDpiScale(new WindowInteropHelper(this).Handle))
                : null,
        };
        await SavePreferenceAsync(() => _settingsManager.SaveSidePanelPositionAsync(_positionSettings), FreeSidePanelMovementToggle);
    }

    private async void OnForceTopmostChanged(object sender, RoutedEventArgs e)
    {
        _positionSettings = _positionSettings with { ForceTopmost = ForceTopmostToggle.IsChecked == true };
        TopmostPreferenceChanged?.Invoke();
        await SavePreferenceAsync(() => _settingsManager.SaveSidePanelPositionAsync(_positionSettings), ForceTopmostToggle);
    }

    private async void OnResetSidePanelPosition(object sender, RoutedEventArgs e)
    {
        if (_lastGameRect is not { } game)
        {
            ShowToast("未找到明日方舟窗口，暂时无法重置侧边栏位置");
            return;
        }
        _userHeightDip = CaptureSidePanelPlacement().HeightDip;
        _positionSettings = _positionSettings with { FreeMovement = false, FollowOffsetDip = null };
        FreeSidePanelMovementToggle.IsChecked = false;
        UpdatePosition(game, _wasFullscreen);
        _positionSettings = _positionSettings with { Placement = CaptureSidePanelPlacement() };
        await SavePreferenceAsync(() => _settingsManager.SaveSidePanelPositionAsync(_positionSettings), FreeSidePanelMovementToggle);
    }

    private async void OnUiThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UiThemeSelector.SelectedIndex < 0) return;
        _appearance.Theme = (GameShopTheme)UiThemeSelector.SelectedIndex;
        if (_appearance.FollowModeTheme) return;
        ApplyAppearance();
        await SaveAppearanceAsync(UiThemeSelector);
    }

    private async void OnGameThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GameThemeSelector.SelectedIndex < 0) return;
        GameTheme = (GameShopTheme)GameThemeSelector.SelectedIndex;
        GameThemeChanged?.Invoke(GameTheme);
        if (_appearance.FollowModeTheme) ApplyAppearance();
        await SavePreferenceAsync(() => _settingsManager.SaveGameShopThemeAsync(GameTheme), GameThemePanel);
    }

    public void InitAutomaticGameTheme(bool automatic)
    {
        AutomaticThemeToggle.IsChecked = automatic;
        AutomaticThemeToggle.Checked += OnAutomaticGameThemeChanged;
        AutomaticThemeToggle.Unchecked += OnAutomaticGameThemeChanged;
    }

    private async void OnAutomaticGameThemeChanged(object sender, RoutedEventArgs e)
    {
        AutomaticGameThemeChanged?.Invoke(AutomaticGameTheme);
        await SavePreferenceAsync(() => _settingsManager.SaveAutomaticGameThemeAsync(AutomaticGameTheme), AutomaticThemeToggle);
    }

    public void ApplyDetectedGameTheme(GameShopTheme theme)
    {
        if (!AutomaticGameTheme) return;
        GameThemeSelector.SelectedIndex = (int)theme;
    }

    /// <summary>Raised when the user turns the in-game board overlay on or off.</summary>
    public event Action<bool>? BoardOverlayToggled;

    public void InitBoardOverlay(bool enabled)
    {
        BoardOverlayToggle.IsChecked = enabled;
        BoardOverlayToggle.Checked += OnBoardOverlayChanged;
        BoardOverlayToggle.Unchecked += OnBoardOverlayChanged;
    }

    /// <summary>Shows what the board recognizer currently sees under the overlay toggle.</summary>
    public void SetBoardStatus(string? mapName, bool shopOpen) =>
        BoardStatusText.Text = mapName is null
            ? "站位页开启“游戏内显示”时，同时标出地面 / 特殊地面 / 高台和敌人路线"
            : $"当前地图：{mapName} · 商店{(shopOpen ? "展开" : "收起")}";

    private async void OnBoardOverlayChanged(object sender, RoutedEventArgs e)
    {
        bool enabled = BoardOverlayToggle.IsChecked == true;
        BoardOverlayToggled?.Invoke(enabled);
        await SavePreferenceAsync(() => _settingsManager.SaveShowBoardOverlayAsync(enabled), BoardOverlayToggle);
    }

    public event Action<bool>? AutomaticPurchaseTrackingChanged;
    public event Action<PurchaseRecognitionSpeed>? PurchaseRecognitionSpeedChanged;

    public void InitPurchaseRecognitionSpeed(PurchaseRecognitionSpeed speed)
    {
        PurchaseSpeedSelector.SelectedIndex = speed switch { PurchaseRecognitionSpeed.Fast => 1, PurchaseRecognitionSpeed.Fastest => 2, _ => 0 };
        PurchaseSpeedSelector.SelectionChanged += OnPurchaseSpeedChanged;
    }

    private async void OnPurchaseSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        var speed = PurchaseSpeedSelector.SelectedIndex switch { 1 => PurchaseRecognitionSpeed.Fast, 2 => PurchaseRecognitionSpeed.Fastest, _ => PurchaseRecognitionSpeed.Standard };
        PurchaseRecognitionSpeedChanged?.Invoke(speed);
        await SavePreferenceAsync(() => _settingsManager.SavePurchaseRecognitionSpeedAsync(speed), PurchaseSpeedControls);
    }

    public void InitAutomaticPurchaseTracking(bool enabled)
    {
        AutomaticPurchaseTrackingToggle.IsChecked = enabled;
        AutomaticPurchaseTrackingToggle.Checked += OnAutomaticPurchaseTrackingChanged;
        AutomaticPurchaseTrackingToggle.Unchecked += OnAutomaticPurchaseTrackingChanged;
    }

    private async void OnAutomaticPurchaseTrackingChanged(object sender, RoutedEventArgs e)
    {
        var enabled = AutomaticPurchaseTrackingToggle.IsChecked == true;
        AutomaticPurchaseTrackingChanged?.Invoke(enabled);
        await SavePreferenceAsync(() => _settingsManager.SaveAutomaticPurchaseTrackingAsync(enabled), AutomaticPurchaseTrackingToggle);
    }

    private async void OnMultipleLineupsChanged(object sender, RoutedEventArgs e)
    {
        TrackingVm.TrackMultipleLineups = MultiLineupTrackingToggle.IsChecked == true;
        await SavePreferenceAsync(() => _settingsManager.SaveTrackMultipleLineupsAsync(TrackingVm.TrackMultipleLineups), MultiLineupTrackingToggle);
    }

    private async Task SavePreferenceAsync(Func<Task> save, UIElement control)
    {
        control.IsEnabled = false;
        try
        {
            await save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn("Settings", $"Could not save preference: {ex.Message}");
            ShowToast("设置已生效，但保存失败");
        }
        finally
        {
            control.IsEnabled = true;
        }
    }

    private async void OnClose(object sender, RoutedEventArgs e) => await RequestUserCloseAsync();

    private void UpdateCloseButtonTooltip()
    {
        CloseAssistantButton.ToolTip = _closeBehavior switch
        {
            MainWindowCloseBehavior.MinimizeToTray => "收起到托盘（后台识别继续）",
            MainWindowCloseBehavior.Exit => "退出程序（停止后台识别）",
            _ => "关闭助手（每次询问）"
        };
    }

    private async Task SetCloseBehaviorAsync(MainWindowCloseBehavior behavior)
    {
        _closeBehavior = behavior;
        CloseBehaviorSelector.SelectedIndex = (int)behavior;
        UpdateCloseButtonTooltip();
        await SavePreferenceAsync(() => _settingsManager.SaveMainWindowCloseBehaviorAsync(behavior), CloseBehaviorSelector);
    }

    private async void OnCloseBehaviorChanged(object sender, SelectionChangedEventArgs e)
    {
        var behavior = (MainWindowCloseBehavior)CloseBehaviorSelector.SelectedIndex;
        if (behavior == _closeBehavior) return;
        await SetCloseBehaviorAsync(behavior);
    }

    internal async Task RequestUserCloseAsync()
    {
        if (_closeRequestPending) return;
        _closeRequestPending = true;
        try
        {
            var behavior = _closeBehavior;
            if (behavior == MainWindowCloseBehavior.Ask)
            {
                var dialog = new CloseBehaviorDialog { Owner = this };
                if (dialog.ShowDialog() != true) return;
                behavior = dialog.Choice!.Value;
                if (dialog.Remember) await SetCloseBehaviorAsync(behavior);
            }
            if (behavior == MainWindowCloseBehavior.Exit) Application.Current.Shutdown();
            else if (!IsAssistantHidden) ToggleAssistantVisibilityRequested?.Invoke();
        }
        finally { _closeRequestPending = false; }
    }

    // ── Lineup feature wiring ──────────────────────────────────────────────

    private void WireLineupView()
    {
        LineupView.MoveLineupRequested += async (lineup, direction) =>
        {
            LineupView.IsEnabled = false;
            try { await LineupVm.MoveAsync(lineup, direction); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowToast($"阵容排序保存失败：{ex.Message}");
            }
            finally { LineupView.IsEnabled = true; }
        };
        LineupView.NewLineupRequested += async () => await OpenEditorAsync(null, null);
        LineupView.EditLineupRequested += async lineup => await OpenEditorAsync(lineup, null);
        LineupView.OpenAutoRouteRequested += lineup =>
        {
            if (PlannerVm.OpenSavedAutoRoute(lineup) is { } error) { ShowToast(error); return; }
            ShowLineupsSegment(planner: true);
        };
        LineupView.DeleteLineupRequested += async lineup =>
        {
            if (!ConfirmDialog.Ask(this, "删除阵容", $"确定删除阵容“{lineup.Name}”？此操作不可撤销。", "删除", danger: true))
                return;
            try
            {
                await LineupVm.DeleteAsync(lineup);
                LineupView.RefreshAllBadges();
                ShowToast($"已删除阵容“{lineup.Name}”");
            }
            catch (Exception ex)
            {
                ShowToast($"删除失败：{ex.Message}");
            }
        };

        LineupView.ShareLineupRequested += async lineup =>
        {
            string url;
            try
            {
                url = await PrepareLineupShareAsync(lineup);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException
                or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
            {
                AppLogger.Warn("Lineup", $"Share failed: {ex.Message}");
                ShowToast($"生成分享链接失败：{ex.Message}");
                return;
            }
            HideToast();
            var shareDialog = new ShareLinkWindow(lineup.Name, url, Clipboard.SetText) { Owner = this };
            shareDialog.SetValue(WindowUiScale.PageTargetProperty, UiScaleTarget.Lineups);
            shareDialog.ShowDialog();
        };

        LineupView.ImportLineupRequested += async () =>
        {
            string clipboard = "";
            try { clipboard = Clipboard.GetText() ?? ""; } catch { /* ignore clipboard race */ }

            var dlg = new InputDialog(
                "导入阵容",
                "粘贴森空岛攻略链接、dpaste.com 短链接或阵容 JSON：",
                clipboard.Trim())
            {
                Owner = this,
                ShowGuideLink = true,
            };
            if (dlg.ShowDialog() != true) return;
            var input = dlg.InputText.Trim();
            if (string.IsNullOrEmpty(input)) return;

            LineupView.IsEnabled = false;
            try
            {
                ShowToast("正在导入…");
                if (SklandGuideImporter.IsOfficialGuideUrl(input))
                {
                    var imported = await _lineupShareService.ImportOfficialGuideAsync(input, _allOperators, _allStrategies);
                    HideToast();
                    await OpenEditorAsync(imported.Lineup, imported.Notice);
                    return;
                }
                var lineup = await _lineupShareService.ImportAsync(input);
                // Avoid id collision with existing lineups by re-id-ing on import.
                lineup.Id = Guid.NewGuid().ToString("N");
                lineup.UpdatedAt = DateTime.UtcNow;
                if (string.IsNullOrWhiteSpace(lineup.Name))
                    lineup.Name = "导入的阵容";
                await LineupVm.AddOrUpdateAsync(lineup);
                LineupView.RefreshAllBadges();
                ShowToast($"已导入阵容“{lineup.Name}”");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Lineup", $"Import failed: {ex.Message}");
                ShowToast($"导入失败：{ex.Message}");
            }
            finally { LineupView.IsEnabled = true; }
        };

        LineupView.TrackLineupRequested += lineup =>
        {
            try
            {
                var added = TrackingVm.TrackLineup(lineup);
                ShowToast($"已追踪阵容“{lineup.Name}”：新增 {added.Operators} 干员 / {added.Equipment} 装备");
            }
            catch (Exception ex)
            {
                ShowToast($"追踪失败：{ex.Message}");
            }
        };
        LineupView.RepairLineupRequested += rec =>
        {
            ShowLineupsSegment(planner: true);
            PlannerVm.BeginRepair(rec.Lineup, LineupVm.TargetsOf(rec.Lineup), rec.Banned);
        };
        WirePlannerView();
        UpdateLineupRunInfo();
    }

    /// <summary>本局推荐 follows the opening bans: none recorded → no recommendations.</summary>
    private void UpdateLineupRunInfo()
    {
        var bannedCovenants = BanVm.BannedCovenants;
        if (BanVm.BannedNames.Count == 0 && bannedCovenants.Count == 0 && !OperatorBrowseVm.HasRunInfo)
        {
            LineupVm.SetRunInfo(null, null);
            return;
        }
        var byName = _allOperators.GroupBy(o => o.Name).ToDictionary(g => g.Key, g => g.First());
        bool Unavailable(string name) => BanVm.IsBanned(name)
            || (byName.TryGetValue(name, out var op) && CovenantBanRules.IsExcluded(op, bannedCovenants));
        LineupVm.SetRunInfo(Unavailable, new HashSet<string>(bannedCovenants),
            [.. BanVm.BannedNames.Concat(byName.Keys.Where(Unavailable)).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal)]);
    }

    /// <summary>Opens the 阵容 tab on 我的阵容 (with 本局推荐 in view) or on 规划.</summary>
    public void ShowLineupsSegment(bool planner)
    {
        MainTabs.SelectedItem = LineupsTab;
        LineupsSegment.SelectedIndex = planner ? 1 : 0;
        if (!planner)
        {
            UpdateLineupRunInfo();
            LineupView.ShowRecommendations();
        }
    }

    private void OnLineupsSegmentChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (LineupView is null || PlannerView is null) return;
        bool planner = LineupsSegment.SelectedIndex == 1;
        LineupView.Visibility = planner ? Visibility.Collapsed : Visibility.Visible;
        PlannerView.Visibility = planner ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded) PageTransition.SlideIn(planner ? PlannerView : LineupView, fromY: 8);
    }

    private void OnCodexSegmentChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (OperatorView is null || EquipmentView is null) return;
        bool equipment = CodexSegment.SelectedIndex == 1;
        OperatorView.Visibility = equipment ? Visibility.Collapsed : Visibility.Visible;
        EquipmentView.Visibility = equipment ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded) PageTransition.SlideIn(equipment ? EquipmentView : OperatorView, fromY: 8);
    }

    private void WirePlannerView()
    {
        PlannerVm = new LineupPlannerViewModel(_lineupPlanner, _allOperators, BanVm, LineupVm.Calculator.EquipmentRules)
        {
            CurrentTerminalLevel = TrackingVm.TerminalLevel,
            TrackedOperatorsProvider = () => TrackingVm.TrackedOperators.Where(e => e.IsStageActive).Select(e => e.Name).ToList(),
            RunOnUiThread = action =>
            {
                if (Dispatcher.CheckAccess()) action();
                else Dispatcher.BeginInvoke(action);
            }
        };
        TrackingVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TrackingViewModel.TerminalLevel))
                PlannerVm.CurrentTerminalLevel = TrackingVm.TerminalLevel;
            if (e.PropertyName is nameof(TrackingViewModel.TerminalLevel) or nameof(TrackingViewModel.IsTerminalLevelRestored)
                or nameof(TrackingViewModel.ManualTerminalLevel) or nameof(TrackingViewModel.Economy) or nameof(TrackingViewModel.OperatorProgress))
                PlannerVm.SetAutoRunState(new(TrackingVm.IsTerminalLevelRestored && TrackingVm.ManualTerminalLevel is null
                    ? 0 : TrackingVm.TerminalLevel, TrackingVm.Economy, TrackingVm.OwnedOperatorNames));
        };
        PlannerVm.SetAutoRunState(new(TrackingVm.IsTerminalLevelRestored && TrackingVm.ManualTerminalLevel is null
            ? 0 : TrackingVm.TerminalLevel, TrackingVm.Economy, TrackingVm.OwnedOperatorNames));
        PlannerVm.SaveRequested += async lineup =>
        {
            try
            {
                await LineupVm.AddOrUpdateAsync(lineup);
                LineupView.RefreshAllBadges();
                ShowToast($"已保存阵容“{lineup.Name}”");
            }
            catch (Exception ex)
            {
                ShowToast($"保存失败：{ex.Message}");
            }
        };
        PlannerVm.OverwriteRequested += async lineup => await SaveEditedLineupAsync(lineup, edited: true);
        PlannerVm.TrackRequested += lineup =>
        {
            try
            {
                var added = TrackingVm.TrackLineup(lineup);
                ShowToast($"已追踪规划阵容：新增 {added.Operators} 名干员");
            }
            catch (Exception ex)
            {
                ShowToast($"追踪失败：{ex.Message}");
            }
        };
        PlannerVm.TrackRouteRequested += route =>
        {
            try
            {
                var added = TrackingVm.TrackRoute(route, LineupVm.Calculator.EquipmentRules);
                ShowToast($"已追踪道中路线：新增 {added.Final} 名成型阵容干员 / {added.Transition} 名过渡干员");
            }
            catch (Exception ex)
            {
                ShowToast($"追踪失败：{ex.Message}");
            }
        };
        PlannerVm.SetAvailableCovenants(OperatorBrowseVm.HighlightedCovenants);
        OperatorBrowseVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OperatorBrowseViewModel.HighlightedCovenants))
                Dispatcher.Invoke(() =>
                {
                    PlannerVm.SetAvailableCovenants(OperatorBrowseVm.HighlightedCovenants);
                    UpdateLineupRunInfo();
                });
        };
        PlannerVm.ShowAutoPageCommand.Execute(null);
        PlannerView.DataContext = PlannerVm;
    }

    internal async Task<string> PrepareLineupShareAsync(Lineup lineup)
    {
        if (lineup.SourceUrl is { } source && SklandGuideImporter.IsOfficialGuideUrl(source) && lineup.SourceContentHash is null)
        {
            ShowToast("正在核对森空岛原攻略…");
            try
            {
                var original = await _lineupShareService.ImportOfficialGuideAsync(source, _allOperators, _allStrategies);
                var reference = new LineupEditorViewModel(original.Lineup, _allOperators, _allEquipment, LineupVm.Calculator);
                lineup.SourceContentHash = LineupShareService.ContentFingerprint(reference.ToLineup());
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException or ArgumentException)
            {
                // An unavailable source cannot prove the local lineup unchanged; share the local content instead.
                AppLogger.Warn("Lineup", $"Original guide unavailable; sharing the local lineup: {ex.Message}");
            }
        }
        ShowToast("正在准备分享链接…");
        return await _lineupShareService.ExportAsync(lineup);
    }

    internal async Task OpenEditorAsync(Lineup? source, string? importNotice)
    {
        var calc = LineupVm.Calculator;
        var editorVm = new LineupEditorViewModel(source, _allOperators, _allEquipment, calc);
        if (importNotice is not null && source?.SourceUrl is { } url && SklandGuideImporter.IsOfficialGuideUrl(url))
            editorVm.SourceContentHash = LineupShareService.ContentFingerprint(editorVm.ToLineup());
        var window = new LineupEditorWindow(editorVm, _allStrategies) { Owner = this };
        if (importNotice is not null) window.ShowImportNotice(importNotice);
        await MinimizableDialog.ShowAsync(window, PanelContent);
        if (window.Result is null) return;

        await SaveEditedLineupAsync(window.Result, source is not null);
    }

    internal async Task SaveEditedLineupAsync(Lineup lineup, bool edited)
    {
        try { await LineupVm.AddOrUpdateAsync(lineup); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast($"阵容保存失败：{ex.Message}");
            return;
        }
        LineupView.RefreshAllBadges();
        ShowToast(edited ? "已保存阵容" : "已创建阵容");
        PlannerVm.NotifySavedAutoRouteSourceChanged(lineup);
        if (!edited || !TrackingVm.IsLineupTracked(lineup.Id)) return;
        var dialog = new ConfirmDialog("更新追踪阵容", $"阵容“{lineup.Name}”已保存。是否将修改应用到追踪栏？其他阵容、手动追踪和获取状态会保留。",
            "应用到追踪栏", cancelText: "仅保存") { Owner = this };
        if (dialog.ShowDialog() != true) return;
        TrackingVm.ApplyTrackedLineup(lineup);
        ShowToast("已将阵容修改应用到追踪栏");
    }

    // ── Transient toast ────────────────────────────────────────────────────

    public void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastBorder.Visibility = Visibility.Visible;

        var fadeIn = new DoubleAnimation
        {
            From = ToastBorder.Opacity,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(140),
        };
        ToastBorder.BeginAnimation(UIElement.OpacityProperty, fadeIn);

        _toastHideTimer.Stop();
        _toastHideTimer.Start();
    }

    private void HideToast()
    {
        _toastHideTimer.Stop();

        var fadeOut = new DoubleAnimation
        {
            From = ToastBorder.Opacity,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(220),
        };
        fadeOut.Completed += (_, _) =>
        {
            ToastBorder.Visibility = Visibility.Collapsed;
        };
        ToastBorder.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }
}
