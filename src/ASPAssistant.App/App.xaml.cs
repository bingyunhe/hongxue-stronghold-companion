// Modified for ASP Assistant Regulation on 2026-11-13; see NOTICE.
using System.IO;
using System.Windows;
using ASPAssistant.App.Windows;
using ASPAssistant.Core;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.GameModes.GarrisonProtocol;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;

namespace ASPAssistant.App;

public partial class App : Application
{
    /// <summary>
    /// UI text font for every window, tooltip and menu: Noto Sans SC (bundled, subset to GB2312 plus the app's own
    /// text, SIL OFL), falling back to the system fonts for anything outside the subset. Number fonts stay per theme.
    /// </summary>
    static App()
    {
        var ui = new System.Windows.Media.FontFamily(
            "pack://application:,,,/HongxueCompanion;component/Resources/Fonts/#Noto Sans SC, Microsoft YaHei UI, Segoe UI");
        foreach (var type in new[] { typeof(Window), typeof(System.Windows.Controls.ToolTip), typeof(System.Windows.Controls.ContextMenu) })
        {
            try
            {
                System.Windows.Documents.TextElement.FontFamilyProperty.OverrideMetadata(type,
                    new FrameworkPropertyMetadata(ui, FrameworkPropertyMetadataOptions.Inherits));
            }
            catch (ArgumentException) { /* this type already overrides it; keep the system font there */ }
        }
    }

    private WindowTrackerService? _windowTracker;
    private OcrScannerService? _ocrScanner;
    private BanScreenAnalyzer? _banScreenAnalyzer;
    private SidePanelWindow? _sidePanel;
    private AssistantTray? _assistantTray;
    private OverlayWindow? _overlay;
    private SettingsManager? _settingsManager;
    private UpdateStartupNotification? _updateStartup;
    private ScreenCaptureService? _captureService;
    private System.Windows.Threading.DispatcherTimer? _connectionStatusTimer;
    private readonly SemaphoreSlim _informationScanGate = new(1, 1);
    private List<ASPAssistant.Core.Models.ShopItem> _lastShopItems = [];
    private List<TrackingEntry> _shopTrackingEntries = [];

    public App()
    {
        // Catches exceptions before the WPF Dispatcher is running (e.g. DllNotFoundException
        // from MaaFramework native libs if VC++ Runtime is missing, or single-file extraction failures).
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            AppLogger.Error("App", "Fatal unhandled exception",
                ex ?? new Exception(args.ExceptionObject?.ToString()));
            if (_updateStartup?.IsPending == true)
            {
                _updateStartup.ReportFailure(ex?.Message ?? "程序启动失败");
                return;
            }

            var isDllMissing = ex is DllNotFoundException
                || (ex?.InnerException is DllNotFoundException)
                || args.ExceptionObject?.ToString()?.Contains("DllNotFoundException") == true;

            var message = isDllMissing
                ? "缺少必要的运行库，程序无法启动。\n\n" +
                  "请先安装 Microsoft Visual C++ Redistributable（x64），然后重新运行：\n" +
                  "https://aka.ms/vs/17/release/vc_redist.x64.exe\n\n" +
                  $"技术详情：\n{ex?.GetType().Name}: {ex?.Message}"
                : $"程序启动时发生严重错误，请将以下信息反馈给开发者：\n\n{args.ExceptionObject}";

            MessageBox.Show(
                message,
                "鸿雪·卫戍助手崩溃",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        };

        // Prevents background Task exceptions from being silently swallowed.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogger.Error("App", "Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Error("App", "Unhandled UI exception", args.Exception);
            if (_updateStartup?.IsPending == true)
            {
                _updateStartup.ReportFailure(args.Exception.Message);
                args.Handled = true;
                Shutdown(1);
                return;
            }
            MessageBox.Show(
                $"启动时发生错误，请将以下信息反馈给开发者：\n\n{args.Exception}",
                "鸿雪·卫戍助手错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(1);
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        ASPAssistant.App.Windows.SystemBorder.RegisterForAllWindows();
        ASPAssistant.App.Controls.PanelToolTip.RegisterForAllElements();
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var startupResult = Environment.GetEnvironmentVariable(UpdateStartupNotification.ResultVariable);
            Environment.SetEnvironmentVariable(UpdateStartupNotification.ResultVariable, null);
            _updateStartup = new UpdateStartupNotification(startupResult);
            if (await StartupAsync(e))
                _updateStartup.ReportReady();
            else
                _updateStartup.ReportFailure("启动已取消");
        }
        catch (Exception ex)
        {
            AppLogger.Error("App", "Startup failed", ex);
            if (_updateStartup?.IsPending == true)
            {
                _updateStartup.ReportFailure(ex.Message);
                Shutdown(1);
                return;
            }

            // DllNotFoundException from MaaFramework almost always means the Visual C++
            // Redistributable (vcruntime140_1.dll) is not installed on the user's machine.
            var isDllMissing = ex is DllNotFoundException
                || (ex.InnerException is DllNotFoundException)
                || ex.ToString().Contains("DllNotFoundException");

            var message = isDllMissing
                ? "缺少必要的运行库，程序无法启动。\n\n" +
                  "请先安装 Microsoft Visual C++ Redistributable（x64），然后重新运行：\n" +
                  "https://aka.ms/vs/17/release/vc_redist.x64.exe\n\n" +
                  $"技术详情：\n{ex.GetType().Name}: {ex.Message}"
                : $"启动时发生错误，请将以下信息反馈给开发者：\n\n{ex}";

            MessageBox.Show(
                message,
                "鸿雪·卫戍助手错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task<bool> StartupAsync(StartupEventArgs e)
    {
        // Paths
        var appDir = AppContext.BaseDirectory;
        var dataDir = Path.Combine(appDir, "data");
        // Initialize file logger as early as possible.
        AppLogger.Initialize(Path.Combine(appDir, "logs"));
        AppLogger.Info("App", $"appDir={appDir}");

        var settingsDir = UserSettingsDirectory.Prepare(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetEnvironmentVariable("ASPASSISTANT_SETTINGS_DIR"));
        // Program's named mutex excludes another app instance; the short-lived root launcher has the same file name.
        var configurationImport = ConfigurationArchive.ApplyPending(settingsDir);
        // Maa starts external ADB processes internally. Set the inherited directory once before loading it.
        Directory.SetCurrentDirectory(AdbGameScreens.WorkingDirectory);

        // Data layer
        var dataStore = new JsonDataStore(dataDir);
        SearchMatch.LoadFile(Path.Combine(dataDir, "search_keys.json"));
        _settingsManager = new SettingsManager(settingsDir);
        var appearance = await _settingsManager.LoadSidebarAppearanceAsync();
        WindowUiScale.Initialize(appearance);
        ThemeManager.Apply(appearance.Theme, appearance.IsNight(appearance.Theme));
        var savedGameTheme = await _settingsManager.LoadGameShopThemeAsync();
        var automaticGameTheme = await _settingsManager.LoadAutomaticGameThemeAsync();
        if (savedGameTheme is null && automaticGameTheme) savedGameTheme = Core.Models.GameShopTheme.Terminal;
        if (savedGameTheme is null)
        {
            var themeWindow = new ModeThemeWindow();
            if (themeWindow.ShowDialog() != true)
            {
                Shutdown();
                return false;
            }
            savedGameTheme = themeWindow.SelectedTheme;
            try
            {
                await _settingsManager.SaveGameShopThemeAsync(savedGameTheme.Value);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLogger.Warn("Settings", $"Could not save initial theme: {ex.Message}");
                MessageBox.Show("主题已生效，但无法保存设置。下次启动时将再次提示选择。",
                    "模式主题设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        var gameTheme = savedGameTheme.Value;
        var uiTheme = appearance.ThemeFor(gameTheme);
        ThemeManager.Apply(uiTheme, appearance.IsNight(uiTheme));

        // Load data
        AppLogger.Info("App", "Loading operators...");
        var operators = await dataStore.LoadOperatorsAsync();
        var customOperators = await dataStore.LoadCustomOperatorsAsync();
        var selectedCustom = (await _settingsManager.LoadCustomOperatorSelectionAsync()).ToHashSet(StringComparer.Ordinal);
        foreach (var op in customOperators) op.IsSelectedCustom = selectedCustom.Contains(op.Name);
        operators.AddRange(customOperators);
        AppLogger.Info("App", $"Loaded {operators.Count} operators");

        var (equipment, manualJobChangeEquipments) = await dataStore.LoadEquipmentAsync();
        AppLogger.Info("App", $"Loaded {equipment.Count} equipment items");


        var (covenantInfos, jobChangeEquipmentToCovenant) = await dataStore.LoadCovenantsAsync();
        var strategies = await dataStore.LoadStrategiesAsync();
        AppLogger.Info("App",
            $"Loaded {covenantInfos.Count} covenants, "
            + $"{jobChangeEquipmentToCovenant.Count} job-change equipment mappings");

        var covenantCalculator = new CovenantCalculator(operators, covenantInfos, jobChangeEquipmentToCovenant);
        var trackingState = await _settingsManager.LoadTrackingStateAsync(covenantCalculator.EquipmentRules);
        AppLogger.Info("App", $"Loaded {trackingState.Entries.Count} tracking entries");
        var lineupStore = new LineupStore(Path.Combine(settingsDir, "lineups"), covenantCalculator.EquipmentRules);
        var lineups = await lineupStore.LoadAllAsync();
        AppLogger.Info("App", $"Loaded {lineups.Count} saved lineups");

        // Boss and enemy-type data — loaded from local JSON files generated by
        // fetch_spdatabase.py. Both catalogues must be ready before recognition starts.
        var bossDataService      = new BossDataService();
        var enemyTypeDataService = new EnemyTypeDataService();
        await Task.WhenAll(bossDataService.LoadAsync(dataDir), enemyTypeDataService.LoadAsync(dataDir));

        // ViewModels
        var banVm   = new BanViewModel();
        var enemyVm = new EnemyViewModel();

        var operatorVm = new OperatorBrowseViewModel { SelectedOperatorSource = OperatorSourceFilter.Default };
        operatorVm.LoadOperators(operators);
        operatorVm.SetBanChecker(banVm.IsBanned);
        operatorVm.SetCovenantBanChecker(banVm.IsCovenantBanned);

        var covenantNames = operators
            .SelectMany(o => o.CoreCovenants.Concat(o.AdditionalCovenants))
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct()
            .ToList();

        var equipmentVm = new EquipmentBrowseViewModel();
        equipmentVm.LoadEquipment(equipment, covenantNames, manualJobChangeEquipments, jobChangeEquipmentToCovenant);

        var trackingVm = new TrackingViewModel
        {
            TrackMultipleLineups = await _settingsManager.LoadTrackMultipleLineupsAsync()
        };
        trackingVm.LoadState(trackingState);
        var stageUpgrades = lineups.Where(l => trackingState.Lineups.Any(t => t.Id == l.Id && t.Stages.Count == 0)).ToList();
        if (stageUpgrades.Count > 0)
        {
            File.Copy(Path.Combine(settingsDir, "tracking.json"), Path.Combine(settingsDir, $"tracking.pre-stages-{Guid.NewGuid():N}.bak"));
            foreach (var lineup in stageUpgrades) trackingVm.ApplyTrackedLineup(lineup);
            await _settingsManager.SaveTrackingStateAsync(trackingVm.CaptureState());
        }


        var gameStateVm = new GameStateViewModel();

        var lineupVm = new LineupViewModel(lineupStore, covenantCalculator);
        lineupVm.LoadLineups(lineups);
        var lineupShareService = new LineupShareService(covenantCalculator.EquipmentRules, Path.Combine(settingsDir, "share-links"));

        // GameState
        var gameState = new Core.GameState.GameState();

        // Game mode
        var garrisonMode = new GarrisonProtocolMode();

        // Services
        var captureService = _captureService = new ScreenCaptureService(
            new GameConnectionService(await _settingsManager.LoadGameConnectionAsync()));
        var ocrEngine = new MaaOcrEngine(dataDir);
        // The shop reader matches the whole catalogue; highlighting against tracking happens afterwards,
        // so switching the tracked lineup updates markers without waiting for a new scan.
        var shopNames = operators.Where(o => o.RequiredStrategyId.Length == 0).Select(o => o.Name)
            .Concat(equipment.Select(e => e.Name)).Distinct().ToList();
        var purchaseNames = shopNames.ToHashSet(StringComparer.Ordinal);
        var shopReader = new ShopReader(ocrEngine, shopNames) { Theme = gameTheme };
        // Covenant groups come from covenants.json (bondType), not from the operator roster:
        // a covenant with no operator in the local data set (a new season addition) must
        // still be recognised on the ban screen.
        var coreCovenantNames = covenantInfos
            .Where(c => string.Equals(c.BondType, "SEASON", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Name)
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct()
            .ToList();
        var additionalCovenantNames = covenantInfos
            .Where(c => !string.Equals(c.BondType, "SEASON", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Name)
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct()
            .ToList();
        AppLogger.Info("App",
            $"Covenant names: core={coreCovenantNames.Count} additional={additionalCovenantNames.Count}");

        var enemyRecognizer = new EnemyRecognizer(ocrEngine, garrisonMode.OcrStrategy, bossDataService);
        var boardData = await Core.Data.GarrisonBoardStore.LoadAsync(dataDir);
        var roundRoutes = await GarrisonRoundRouteStore.LoadAsync(dataDir);
        _ocrScanner = new OcrScannerService(
            captureService.CaptureScreen, garrisonMode.OcrStrategy, shopReader, ocrEngine, gameState, shopNames,
            intervalMs: 500,
            getConnection: () => captureService.Connection)
        {
            TerminalReader = new TerminalLevelReader(ocrEngine),
            EconomyReader = new ShopEconomyReader(ocrEngine),
            ThemeProvider = () => shopReader.Theme,
            ThemeDetector = new GameShopThemeDetector(Path.Combine(dataDir, "shop_theme_refs.json")),
            AutomaticGameTheme = automaticGameTheme,
            RoundEnemyReader = new RoundEnemyReader(ocrEngine, roundRoutes,
                Views.CodexBounty.Load().Select(b => b.Title).ToHashSet(StringComparer.Ordinal)),
            CaptureEnemyPanelFrame = captureService.CaptureEnemyPanelFrame,
            StartupInformationEnabled = await _settingsManager.LoadStartupInformationAsync(),
            AutomaticRoundEnemies = await _settingsManager.LoadAutomaticRoundEnemiesAsync(),
            AutomaticPurchaseTracking = await _settingsManager.LoadAutomaticPurchaseTrackingAsync(),
            PurchaseSpeed = await _settingsManager.LoadPurchaseRecognitionSpeedAsync(),
            BoardRecognizer = boardData is null ? null : new BoardRecognizer(boardData),
            PurchaseTargets = () => purchaseNames,
            LiveReader = boardData == null ? null : new LiveFieldReader(dataDir, boardData),
            FieldTiers = operators.GroupBy(o => o.Name).ToDictionary(g => g.Key, g => g.First().Tier),
        };

        _windowTracker = new WindowTrackerService(
            findWindow: () => captureService.Connection.FindGameWindow(),
            getViewport: window => captureService.Connection.GetViewport(window));

        // Windows
        var sidePanelPosition = await _settingsManager.LoadSidePanelPositionAsync();
        var sidePanel = _sidePanel = new SidePanelWindow(
            operatorVm, equipmentVm, trackingVm, gameStateVm, banVm, enemyVm,
            lineupVm, lineupShareService,
            operators, equipment, strategies, _settingsManager, gameTheme, sidePanelPosition,
            new LineupPlanner(operators, covenantInfos), covenantInfos, appearance: appearance,
            closeBehavior: await _settingsManager.LoadMainWindowCloseBehaviorAsync());
        sidePanel.SetCodexBosses(bossDataService.Bosses);
        sidePanel.InitGameStartSettings(await _settingsManager.LoadGameStartSettingsAsync());
        if (configurationImport is not null) sidePanel.ShowConfigurationImportResult(configurationImport);
        sidePanel.GameThemeChanged += theme => shopReader.Theme = theme;
        sidePanel.InitAutomaticGameTheme(automaticGameTheme);
        sidePanel.AutomaticGameThemeChanged += automatic => _ocrScanner.AutomaticGameTheme = automatic;
        sidePanel.InitAutomaticPurchaseTracking(_ocrScanner.AutomaticPurchaseTracking);
        sidePanel.InitAutomaticCurrentRunFilters(await _settingsManager.LoadAutomaticCurrentRunFiltersAsync());
        sidePanel.AutomaticPurchaseTrackingChanged += enabled => _ocrScanner.AutomaticPurchaseTracking = enabled;
        sidePanel.InitStartupInformation(_ocrScanner.StartupInformationEnabled);
        sidePanel.StartupInformationChanged += enabled => { if (!enabled) _ocrScanner.CancelStartupInformation(); };
        sidePanel.InitAutomaticRoundEnemies(_ocrScanner.AutomaticRoundEnemies);
        sidePanel.AutomaticRoundEnemiesChanged += enabled => _ocrScanner.AutomaticRoundEnemies = enabled;
        sidePanel.InitPurchaseRecognitionSpeed(_ocrScanner.PurchaseSpeed);
        sidePanel.PurchaseRecognitionSpeedChanged += speed => _ocrScanner.PurchaseSpeed = speed;
        _ocrScanner.GameThemeDetected += theme => Dispatcher.Invoke(() => sidePanel.ApplyDetectedGameTheme(theme));
        var overlay = _overlay = new OverlayWindow()
        {
            FindGameWindow = () => captureService.Connection.FindGameWindow(),
#if DEBUG
            ShowDebugCardBorders = false,
#endif
        };

        var informationScroll = new MaaScrollService();
        async Task<byte[]?> CaptureInformationScreenAsync(bool scrollDown)
        {
            var connection = captureService.Connection;
            if (connection.IsAdb)
            {
                if (scrollDown)
                {
                    if (!await Task.Run(connection.ScrollInformationPanelDown)) return null;
                    await Task.Delay(250);
                }
                if (!ReferenceEquals(connection, captureService.Connection)) return null;
                return await Task.Run(captureService.CaptureScreen);
            }
            var visibility = await Dispatcher.InvokeAsync(() =>
            {
                var game = captureService.Connection.FindGameWindow();
                if (game == IntPtr.Zero)
                {
                    AppLogger.Warn("ScreenCapture", "Manual capture rejected: game window not found");
                    return (Allowed: false, Panel: false, Overlay: false, Game: IntPtr.Zero);
                }
                var before = (Allowed: true, Panel: !sidePanel.IsAssistantHidden && sidePanel.IsVisible, Overlay: overlay.IsVisible, Game: game);
                if (!sidePanel.IsAssistantHidden) sidePanel.Hide();
                overlay.Hide();
                Core.Interop.User32.SetForegroundWindow(game);
                return before;
            });
            if (!visibility.Allowed) return null;
            try
            {
                // CopyFromScreen sees desktop pixels, including our attached panel.
                // Give the compositor time to display the game after hiding it.
                await Task.Delay(150);
                if (scrollDown)
                {
                    if (!ReferenceEquals(connection, captureService.Connection)) return null;
                    if (!await Task.Run(() => informationScroll.TryScrollAtWindowCenter(visibility.Game, -120))) return null;
                }
                if (!ReferenceEquals(connection, captureService.Connection)) return null;
                return await Task.Run(captureService.CaptureScreen);
            }
            finally
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (visibility.Panel && !sidePanel.IsAssistantHidden)
                    {
                        bool showActivated = sidePanel.ShowActivated;
                        sidePanel.ShowActivated = false;
                        sidePanel.Show();
                        sidePanel.ShowActivated = showActivated;
                    }
                    if (visibility.Overlay) UpdateOverlayPosition(refreshShopItems: false);
                });
            }
        }

        _banScreenAnalyzer = new BanScreenAnalyzer(
            () => CaptureInformationScreenAsync(false), ocrEngine, garrisonMode.OcrStrategy,
            enemyRecognizer, operators,
            coreCovenantNames, additionalCovenantNames);
        _banScreenAnalyzer.ScrollInformationPanelAsync = () => CaptureInformationScreenAsync(true);
        _banScreenAnalyzer.CaptureRevisionProvider = () => captureService.Connection.CaptureRevision;
        _banScreenAnalyzer.ScrollRequired += message => Dispatcher.BeginInvoke(() =>
            ConfirmDialog.Tell(sidePanel, "请下拉盟约面板", message));

        sidePanel.GameConnectionRequested += () =>
        {
            if (!_informationScanGate.Wait(0))
            {
                sidePanel.ShowToast("本局信息识别完成后可切换连接");
                return;
            }
            try
            {
                var dialog = new GameConnectionWindow(captureService, _settingsManager) { Owner = sidePanel };
                if (dialog.ShowDialog() == true)
                {
                    _lastShopItems = [];
                    overlay.UpdateShopItems([]);
                    sidePanel.UpdateGameConnectionStatus(captureService.Connection.IsAdb, captureService.Connection.Status);
                }
            }
            finally { _informationScanGate.Release(); }
        };
        _connectionStatusTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _connectionStatusTimer.Tick += (_, _) => sidePanel.UpdateGameConnectionStatus(captureService.Connection.IsAdb, captureService.Connection.Status);
        _connectionStatusTimer.Start();

        void UpdateOverlayPosition(bool refreshShopItems)
        {
            var foreground = Core.Interop.User32.GetForegroundWindow();
            sidePanel.UpdateTopmost(_windowTracker.CurrentGameWindowHandle, foreground);
            var panelHandle = new System.Windows.Interop.WindowInteropHelper(sidePanel).Handle;
            var viewport = _windowTracker.CurrentViewport;
            if (sidePanel.SuppressGameOverlay || viewport is null || _windowTracker.IsGameMinimized
                || foreground != _windowTracker.CurrentGameWindowHandle && foreground != panelHandle)
            {
                overlay.Hide();
                return;
            }
            overlay.UpdatePosition(viewport.Value);
            if (refreshShopItems)
                overlay.UpdateShopItems(_lastShopItems);
            overlay.Show();
        }

        // Wire window tracker → window positioning
        sidePanel.Activated += (_, _) => UpdateOverlayPosition(false);
        sidePanel.TopmostPreferenceChanged += () => UpdateOverlayPosition(false);
        sidePanel.Deactivated += (_, _) => Dispatcher.BeginInvoke(() => UpdateOverlayPosition(false));
        _windowTracker.GameWindowFocusChanged += _ => Dispatcher.Invoke(() => UpdateOverlayPosition(false));
        _windowTracker.GameWindowMoved += rect =>
        {
            Dispatcher.Invoke(() =>
            {
                _sidePanel.UpdatePosition(rect, _windowTracker.IsFullscreen);

                UpdateOverlayPosition(refreshShopItems: true);
            });
        };

        _windowTracker.GameWindowPolled += rect =>
        {
            Dispatcher.Invoke(() =>
            {
                _sidePanel.UpdatePosition(rect, _windowTracker.IsFullscreen);
                UpdateOverlayPosition(refreshShopItems: false);
            });
        };

        _windowTracker.GameWindowLost += () =>
        {
            Dispatcher.Invoke(() =>
            {
                _sidePanel.ClearGamePosition();
                _sidePanel.UpdateTopmost(_windowTracker.CurrentGameWindowHandle, Core.Interop.User32.GetForegroundWindow());
                _overlay.Hide();
            });
        };

        _windowTracker.GameWindowMinimizedChanged += minimized =>
        {
            Dispatcher.Invoke(() =>
            {
                if (!_sidePanel.IsAssistantHidden) _sidePanel.SetGameMinimized(minimized);
                if (minimized)
                {
                    _sidePanel.UpdateTopmost(_windowTracker.CurrentGameWindowHandle, Core.Interop.User32.GetForegroundWindow());
                    _overlay.Hide();
                    _overlay.UpdateShopItems([]);
                }
            });
        };

        // Wire OCR scanner → GameState → overlay markers
        _ocrScanner.GameStateUpdated += state =>
        {
            Dispatcher.Invoke(() =>
            {
                Core.GameState.GameStateUpdater.UpdateShopTracking(
                    state.ShopItems, _shopTrackingEntries);

                _lastShopItems = state.ShopItems;

                state.CovenantCounts = Core.GameState.GameStateUpdater.ComputeCovenantCounts(
                    [.. state.FieldOperators, .. state.BenchOperators], operators);

                trackingVm.CovenantCounts = state.CovenantCounts;
                gameStateVm.UpdateFromGameState(state);

                _overlay.UpdateShopItems(state.ShopItems);
            });
        };


        // Board recognition → deployment overlay + status under the settings toggle.
        var showBoard = await _settingsManager.LoadShowBoardOverlayAsync();
        sidePanel.InitBoardOverlay(showBoard);
        // 追踪 · 站位: placement planning for the recognised map; its plan is drawn on the same board overlay.
        var deployState = await _settingsManager.LoadDeployPoolAsync();
        var deploy = sidePanel.TrackingView.DeployPage;
        deploy.Initialize(operators.GroupBy(o => o.Name).ToDictionary(g => g.Key, g => g.First()), covenantInfos,
            boardData, trackingVm, _settingsManager, deployState, deployState.ShowInGame, roundRoutes);
        deploy.ConnectLiveField(_ocrScanner);
        _ocrScanner.PurchasesDetected += (names, captureRevision, runRevision) => Dispatcher.Invoke(() =>
        {
            if (captureRevision != captureService.Connection.CaptureRevision || runRevision != _ocrScanner.RunRevision) return;
            deploy.ApplyRecognizedPurchases(names, _ocrScanner.LastPurchase.OfferedAt, _ocrScanner.LastPurchase.Round);
        });
        void ApplyBoard(BoardMatch? match)
        {
            sidePanel.SetBoardStatus(match?.Map.Name, match?.Camera == Core.Models.BoardCamera.Expanded);
            deploy.SetRecognizedBoard(match);
            // 站位页的“游戏内显示”是总开关；设置里的“游戏内显示部署格”只管要不要同时画格子和路线。
            bool draw = deploy.ShowInGame;
            overlay.UpdateBoard(draw ? match?.Map : null,
                draw && match is not null ? boardData?.Cameras[match.Camera] : null, showCells: false);   // no grid, only the plan
        }
        deploy.PlanChanged += plan => overlay.UpdatePlacements(plan);
        deploy.ShowInGameChanged += enabled =>
        {
            deployState.ShowInGame = enabled;
            _ = _settingsManager.SaveDeployPoolAsync(deployState);
            ApplyBoard(_ocrScanner.CurrentBoard);
        };
        _ocrScanner.BoardChanged += match => Dispatcher.Invoke(() => ApplyBoard(match));
        sidePanel.BoardOverlayToggled += enabled =>
        {
            showBoard = enabled;
            ApplyBoard(_ocrScanner.CurrentBoard);
        };

        // Enemy recognition result → update enemy tab (include boss + enemy-type data).
        void UpdateEnemyPage(Core.Models.EnemyResult result)
        {
            var bossInfo   = bossDataService.Lookup(result.BossName);
            var typeEntries = result.EnemyTypes
                .Select(t => enemyTypeDataService.LookupByTypeName(t.Split('·').Last()))
                .ToList();
            Dispatcher.Invoke(() => enemyVm.Update(result, bossInfo, typeEntries));
        }
        _banScreenAnalyzer.EnemyDetected += UpdateEnemyPage;

        _banScreenAnalyzer.AnalysisBusyChanged += busy =>
        {
            Dispatcher.Invoke(() =>
            {
                sidePanel.UpdateManualScanStatus(busy ? 1 : 0);
                if (busy) enemyVm.BeginRecognition();
                else if (enemyVm.IsRecognizing)
                    enemyVm.RecognitionFailed("本次未识别到敌人信息，请打开本局信息页后点击“截屏录入”重试。");
                _ocrScanner?.SetBanScreenDetectionPaused(busy);
                _sidePanel?.SetBanProcessingMaskVisible(
                    busy,
                    "ban位识别中，请勿操作鼠标");
                if (busy) _overlay?.ShowBanInputBlockOverlay();
                else _overlay?.HideBanInputBlockOverlay();
            });
        };

        _banScreenAnalyzer.AnalysisFailed += message =>
            Dispatcher.Invoke(() =>
            {
                enemyVm.RecognitionFailed(message);
                sidePanel.ShowToast(message);
            });

        _banScreenAnalyzer.AutoBanListComputed += names =>
        {
            Dispatcher.Invoke(() => banVm.SetBans(names));
        };

        _banScreenAnalyzer.AnalysisCompleted += result =>
        {
            _ocrScanner.MarkInformationRecorded();
            Dispatcher.Invoke(() =>
            {
                banVm.SetCovenantBans(result.CoreCovenants.Concat(result.AdditionalCovenants).Where(c => c.IsBanned).Select(c => c.Name));
                operatorVm.SetAvailableCovenants(result.CoreCovenants.Concat(result.AdditionalCovenants)
                    .Where(c => !c.IsBanned).Select(c => c.Name));
                sidePanel.ApplyAutomaticCurrentRunFilters();
                if (result.EnemyFaction is null)
                    enemyVm.RecognitionFailed("盟约已录入，本次敌人信息未完整读出，可点击“截屏录入”重试。");
                sidePanel.ShowToast(result.EnemyFaction is null ? "盟约已录入，敌人信息可重新截屏录入" : "本局信息已录入");
                sidePanel.NavigateAfterGameStart();
            });
        };

        _banScreenAnalyzer.ShowGameOverlayMessageRequested += message =>
        {
            Dispatcher.Invoke(() =>
            {
                if (_windowTracker.IsGameFocused) _overlay?.ShowBanTimedMessageOverlay(message);
            });
        };
        _banScreenAnalyzer.HideGameOverlayMessageRequested += () =>
        {
            Dispatcher.Invoke(() => _overlay?.HideBanTimedMessageOverlay());
        };

        _banScreenAnalyzer.PrepareNewRunAsync = async bansChanged =>
        {
            if (!_ocrScanner.TryStartNewRun(bansChanged)) return;
            _ocrScanner.ResetTerminalLevel();
            _ocrScanner.ResetRegulationLevel();
            _ocrScanner.ResetEconomy();
            await Dispatcher.InvokeAsync(deploy.StartNewRunAsync).Task.Unwrap();
            await Dispatcher.InvokeAsync(() =>
            {
                enemyVm.Clear();
                sidePanel.PlannerVm.Strategy = PlannerStrategy.Other;
            });
            await _settingsManager.SaveTerminalLevelAsync(0);
            AppLogger.Info("Run", bansChanged ? "Changed Ban set confirmed; progress reset for the new run"
                : "Opening confirmed; progress reset for the new run");
        };

        // Both entry points classify the same captured page before deciding whether to reset progress.
        async Task<bool> RecordInformationAsync(bool showGameResult = true)
        {
            if (!_informationScanGate.Wait(0)) return false;
            try
            {
                return await _banScreenAnalyzer.RunAnalysisAsync(showGameResult);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLogger.Warn("Run", $"Failed to save run state: {ex.Message}");
                Dispatcher.Invoke(() => sidePanel.ShowToast($"本局状态保存失败：{ex.Message}"));
                return false;
            }
            finally
            {
                _informationScanGate.Release();
            }
        }
        _ocrScanner.RecordStartupInformationAsync = () => RecordInformationAsync(showGameResult: false);
        _ocrScanner.BanScreenDetected += async () => await RecordInformationAsync();
        _ocrScanner.RegulationLevelChanged += () =>
            Dispatcher.Invoke(() => enemyVm.RegulationLevel = _ocrScanner.RegulationLevel);
        _ocrScanner.RoundEnemiesChanged += () => Dispatcher.Invoke(() =>
        {
            enemyVm.RoundEnemies = _ocrScanner.CurrentRoundEnemies;
            deploy.SetRoundRouteContext(_ocrScanner.CurrentEconomy, enemyVm.RoundEnemies);
        });
        enemyVm.RoundEnemyReadRequested += () =>
        {
            if (_ocrScanner.RequestRoundEnemyRead() is { } hint)
            {
                sidePanel.ShowToast(hint);
                return;
            }
            var connection = captureService.Connection;
            if (!connection.IsAdb)
            {
                // The recognition button is on the assistant; desktop capture needs the game in front.
                var game = connection.FindGameWindow();
                if (game != IntPtr.Zero && Core.Interop.User32.GetForegroundWindow() != game)
                    Core.Interop.User32.SetForegroundWindow(game);
            }
        };
        _ocrScanner.EconomyChanged += _ => Dispatcher.Invoke(() =>
        {
            var economy = _ocrScanner.CurrentEconomy;
            trackingVm.Economy = economy;
            deploy.SetRoundRouteContext(economy, _ocrScanner.CurrentRoundEnemies);
            if (economy is null) enemyVm.CurrentMode = null;
            if (economy?.Mode is { } mode) enemyVm.CurrentMode = mode;
        });
        _ocrScanner.TerminalLevelChanged += level =>
        {
            Dispatcher.Invoke(() =>
            {
                trackingVm.DetectedTerminalLevel = level;
                trackingVm.IsTerminalLevelRestored = false;
            });
            _ = _settingsManager!.SaveTerminalLevelAsync(level);
        };
        // Restarted mid-run: start from the saved level until the shop's upgrade card confirms or replaces it.
        if (await _settingsManager!.LoadTerminalLevelAsync() is { } savedLevel)
        {
            _ocrScanner.RestoreTerminalLevel(savedLevel);
            trackingVm.DetectedTerminalLevel = savedLevel;
            trackingVm.IsTerminalLevelRestored = true;
        }
        trackingVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TrackingViewModel.TerminalLevel)) RefreshOverlayTracking(trackingVm);
            else if (e.PropertyName == nameof(TrackingViewModel.OperatorProgress) &&
                trackingVm.ShowAllStages && !trackingVm.StageTargetsUpdating)
                RefreshOverlayTracking(trackingVm, scanIfEmpty: false);
        };
        _sidePanel.ManualScanRequested += async () => await RecordInformationAsync();

        // Manual ban/unban from operator card button.
        _sidePanel.BanToggleRequested += (name, banned) =>
        {
            banVm.ToggleBan(name);
        };


        // Persist entries and their lineup associations in one atomic snapshot.
        async Task SaveTrackingAsync()
        {
            try { await _settingsManager.SaveTrackingStateAsync(trackingVm.CaptureState()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLogger.Warn("Tracking", $"Failed to save tracking: {ex.Message}");
                sidePanel.ShowToast($"追踪保存失败：{ex.Message}");
            }
        }
        trackingVm.TrackedOperators.CollectionChanged += async (_, _) =>
        {
            if (trackingVm.StageTargetsUpdating) return;
            RefreshOverlayTracking(trackingVm);
            await SaveTrackingAsync();
        };
        trackingVm.TrackedEquipment.CollectionChanged += async (_, _) =>
        {
            if (trackingVm.StageTargetsUpdating) return;
            RefreshOverlayTracking(trackingVm);
            await SaveTrackingAsync();
        };
        trackingVm.StageStateChanged += async () =>
        {
            RefreshOverlayTracking(trackingVm, scanIfEmpty: false);
            await SaveTrackingAsync();
        };
        trackingVm.SourcesChanged += async () =>
        {
            RefreshOverlayTracking(trackingVm, scanIfEmpty: false);
            await SaveTrackingAsync();
        };
        trackingVm.UpgradeReminderStateChanged += async () => await SaveTrackingAsync();
        trackingVm.UpgradeReminderRequested += reminder => sidePanel.ShowToast($"{reminder.Title} · {reminder.RangeText}");

        // Show windows and start services
        _shopTrackingEntries = trackingVm.ShopTrackingEntries;
        MainWindow = _sidePanel;
        _sidePanel.Show();
        if (_settingsManager.DeployPoolRecoveryNotice is { } recoveryNotice)
            ConfirmDialog.Tell(_sidePanel, "已恢复干员拥有状态", recoveryNotice);
        _overlay.Show();
        _assistantTray = new AssistantTray(_sidePanel, _overlay);
        _assistantTray.VisibilityChanged += () =>
        {
            if (!sidePanel.IsAssistantHidden) sidePanel.SetGameMinimized(_windowTracker.IsGameMinimized);
            UpdateOverlayPosition(refreshShopItems: true);
        };
        await _sidePanel.InitRecognitionHotkeyAsync();
        await _sidePanel.InitAssistantHotkeyAsync();
        await _sidePanel.InitGuideHotkeyAsync();
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        _windowTracker.Start();
        _ocrScanner.Start();
        AppLogger.Info("App", "Startup complete");
        AppLogger.SetGamePresenceCheck(() => captureService.Connection.IsAdb || _windowTracker.IsGameFound);

        // Start background update check
        _ = _sidePanel.StartUpdateCheckAsync();
        return true;
    }

    private void RefreshOverlayTracking(TrackingViewModel trackingVm, bool scanIfEmpty = true)
    {
        if (_overlay is null) return;
        _shopTrackingEntries = trackingVm.ShopTrackingEntries;
        Core.GameState.GameStateUpdater.UpdateShopTracking(
            _lastShopItems, _shopTrackingEntries);
        Dispatcher.Invoke(() => _overlay.UpdateShopItems(_lastShopItems));
        if (scanIfEmpty && _lastShopItems.Count == 0 && trackingVm.AllEntries.Any(e => !e.IsAcquired))
            _ = _ocrScanner!.ScanNowAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLogger.Info("App", "Shutting down");
        _assistantTray?.Dispose();
        _sidePanel?.ReleaseAssistantHotkey();
        _sidePanel?.ReleaseRecognitionHotkey();
        _sidePanel?.ReleaseGuideHotkey();
        _ocrScanner?.Dispose();
        _windowTracker?.Dispose();
        _connectionStatusTimer?.Stop();
        _captureService?.Connection.Dispose();
        _sidePanel?.InstallPreparedUpdateOnExit();
        base.OnExit(e);
        // BanScreenAnalyzer and BanIndicatorDetector hold no unmanaged resources
        // of their own beyond what MaaTasker cleans up; no explicit Dispose needed.
    }
}
