// Modified for ASP Assistant Regulation on 2026-11-13; see NOTICE.
using System.Threading.Channels;
using System.Diagnostics;
using System.Drawing;
using System.Timers;
using ASPAssistant.Core.GameModes;
using ASPAssistant.Core.Models;
using Timer = System.Timers.Timer;

namespace ASPAssistant.Core.Services;

public partial class OcrScannerService : IDisposable
{
    private readonly Func<byte[]?> _captureScreen;
    private readonly Func<GameConnectionService>? _getConnection;
    private readonly IOcrStrategy _ocrStrategy;
    private readonly ShopReader _shopReader;
    private readonly IOcrEngine _ocrEngine;
    private readonly RegulationLevelReader _regulationReader;
    private readonly object _runLock = new();
    private int? _regulationLevel;
    private int _runRevision;
    private DateTime _nextRegulationRead = DateTime.MinValue;
    private readonly GameState.GameState _gameState;
    private readonly IReadOnlyList<string> _shopNames;
    private readonly Timer _scanTimer;
    private readonly int _normalScanIntervalMs;
    private bool _purchaseShopOpen;
    private bool _disposed;
    private PurchaseRecognitionSpeed _purchaseSpeed = PurchaseRecognitionSpeed.Standard;
    public PurchaseRecognitionSpeed PurchaseSpeed
    {
        get { lock (_runLock) return _purchaseSpeed; }
        set
        {
            lock (_runLock)
            {
                _purchaseSpeed = value;
                UpdateScanInterval();
            }
        }
    }
    private readonly ShopStateStabilizer _stabilizer = new();
    private readonly ShopPurchaseDetector _purchases = new();
    private ShopEconomy? _purchaseEconomy;
    public Func<IReadOnlySet<string>>? PurchaseTargets { get; init; }
    public event Action<IReadOnlyList<string>, long, int>? PurchasesDetected;
    public int RunRevision { get { lock (_runLock) return _runRevision; } }
    private bool _automaticPurchaseTracking;
    public bool AutomaticPurchaseTracking
    {
        get { lock (_runLock) return _automaticPurchaseTracking; }
        set
        {
            lock (_runLock)
            {
                if (_automaticPurchaseTracking == value) return;
                _automaticPurchaseTracking = value;
                _runRevision++;
                AppLogger.Info("ShopPurchase", $"Automatic marking enabled={value}, revision={_runRevision}");
                _purchases.Reset("automatic-marking-changed");
                _purchaseEconomy = null;
                UpdateScanInterval();
            }
        }
    }
    private int _scanning; // re-entry guard (0=idle, 1=busy)

    // Accumulated ban set for manual screenshot scans.

    // Manual screenshot scan queue — button-triggered scans execute sequentially.

    // Ban screen detection state machine.
    private bool _banScreenActive;
    private InformationScreenState _informationState = new();
    private GameConnectionService? _lastConnection;
    private long _lastCaptureRevision;
    private readonly InformationScreenDetector _informationDetector;
    private int _banScreenAbsentTicks;
    private volatile bool _pauseBanScreenDetection;
    private const int BanLostThreshold   = 3;

    public event Action<GameState.GameState>? GameStateUpdated;

    public event Action? RegulationLevelChanged;
    public int? RegulationLevel { get { lock (_runLock) return _regulationLevel; } }

    public void ResetRegulationLevel()
    {
        lock (_runLock)
        {
            _runRevision++;
            _regulationLevel = null;
            _nextRegulationRead = DateTime.MinValue;
        }
        RegulationLevelChanged?.Invoke();
    }

    /// <summary>Raised when the (confirmed) terminal level read from the shop's upgrade card changes.</summary>
    public event Action<int>? TerminalLevelChanged;

    /// <summary>Optional: reads the terminal level every <see cref="TerminalReadInterval"/> while the shop is visible.</summary>
    public TerminalLevelReader? TerminalReader { get; init; }

    /// <summary>Round, funds, upgrade price and mode from the shop HUD; null leaves them unread.</summary>
    public ShopEconomyReader? EconomyReader { get; init; }

    /// <summary>Raised on the scan thread when the confirmed shop HUD values change (null after a reset).</summary>
    public event Action<ShopEconomy?>? EconomyChanged;
    public ShopEconomy? CurrentEconomy { get { lock (_runLock) return _economy.Current; } }

    private readonly ShopEconomyTracker _economy = new();
    private DateTime _nextEconomyRead;
    private static readonly TimeSpan EconomyReadInterval = TimeSpan.FromSeconds(2);

    /// <summary>Recognises the preparation board (map + camera) for the deployment overlay; optional.</summary>
    public BoardRecognizer? BoardRecognizer { get; init; }
    private BoardMatchStabilizer _board = new();

    /// <summary>The recognised board changed (null: no board on screen).</summary>
    public event Action<BoardMatch?>? BoardChanged;
    public BoardMatch? CurrentBoard => _board.Current;
    public Func<GameShopTheme>? ThemeProvider { get; init; }
    public GameShopThemeDetector? ThemeDetector { get; init; }
    public event Action<GameShopTheme>? GameThemeDetected;
    private volatile bool _automaticGameTheme;
    public bool AutomaticGameTheme { get => _automaticGameTheme; set => _automaticGameTheme = value; }
    private GameShopTheme? _themeCandidate;
    private int _themeConfirmations;
    public TimeSpan TerminalReadInterval { get; init; } = TimeSpan.FromSeconds(4);
    private readonly TerminalLevelTracker _terminalTracker = new();
    private DateTime _nextTerminalRead = DateTime.MinValue;

    /// <summary>New run: forget the previous run's terminal level.</summary>
    public void ResetTerminalLevel()
    {
        _terminalTracker.Reset();
        _gameState.TerminalLevel = 0;
        _nextTerminalRead = DateTime.MinValue;
    }

    /// <summary>Starts from the level the last session saved; the shop's upgrade card confirms or replaces it.</summary>
    public void RestoreTerminalLevel(int level)
    {
        _terminalTracker.Restore(level);
        _gameState.TerminalLevel = _terminalTracker.Level ?? 0;
    }

    /// <summary>
    /// Raised when the periodic scan first detects that the game is on the ban selection screen.
    /// Fires once per entry; resets after the screen is left.
    /// </summary>
    public event Action? BanScreenDetected;

    /// <summary>
    /// Raised when the ban selection screen is no longer detected.
    /// </summary>
    public event Action? BanScreenLost;

    public OcrScannerService(
        Func<byte[]?> captureScreen,
        IOcrStrategy ocrStrategy,
        ShopReader shopReader,
        IOcrEngine ocrEngine,
        GameState.GameState gameState,
        IReadOnlyList<string> shopNames,
        int intervalMs = 3000,
        Func<GameConnectionService>? getConnection = null)
    {
        _captureScreen = captureScreen;
        _getConnection = getConnection;
        _lastConnection = getConnection?.Invoke();
        _lastCaptureRevision = _lastConnection?.CaptureRevision ?? 0;
        _ocrStrategy = ocrStrategy;
        _shopReader = shopReader;
        _ocrEngine = ocrEngine;
        _regulationReader = new RegulationLevelReader(ocrEngine);
        _informationDetector = new InformationScreenDetector(ocrEngine, ocrStrategy);
        _gameState = gameState;
        _shopNames = shopNames;
        _normalScanIntervalMs = intervalMs;
        _scanTimer = new Timer(intervalMs);
        _scanTimer.Elapsed += OnScanTick;
        _panelProbeTimer.Elapsed += (_, _) => ProbeBusyRound();
    }

    public void Start()
    {
        if (CaptureEnemyPanelFrame is not null) _panelProbeTimer.Start();
        _scanTimer.Start();
    }

    // Called under _runLock; changing an unchanged interval would restart the timer.
    private void UpdateScanInterval()
    {
        if (_disposed) return;
        var interval = _automaticPurchaseTracking && _purchaseShopOpen
            ? Math.Min(_normalScanIntervalMs, (int)_purchaseSpeed) : _normalScanIntervalMs;
        if (_scanTimer.Interval != interval) _scanTimer.Interval = interval;
    }

    /// <summary>
    /// Temporarily pauses ban-screen state-machine OCR checks. This is used while
    /// app-controlled fullscreen overlays are shown, so those overlays do not
    /// accidentally trigger BanScreenLost/BanScreenDetected flapping.
    /// </summary>
    public void SetBanScreenDetectionPaused(bool paused)
    {
        _pauseBanScreenDetection = paused;
        if (paused)
        {
            // Freeze state transitions while overlays cover the game window.
            // Keep _banScreenActive as-is, but clear accumulated edge counters
            // to avoid immediate false lost/detected transitions after resume.
            _informationState.Suspend();
            _banScreenAbsentTicks = 0;
            lock (_runLock) { _purchaseShopOpen = false; UpdateScanInterval(); }
        }
        AppLogger.Info("BanScreen", $"Ban screen detection paused={paused}");
    }

    private async void OnScanTick(object? sender, ElapsedEventArgs e) => await ScanAsync();

    public Task ScanNowAsync() => Task.Run(ScanAsync);

    private async Task ScanAsync()
    {
        // Skip if a previous scan is still running.
        lock (_runLock)
        {
            if (_disposed || _manualField != 0 || Interlocked.CompareExchange(ref _scanning, 1, 0) != 0) return;
        }

        GameConnectionService? connection = null;
        long captureRevision = 0;
        byte[]? diagnosticFrame = null;
        var capturedAt = DateTimeOffset.Now;
        bool observedEnemyFrame = false;
        var diagnosticStatus = "读取中";
        string? diagnosticError = null;
        string? rawText = null;
        object? diagnosticItems = null;
        var shopOpenThisScan = false;
        var elapsed = Stopwatch.StartNew();
        try
        {
            connection = _getConnection?.Invoke();
            captureRevision = connection?.CaptureRevision ?? 0;
            if (!ReferenceEquals(connection, _lastConnection) || captureRevision != _lastCaptureRevision)
            {
                _lastConnection = connection;
                _lastCaptureRevision = captureRevision;
                _board = new();
                BoardChanged?.Invoke(null);
                ResetTerminalLevel();
                _stabilizer.Reset();
                _themeCandidate = null;
                _themeConfirmations = 0;
                _informationState = new InformationScreenState();
                _banScreenActive = false;
                _banScreenAbsentTicks = 0;
                ResetRegulationLevel();
                ResetEconomy();
                _gameState.ShopItems = [];
                GameStateUpdated?.Invoke(_gameState);
            }
            ExpireRoundEnemyRead();
            int runRevision;
            BufferedPanel? pending;
            bool awaitingEnemyPanel;
            lock (_runLock)
            {
                runRevision = _runRevision;
                pending = _bufferedPanel;
                awaitingEnemyPanel = _roundEnemyState.AwaitingPanel;
            }
            var stageStart = elapsed.ElapsedMilliseconds;
            var frameTick = Environment.TickCount64;
            var screenshot = _captureScreen();
            var captureMs = elapsed.ElapsedMilliseconds - stageStart;
            if (!IsCurrentCapture(connection, captureRevision)) return;
            if (screenshot == null)
            {
                lock (_runLock) _purchases.Reset("capture-unavailable");
                _informationState.Suspend();
                if (connection?.IsAdb == true)
                    PublishShopItems([]);
                else
                    // 切换到辅助窗口时暂时无法截图，保留最后一次识别供追踪切换使用。
                    _stabilizer.Reset();
                return;
            }

            diagnosticFrame = screenshot;
            // Derive dimensions from the actual PNG bytes so that every downstream
            // coordinate (ROI, card boxes, normalization) lives in the same pixel
            // space as the image MAA will process. Using GetClientRect here was
            // wrong when ScreenCaptureService downscales the capture to MaxCaptureWidth.
            var (imgWidth, imgHeight) = GetPngDimensions(screenshot);
            if (imgWidth <= 0 || imgHeight <= 0)
            {
                diagnosticStatus = "截图尺寸无效";
                return;
            }

            var samples = RecognitionDiagnostics.Samples;
            samples.Observe(screenshot, CurrentEconomy?.Round, capturedAt);
            BmpPixels? scenePixels = null;
            BmpPixels? Pixels() => scenePixels ??= BmpPixels.TryRead(ImageBytes.ToBmp(screenshot));
            if (LiveReader != null && !_pauseBanScreenDetection && _fieldOpeningRevision != runRevision && LiveRoster.Map == null &&
                Pixels() is { } opening && OpeningWithoutCards(opening))
            {
                _fieldOpeningRevision = runRevision;
                await Task.Delay(220);
                if (!IsCurrentCapture(connection,captureRevision) || RunRevision != runRevision) return;
                frameTick = Environment.TickCount64;
                var fresh = _captureScreen();
                if (fresh == null || ImageBytes.GetDimensions(fresh) != (imgWidth,imgHeight) || !IsCurrentCapture(connection,captureRevision)) return;
                screenshot = diagnosticFrame = fresh;
                scenePixels = null;
            }

            // Capture the end of a selected, previously named card's purchase animation.
            // Keep the original observation unless the card actually clears in the same viewport.
            ShopItem? selectedCard = null;
            if (LiveReader != null && !_pauseBanScreenDetection && _liveState != null &&
                Pixels() is { } buying)
                lock (_runLock) selectedCard = _liveState?.ObservePurchaseButton(buying);
            if (selectedCard != null)
            {
                var purchaseSample = samples.Begin("购买 · " + selectedCard.Name, CurrentEconomy?.Round, includeRecent: true);
                samples.Step(purchaseSample, $"点选 {selectedCard.Name}", screenshot);
                await Task.Delay(180);
                if (!IsCurrentCapture(connection,captureRevision) || RunRevision != runRevision) return;
                var sampledAt = Environment.TickCount64;
                var newer = _captureScreen();
                samples.Step(purchaseSample, newer is null ? "未取得购买后画面" : "购买动画后", newer);
                if (newer != null && ImageBytes.GetDimensions(newer) == (imgWidth,imgHeight) &&
                    BmpPixels.TryRead(ImageBytes.ToBmp(newer)) is { } after &&
                    ShopPurchaseDetector.IsEmpty(after,selectedCard) &&
                    BoardRecognizer?.Recognize(after)?.Camera == BoardCamera.Expanded)
                {
                    screenshot = diagnosticFrame = newer;
                    frameTick = sampledAt;
                    scenePixels = after;
                }
            }

            // Ban screen detection runs every tick unless explicitly paused
            // (e.g. when app-owned overlays cover the game window).
            if (!_pauseBanScreenDetection)
                await DetectBanScreenAsync(screenshot, imgWidth, imgHeight, connection, captureRevision);

            if (!IsCurrentCapture(connection, captureRevision)) return;

            if (pending is not null && !_pauseBanScreenDetection && !_banScreenActive)
                await ReadBufferedEnemyPanelAsync(pending, screenshot, imgWidth, imgHeight, connection, runRevision, captureRevision);
            bool confirmingEnemyPanel;
            lock (_runLock)
                confirmingEnemyPanel = !_pauseBanScreenDetection && !_banScreenActive &&
                    (_roundEnemyState.AwaitingPanel || _roundEnemyState.Info?.IsReading == true);
            using var enemyStream = confirmingEnemyPanel ? new MemoryStream(screenshot) : null;
            using var enemyFrame = enemyStream is null ? null : new Bitmap(enemyStream);
            (Bitmap Frame, RoundEnemyReader.Layout Layout)? preview = null;
            if (enemyFrame is not null)
            {
                preview = (enemyFrame, RoundEnemyReader.Inspect(enemyFrame, locateButton: true));
                if (!preview.Value.Layout.Title.IsEmpty)
                {
                    var panelHud = await EconomyReader!.ReadPreparationAsync(screenshot, imgWidth, imgHeight);
                    if (panelHud.IsPreparation && panelHud.Round.HasValue)
                    {
                        PublishEconomyReading(panelHud, connection, runRevision, captureRevision, fullHud: true);
                        await ObserveRoundEnemiesAsync(screenshot, panelHud, false, connection, runRevision, captureRevision, frameTick, preview);
                    }
                    else
                    {
                        lock (_runLock) _roundEnemyState.PauseConfirmationClock();
                    }
                    observedEnemyFrame = true;
                }
            }
            if (!IsCurrentCapture(connection, captureRevision)) return;

            if (!_pauseBanScreenDetection)
                await ReadRegulationLevelAsync(screenshot, imgWidth, imgHeight, connection, runRevision, captureRevision);
            if (!IsCurrentCapture(connection, captureRevision)) return;

            if (_pauseBanScreenDetection || _banScreenActive)
            {
                diagnosticStatus = _pauseBanScreenDetection ? "识别已暂停" : "本局信息页，暂停商店读取";
                lock (_runLock) _purchases.Reset(_pauseBanScreenDetection ? "recognition-paused" : "information-screen");
                PublishShopItems([]);
                ObserveBoard(null);
                return;
            }

            // MuMu / ADB frames are PNG; the board reader samples raw BMP pixels.
            var pixels = BoardRecognizer == null ? null : Pixels();
            var boardMatch = pixels == null ? null : BoardRecognizer!.Recognize(pixels);
            bool checkStartup = StartupInformationEnabled && !_startupOpportunityConsumed && !_startupCancelled &&
                !confirmingEnemyPanel && !observedEnemyFrame;
            if (checkStartup && boardMatch != null &&
                await TryStartupInformationAsync(screenshot, connection, runRevision, captureRevision)) return;
            ReadGameTheme(screenshot);
            stageStart = elapsed.ElapsedMilliseconds;
            var rawItems = await _shopReader.ReadAsync(screenshot, imgWidth, imgHeight, _shopNames);
            var shopMs = elapsed.ElapsedMilliseconds - stageStart;
            if (!IsCurrentCapture(connection, captureRevision)) return;
            shopOpenThisScan = _shopReader.IsShopOpen;
            if (checkStartup && boardMatch == null && shopOpenThisScan &&
                await TryStartupInformationAsync(screenshot, connection, runRevision, captureRevision)) return;
            rawText = _shopReader.DiagnosticText;
            diagnosticItems = rawItems.Select(item => new { item.Name, item.Price,
                Region = new { item.OcrRegion.X, item.OcrRegion.Y, item.OcrRegion.Width, item.OcrRegion.Height } }).ToArray();
            diagnosticStatus = _shopReader.IsShopOpen ? "商店读取完成；请对照画面检查漏识别或错字" : "未检测到展开的商店";
            PublishShopItems(rawItems);
            var forceFundsRead = false;
            var automaticPurchases = AutomaticPurchaseTracking;
            if (automaticPurchases && PurchaseTargets is not null)
                lock (_runLock) forceFundsRead = _purchases.NeedsFundsRead(rawItems, _shopReader.IsShopOpen);
            ShopEconomy? economy = null;
            stageStart = elapsed.ElapsedMilliseconds;
            if (rawItems.Count > 0 || _shopReader.IsShopOpen)
                economy = await ReadEconomyAsync(screenshot, imgWidth, imgHeight, connection, runRevision, captureRevision, forceFundsRead, rawItems);
            if (forceFundsRead)
                AppLogger.Info("ShopPurchase", $"Funds read: capture={captureMs} ms, shopOcr={shopMs} ms, hud={elapsed.ElapsedMilliseconds - stageStart} ms");
            // Opening and closing the enemy panel hides the shop for several capture ticks.
            // Keep the last comparable offers and balance until the next gameplay frame.
            bool shopOccluded = observedEnemyFrame || awaitingEnemyPanel && !shopOpenThisScan;
            if (shopOccluded)
            {
                lock (_runLock)
                {
                    if (runRevision != _runRevision || _pauseBanScreenDetection || !IsCurrentCapture(connection, captureRevision)) return;
                    _purchases.PauseForEnemyPanel();
                }
            }
            else if (automaticPurchases && PurchaseTargets is { } targets)
            {
                var names = targets();
                IReadOnlyList<string> purchased;
                bool confirm;
                ShopEconomy? purchaseEconomy;
                lock (_runLock)
                {
                    if (runRevision != _runRevision || _pauseBanScreenDetection || !IsCurrentCapture(connection, captureRevision)) return;
                    if (economy is not null) _purchaseEconomy = economy;
                    purchased = _purchases.Observe(screenshot, rawItems, _shopReader.IsShopOpen, _purchaseEconomy, names, freshFunds: economy is not null);
                    confirm = economy is not null && _purchases.NeedsConfirmation && rawItems.Count > 0 && EconomyReader is not null;
                    purchaseEconomy = _purchaseEconomy;
                }
                long? purchaseCheck = null;
                if (confirm || purchased.Count > 0)
                {
                    purchaseCheck = samples.Begin("商店购买核对", purchaseEconomy?.Round, includeRecent: true);
                    samples.Step(purchaseCheck.Value, $"槽位变化 · 余额 {purchaseEconomy?.Money}", screenshot);
                }
                if (confirm)
                {
                    var confirmationTime = Stopwatch.StartNew();
                    var confirmation = _captureScreen();
                    var recheckCaptureMs = confirmationTime.ElapsedMilliseconds;
                    long? recheckHudMs = null;
                    if (confirmation is not null && IsCurrentCapture(connection, captureRevision)
                        && await _shopReader.IsSameOpenShopAsync(screenshot, confirmation, rawItems))
                    {
                        var hudStart = confirmationTime.ElapsedMilliseconds;
                        var freshEconomy = await EconomyReader!.ReadPurchaseAsync(confirmation, imgWidth, imgHeight, purchaseEconomy!);
                        recheckHudMs = confirmationTime.ElapsedMilliseconds - hudStart;
                        lock (_runLock)
                        {
                            if (runRevision != _runRevision || _pauseBanScreenDetection || !IsCurrentCapture(connection, captureRevision)) return;
                            _purchaseEconomy = freshEconomy;
                            purchased = _purchases.Observe(confirmation, rawItems, true, freshEconomy, names);
                        }
                    }
                    samples.Step(purchaseCheck!.Value, $"二次核对 · {(purchased.Count == 0 ? "未确认购买" : string.Join("、", purchased))}", confirmation);
                    AppLogger.Info("ShopPurchase", $"Lightweight recheck: confirmed={purchased.Count > 0}, elapsed={confirmationTime.ElapsedMilliseconds} ms, capture={recheckCaptureMs} ms, hud={recheckHudMs} ms");
                }
                if (purchased.Count > 0)
                {
                    samples.Step(purchaseCheck!.Value, "已确认购买 " + string.Join("、", purchased));
                    PurchasesDetected?.Invoke(purchased, captureRevision, runRevision);
                }
            }
            if (!IsCurrentCapture(connection, captureRevision)) return;
            ObserveBoard(boardMatch);
            if (!shopOccluded && LiveReader is not null && boardMatch is not null && !LiveFieldReader.IsDragging(pixels!) && IsCurrentCapture(connection, captureRevision) &&
                (economy ?? CurrentEconomy) is { Round: int fieldRound, IsPreparation: true } fieldHud)
            {
                var fieldWallet = economy;
                bool needsBalance;
                lock (_runLock) needsBalance = _liveState?.NeedsBalance(rawItems) ?? true;
                if (fieldWallet == null && needsBalance && shopOpenThisScan && EconomyReader != null)
                    fieldWallet = fieldHud with { Money = await EconomyReader.ReadMoneyAsync(screenshot,imgWidth,imgHeight) };
                var previousRoster = LiveRoster;
                ObserveField(screenshot, pixels!, boardMatch, fieldRound, rawItems, fieldWallet, shopOpenThisScan, frameTick, runRevision, connection, captureRevision, false);
                RecordFieldPurchases(previousRoster, LiveRoster, screenshot, fieldWallet?.Money);
            }
            else if (!shopOccluded && LiveReader != null && pixels != null && shopOpenThisScan && economy is { IsPreparation:true, Round:int shopRound })
            {
                bool updated = false;
                var previousRoster = LiveRoster;
                lock (_runLock)
                {
                    if (runRevision == _runRevision && IsCurrentCapture(connection,captureRevision))
                    {
                        _liveState ??= new(FieldTiers);
                        _liveState.Shop(pixels,rawItems,economy,true,frameTick);
                        updated = _liveState.PublishPurchases(shopRound);
                    }
                }
                if (updated)
                {
                    RecordFieldPurchases(previousRoster, LiveRoster, screenshot, economy.Money);
                    LiveFieldChanged?.Invoke();
                }
            }
            else if (!shopOccluded && !shopOpenThisScan)
                lock (_runLock) _liveState?.HideShop(frameTick);
            await ReadTerminalLevelAsync(screenshot, imgWidth, imgHeight, connection, captureRevision);
            if (!observedEnemyFrame && RoundEnemyReader is not null && (boardMatch is not null || CurrentEconomy?.IsPreparation == true
                || shopOpenThisScan || ProbeRoundEnemyPanel(screenshot)))
            {
                // Funds-only reads can still carry a previous frame's round and phase.
                var roundHud = forceFundsRead ? null : economy;
                if (!shopOpenThisScan && rawItems.Count == 0)
                    roundHud = await ReadEconomyAsync(screenshot, imgWidth, imgHeight, connection, runRevision, captureRevision, false, rawItems, hudOnly: true);
                await ObserveRoundEnemiesAsync(screenshot, roundHud, boardMatch is not null || shopOpenThisScan, connection, runRevision, captureRevision, frameTick, preview);
                observedEnemyFrame = true;
            }
        }
        catch (Exception ex)
        {
            // Swallow unexpected errors (e.g. capture failure during window state change)
            // so the timer-driven async void method never propagates an unhandled exception.
            diagnosticStatus = "识别发生异常";
            diagnosticError = ex.ToString();
            AppLogger.RecognitionFailure("OcrScanner", "scan", diagnosticError);
        }
        finally
        {
            lock (_runLock)
            {
                if (!observedEnemyFrame && (_roundEnemyState.AwaitingPanel || _roundEnemyState.OpenedPanel.HasValue))
                    _roundEnemyState.PauseConfirmationClock(captureLost: diagnosticFrame is null || _pauseBanScreenDetection || _banScreenActive);
                _purchaseShopOpen = shopOpenThisScan && !_pauseBanScreenDetection && IsCurrentCapture(connection, captureRevision);
                UpdateScanInterval();
            }
            if (diagnosticFrame is not null && IsCurrentCapture(connection, captureRevision))
                RecognitionDiagnostics.Record(capturedAt, connection?.IsAdb == true ? "ADB 商店识别" : "桌面商店识别", diagnosticFrame,
                    new { Status = diagnosticStatus, CaptureRevision = captureRevision, ElapsedMs = elapsed.Elapsed.TotalMilliseconds,
                        Theme = _shopReader.Theme.ToString(), Items = diagnosticItems, RawText = rawText,
                        Economy = CurrentEconomy, TerminalLevel = _terminalTracker.Level, Error = diagnosticError });
            Interlocked.Exchange(ref _scanning, 0);
        }
    }

    private static void RecordFieldPurchases(LiveRoster before, LiveRoster after, byte[] screenshot, int? money)
    {
        int previousCount = before.Round == after.Round ? before.Bought.Count : 0;
        if (after.Bought.Count <= previousCount) return;
        var names = string.Join("、", after.Bought.Skip(previousCount));
        var history = RecognitionDiagnostics.Samples;
        var id = history.Begin("实况购入 · " + names, after.Round, includeRecent: true, game: after.Observations.LastOrDefault()?.Game);
        history.Step(id, $"购入 {names} · 余额 {money}", screenshot);
    }

    private bool IsCurrentCapture(GameConnectionService? connection, long captureRevision)
        => ReferenceEquals(connection, _getConnection?.Invoke()) && (connection?.CaptureRevision ?? 0) == captureRevision;

    private async Task ReadRegulationLevelAsync(byte[] screenshot, int width, int height,
        GameConnectionService? connection, int revision, long captureRevision)
    {
        lock (_runLock)
        {
            if (revision != _runRevision || DateTime.UtcNow < _nextRegulationRead) return;
        }
        var level = await _regulationReader.ReadAsync(screenshot, width, height);
        lock (_runLock)
        {
            if (revision != _runRevision || _pauseBanScreenDetection
                || !IsCurrentCapture(connection, captureRevision)) return;
            _nextRegulationRead = DateTime.UtcNow.AddSeconds(3);
            if (level is null) return;
            _regulationLevel = level;
        }
        RegulationLevelChanged?.Invoke();
    }

    private void ReadGameTheme(byte[] screenshot)
    {
        if (!AutomaticGameTheme || ThemeDetector is null)
        {
            _themeCandidate = null;
            _themeConfirmations = 0;
            return;
        }
        var detected = ThemeDetector.Detect(screenshot);
        if (detected != _themeCandidate)
        {
            _themeCandidate = detected;
            _themeConfirmations = 1;
        }
        else if (_themeConfirmations < 2) _themeConfirmations++;
        if (!AutomaticGameTheme || detected is not { } theme || _themeConfirmations < 2 || theme == _shopReader.Theme) return;
        _shopReader.Theme = theme;
        _nextTerminalRead = DateTime.MinValue;
        AppLogger.Info("Theme", $"Detected game theme {theme}");
        GameThemeDetected?.Invoke(theme);
    }

    private async Task ReadTerminalLevelAsync(byte[] screenshot, int width, int height, GameConnectionService? connection, long captureRevision)
    {
        if (TerminalReader is null || DateTime.UtcNow < _nextTerminalRead) return;
        var level = await TerminalReader.ReadAsync(screenshot, width, height, ThemeProvider?.Invoke() ?? GameShopTheme.Terminal);
        if (!IsCurrentCapture(connection, captureRevision)) return;
        // Not visible (battle, menus): look again a little later. Confirming a change reads again soon.
        _nextTerminalRead = DateTime.UtcNow + (level is null ? TerminalReadInterval * 2 : TerminalReadInterval);
        if (level is not null && (level != _terminalTracker.Level || _terminalTracker.IsRestored))
            _nextTerminalRead = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        if (_terminalTracker.Observe(level))
        {
            AppLogger.Info("Terminal", $"Terminal level {_terminalTracker.Level}");
            _gameState.TerminalLevel = _terminalTracker.Level ?? 0;
            TerminalLevelChanged?.Invoke(_terminalTracker.Level!.Value);
        }
    }

    private async Task<ShopEconomy?> ReadEconomyAsync(byte[] screenshot, int width, int height, GameConnectionService? connection, int revision, long captureRevision, bool forceFundsRead, IReadOnlyList<ShopItem> cards, bool hudOnly = false)
    {
        if (EconomyReader is null) return null;
        bool calibrationDue;
        ShopEconomy? previous;
        lock (_runLock)
        {
            calibrationDue = DateTime.UtcNow >= _nextEconomyRead;
            if (revision != _runRevision || !forceFundsRead && !calibrationDue) return null;
            previous = forceFundsRead && !calibrationDue && _purchases.CanReuseHudLayout(cards, width, height)
                ? _purchaseEconomy : null;
        }
        var read = previous is not null
            ? previous with { Money = await EconomyReader.ReadMoneyAsync(screenshot, width, height) }
            : hudOnly ? await EconomyReader.ReadPreparationAsync(screenshot, width, height)
            : await EconomyReader.ReadAsync(screenshot, width, height);
        // A closed shop has no prices. Do not treat a false OCR round outside preparation as a new round.
        if (hudOnly && !read.IsPreparation) read = read with { Round = null, Mode = null };
        return PublishEconomyReading(read, connection, revision, captureRevision, fullHud: previous is null);
    }

    private ShopEconomy? PublishEconomyReading(ShopEconomy read, GameConnectionService? connection,
        int revision, long captureRevision, bool fullHud)
    {
        ShopEconomy? current;
        lock (_runLock)
        {
            if (revision != _runRevision || _pauseBanScreenDetection
                || !IsCurrentCapture(connection, captureRevision)) return null;
            if (fullHud) _nextEconomyRead = DateTime.UtcNow + EconomyReadInterval;
            if (!_economy.Observe(read)) return read;
            current = _economy.Current;
        }
        AppLogger.Info("Economy", $"{current}");
        EconomyChanged?.Invoke(current);
        return read;
    }

    /// <summary>New game (opening information recorded): forget the last game's HUD values.</summary>
    public void ResetEconomy()
    {
        lock (_runLock)
        {
            _runRevision++;
            RecognitionDiagnostics.Samples.ClearRecent();
            _economy.Reset();
            _liveState?.Clear();
            _reviewBoard = null;
            _fieldGame = Guid.NewGuid();
            _fieldObservations = [];
            _roundEnemyState.Reset();
            _bufferedPanel = null;
            _purchases.Reset("economy-reset");
            _purchaseEconomy = null;
            _nextEconomyRead = DateTime.MinValue;
        }
        LiveFieldChanged?.Invoke();
        EconomyChanged?.Invoke(null);
        RoundEnemiesChanged?.Invoke();
    }

    private void ObserveBoard(BoardMatch? match)
    {
        if (BoardRecognizer is null || !_board.Observe(match)) return;
        var current = _board.Current;
        AppLogger.Info("Board", current is null ? "Board lost"
            : $"Board {current.Map.Id} ({current.Camera}) score={current.Score:F1} margin={current.Margin:F1}");
        BoardChanged?.Invoke(current);
    }

    private void PublishShopItems(IReadOnlyList<ShopItem> items)
    {
        if (!_stabilizer.TryUpdate(items, out var stableItems))
            return;
        _gameState.ShopItems = stableItems.ToList();
        GameStateUpdated?.Invoke(_gameState);
    }

    /// <summary>Width and height of a captured frame (BMP or PNG).</summary>
    private static (int Width, int Height) GetPngDimensions(byte[] image) => ImageBytes.GetDimensions(image);

    /// <summary>Successful manual recording also consumes this opening screen.</summary>
    public void MarkInformationRecorded()
    {
        _startupOpportunityConsumed = true;
        _informationState.MarkRecorded();
    }

    public bool TryStartNewRun(bool bansChanged) => _informationState.TryStartNewRun(bansChanged);

    private async Task DetectBanScreenAsync(byte[] screenshot, int width, int height, GameConnectionService? connection, long captureRevision)
    {
        bool opening = await _informationDetector.IsOpeningScreenAsync(screenshot, width, height);
        bool canRearm = !opening && _informationState.IsConsumed
            && (await _informationDetector.IsGameplayAsync(screenshot, width, height)
                || await _informationDetector.IsRunLobbyAsync(screenshot, width, height));
        if (_pauseBanScreenDetection || !IsCurrentCapture(connection, captureRevision)) return;

        if (opening)
        {
            _startupOpportunityConsumed = true;
            _banScreenActive = true;
            _banScreenAbsentTicks = 0;
        }
        else if (_banScreenActive && ++_banScreenAbsentTicks >= BanLostThreshold)
        {
            _banScreenActive = false;
            BanScreenLost?.Invoke();
        }
        if (_informationState.Observe(opening, canRearm))
        {
            AppLogger.Info("BanScreen", "Validated opening info screen detected");
            BanScreenDetected?.Invoke();
        }
        if (canRearm && !_informationState.IsConsumed)
            AppLogger.Info("BanScreen", "Confirmed gameplay or run lobby; next automatic opening armed");
    }

    public void Dispose()
    {
        lock (_runLock)
        {
            _disposed = true;
            _scanTimer.Stop();
            _scanTimer.Dispose();
            _panelProbeTimer.Dispose();
            _bufferedPanel = null;
        }
    }
}
