using ASPAssistant.Core.Interop;
using System.Diagnostics;
using System.Drawing;

namespace ASPAssistant.Core.Services;

public sealed record RoundEnemyInfo(int Round, IReadOnlyList<string> Names)
{
    public IReadOnlyList<string> Bounties { get; init; } = [];
    public IReadOnlyList<string> Leaders { get; init; } = [];
    public IReadOnlyList<string> RouteNames => Leaders.Count == 0 ? Names : [.. Names, .. Leaders];
    public bool IsPartial { get; init; }
    public bool IsReading { get; init; }
    public string? Hint { get; init; }
    internal bool HasCompleteList => !IsPartial && (Names.Count > 0 || Leaders.Count > 0);
}

public partial class OcrScannerService
{
    private readonly RoundEnemyState _roundEnemyState = new();
    private readonly System.Timers.Timer _panelProbeTimer = new(300);
    private bool _probeBusy;
    private sealed record BufferedPanel(byte[] Bytes, long FrameTick, int Round, int Run, GameConnectionService Connection, long Capture);
    private BufferedPanel? _bufferedPanel;
    public Func<byte[]?>? CaptureEnemyPanelFrame { get; init; }

    internal void ProbeBusyRound()
    {
        GameConnectionService connection;
        int run, round;
        long capture;
        lock (_runLock)
        {
            if (_disposed || _scanning == 0 || _probeBusy || _pauseBanScreenDetection || _banScreenActive ||
                _economy.Current is not { IsPreparation: true, Round: int current } ||
                EconomyReader is null || RoundEnemyReader is null || CaptureEnemyPanelFrame is null ||
                _getConnection?.Invoke() is not { IsAdb: false } pc) return;
            round = current;
            run = _runRevision;
            connection = pc;
            capture = pc.CaptureRevision;
            if (_bufferedPanel is { } saved && saved.Run == run && saved.Round == round &&
                saved.Connection == connection && saved.Capture == capture) return;
            if (!_roundEnemyState.NeedsPanelFrame(round)) return;
            _probeBusy = true;
        }
        try
        {
            var frameTick = Environment.TickCount64;
            var bytes = CaptureEnemyPanelFrame();
            if (bytes is null) return;
            lock (_runLock)
            {
                if (_disposed || _pauseBanScreenDetection || _banScreenActive || _runRevision != run ||
                    !IsCurrentCapture(connection, capture) || _economy.Current is not { IsPreparation: true } hud ||
                    hud.Round != round || !_roundEnemyState.NeedsPanelFrame(round) ||
                    _roundEnemyState.Info?.IsReading == true && frameTick < _roundEnemyState.ManualStartTick) return;
                _bufferedPanel = new(bytes, frameTick, round, run, connection, capture);
            }
            AppLogger.Info("RoundEnemy", $"round={round} probe-frame=saved");
        }
        finally { lock (_runLock) _probeBusy = false; }
    }

    private async Task ReadBufferedEnemyPanelAsync(BufferedPanel saved, byte[] live, int width, int height,
        GameConnectionService? connection, int revision, long captureRevision)
    {
        try
        {
            lock (_runLock)
            {
                if (!ReferenceEquals(_bufferedPanel, saved) || saved.Run != revision || saved.Connection != connection ||
                    saved.Capture != captureRevision || _economy.Current?.Round != saved.Round ||
                    !_roundEnemyState.NeedsPanelFrame(saved.Round)) return;
            }
            using var stream = new MemoryStream(saved.Bytes);
            using var frame = new Bitmap(stream);
            var layout = RoundEnemyReader.Inspect(frame, locateButton: false);
            if (layout.Title.IsEmpty) return;
            var savedHud = await EconomyReader!.ReadPreparationAsync(saved.Bytes, frame.Width, frame.Height);
            if (!savedHud.IsPreparation || savedHud.Round != saved.Round) return;
            var liveHud = await EconomyReader.ReadPreparationAsync(live, width, height);
            if (!liveHud.IsPreparation || liveHud.Round != saved.Round) return;
            await ObserveRoundEnemiesAsync(saved.Bytes, savedHud, false, connection, revision, captureRevision,
                saved.FrameTick, (frame, layout), buffered: true);
        }
        finally
        {
            lock (_runLock)
                if (ReferenceEquals(_bufferedPanel, saved)) _bufferedPanel = null;
        }
    }

    private DateTime _nextRoundEnemyProbe;
    private volatile bool _automaticRoundEnemies;
    public bool AutomaticRoundEnemies { get => _automaticRoundEnemies; set => _automaticRoundEnemies = value; }
    public RoundEnemyReader? RoundEnemyReader { get; init; }
    internal Func<GameConnectionService, Point, Size, long, ClickResult> SendCapturedClick { get; init; } =
        static (connection, point, size, revision) => connection.ClickCapturedPoint(point, size.Width, size.Height, revision);
    internal Func<GameConnectionService, Point, Size, long, bool> RoundEnemyScroll { get; init; } =
        static (connection, point, size, revision) => connection.ScrollEnemyPanel(point, size, revision);
    public event Action? RoundEnemiesChanged;
    public RoundEnemyInfo? CurrentRoundEnemies { get { lock (_runLock) return _roundEnemyState.Info; } }

    public string? RequestRoundEnemyRead()
    {
        long request;
        lock (_runLock)
        {
            if (_disposed || _pauseBanScreenDetection || _banScreenActive ||
                _economy.Current is not { Round: int round, IsPreparation: true })
                return "请回到游戏整备阶段，等助手确认回合后再识别小怪。";
            _roundEnemyState.Observe(round);
            if (_roundEnemyState.Info?.Round != round) return "正在确认当前回合，请稍后重试。";
            if (_roundEnemyState.Info.HasCompleteList) return $"第 {round} 回合的名单已识别。";
            if (!_roundEnemyState.RequestManual(DateTime.UtcNow)) return null;
            request = _roundEnemyState.ManualVersion;
            _bufferedPanel = null;
        }
        _ = ExpireAfterDeadlineAsync(request);
        RoundEnemiesChanged?.Invoke();
        return null;
    }

    private async Task ExpireAfterDeadlineAsync(long request)
    {
        await Task.Delay(ManualRecognitionLimit).ConfigureAwait(false);
        lock (_runLock)
        {
            if (_disposed || request != _roundEnemyState.ManualVersion || !ExpireManualRead(DateTime.MaxValue)) return;
        }
        RoundEnemiesChanged?.Invoke();
    }

    private void ExpireRoundEnemyRead()
    {
        bool expired;
        lock (_runLock) expired = ExpireManualRead(DateTime.UtcNow);
        if (expired) RoundEnemiesChanged?.Invoke();
    }

    private bool ExpireManualRead(DateTime now)
    {
        if (!_roundEnemyState.ExpireManual(now)) return false;
        var info = _roundEnemyState.Info!;
        var samples = RecognitionDiagnostics.Samples;
        var id = samples.Begin("小怪识别超时", info.Round, includeRecent: true);
        samples.Step(id, $"8 秒截止 · 已读到 {string.Join("、", info.RouteNames)}");
        return true;
    }

    // A player can open the enemy panel before the shop or the player's board has been recognised.
    private bool ProbeRoundEnemyPanel(byte[] screenshot)
    {
        var now = DateTime.UtcNow;
        if (now < _nextRoundEnemyProbe) return false;
        _nextRoundEnemyProbe = now.AddSeconds(1);
        using var stream = new MemoryStream(screenshot);
        using var frame = new Bitmap(stream);
        return !RoundEnemyReader.Inspect(frame, locateButton: false).Title.IsEmpty;
    }

    internal async Task ObserveRoundEnemiesAsync(byte[] screenshot, ShopEconomy? hud, bool sceneRecognized,
        GameConnectionService? connection, int revision, long captureRevision, long frameTick, (Bitmap Frame, RoundEnemyReader.Layout Layout)? preview = null, bool buffered = false)
    {
        if (EconomyReader is not { } economyReader || RoundEnemyReader is not { } reader) return;
        bool newRound, manual, mayOpen, autoRequested, retryPending, confirming;
        int round;
        long version;
        bool manualRequest;
        lock (_runLock)
        {
            if (revision != _runRevision || _pauseBanScreenDetection || !IsCurrentCapture(connection, captureRevision)
                || _economy.Current is not { Round: int confirmedRound, IsPreparation: true }) return;
            round = confirmedRound;
            newRound = _roundEnemyState.Observe(round);
            if (_roundEnemyState.Info?.Round != round || _roundEnemyState.Info.HasCompleteList) return;
            manual = _roundEnemyState.Info.IsReading;
            manualRequest = manual || _roundEnemyState.ManualContinuation;
            version = _roundEnemyState.ManualVersion;
            if (!RequestCurrent()) return;
            if (manual && frameTick < _roundEnemyState.ManualStartTick) return;
            if (_roundEnemyState.EmptyReadTick is long emptyTick)
            {
                if (!manual && !AutomaticRoundEnemies)
                {
                    _roundEnemyState.EndConfirmation();
                    return;
                }
                if (buffered || frameTick <= emptyTick) return;
            }
            confirming = _roundEnemyState.AwaitingPanel;
            retryPending = !manual && _roundEnemyState.HasUnsentRetry;
            autoRequested = AutomaticRoundEnemies && !_roundEnemyState.AutoAttempted;
            bool verifiedHud = hud is { IsPreparation: true } && hud.Round == round;
            mayOpen = connection is not null && (manual || !_roundEnemyState.Info.IsPartial) &&
                _roundEnemyState.CanOpen(AutomaticRoundEnemies, sceneRecognized || !manual && verifiedHud, DateTime.UtcNow);
        }
        bool RequestCurrent() => version == _roundEnemyState.ManualVersion &&
            (!manualRequest || _roundEnemyState.WithinManualDeadline);
        if (newRound) RoundEnemiesChanged?.Invoke();

        using var stream = preview is null ? new MemoryStream(screenshot) : null;
        using var decoded = stream is null ? null : new Bitmap(stream);
        var frame = preview?.Frame ?? decoded!;
        var layout = preview?.Layout ?? RoundEnemyReader.Inspect(frame, mayOpen || confirming);
        if (layout.Title.IsEmpty)
        {
            bool cancelledRead;
            lock (_runLock)
            {
                if (!RequestCurrent()) return;
                _roundEnemyState.PanelClosed();
                cancelledRead = _roundEnemyState.EmptyReadTick.HasValue;
                if (cancelledRead)
                {
                    _roundEnemyState.EndConfirmation();
                    if (manual) _roundEnemyState.Fail("面板已关闭，可点击「识别小怪」重试。");
                    AppLogger.Info("RoundEnemy", $"round={round} empty-read-retry=cancelled-panel-closed");
                }
                else if (confirming)
                {
                    if (!sceneRecognized || layout.Button is null || hud?.Round is int seen && (seen != round || !hud.IsPreparation))
                    {
                        _roundEnemyState.PauseConfirmationClock();
                        return;
                    }
                    if (!_roundEnemyState.ClosedLongEnough(frameTick)) return;
                }
            }
            if (cancelledRead)
            {
                if (manual) RoundEnemiesChanged?.Invoke();
                return;
            }
            if ((!mayOpen && !confirming) || layout.Button is not { } button || connection is null)
            {
                if (!manual && autoRequested)
                    LogRoundEnemyAutoSkip(round, mayOpen ? "button-not-found" : "scene-not-confirmed");
                return;
            }
            if (retryPending && !connection.CanRetryCapturedPoint(button, frame.Width, frame.Height)) return;
            // Only a HUD reading from this exact frame can authorize a click.
            if (hud?.Round is null) hud = await economyReader.ReadPreparationAsync(screenshot, frame.Width, frame.Height);
            if (!hud.IsPreparation || hud.Round != round)
            {
                lock (_runLock) { if (!RequestCurrent()) return; _roundEnemyState.PauseConfirmationClock(); }
                if (!manual) LogRoundEnemyAutoSkip(round, "round-not-confirmed-on-frame");
                return;
            }
            bool exhausted = false;
            lock (_runLock)
            {
                if (!RequestCurrent() || revision != _runRevision || _pauseBanScreenDetection || !IsCurrentCapture(connection, captureRevision)
                    || _roundEnemyState.Info?.Round != round || _roundEnemyState.Info.IsReading != manual
                    || _bufferedPanel is { } saved && saved.Round == round && saved.Run == revision &&
                        saved.Connection == connection && saved.Capture == captureRevision) return;
                if (confirming)
                {
                    if (!_roundEnemyState.AwaitingPanel) return;
                    _roundEnemyState.EndConfirmation();
                    if (!manual && !AutomaticRoundEnemies) return;
                    if (!manual && !_roundEnemyState.TryRepeatSentClick())
                    {
                        _roundEnemyState.Fail("未确认敌人面板已打开，可点击「识别小怪」重试。");
                        exhausted = true;
                    }
                }
                if (manual && !_roundEnemyState.TryManualOpen()) return;
                if (!manual && !confirming && (!AutomaticRoundEnemies || !_roundEnemyState.TryAutoOpen())) return;
            }
            if (exhausted)
            {
                AppLogger.Info("RoundEnemy", $"round={round} auto-panel=not-confirmed after-retry");
                RoundEnemiesChanged?.Invoke();
                return;
            }
            var timer = Stopwatch.StartNew();
            lock (_runLock) { if (!RequestCurrent()) return; }
            var result = SendCapturedClick(connection, button, frame.Size, captureRevision);
            bool reportFailure = false;
            lock (_runLock)
            {
                if (!RequestCurrent() || revision != _runRevision || _roundEnemyState.Info?.Round != round) return;
                if (result == ClickResult.Sent && !manual) _roundEnemyState.BeginConfirmation();
                if (result != ClickResult.Sent)
                {
                    bool retry = !manual && !confirming && result is not (ClickResult.ReleaseUnconfirmed or ClickResult.AdbUnconfirmed)
                        && _roundEnemyState.RetryUnsent(DateTime.UtcNow);
                    _roundEnemyState.Fail(retry ? "等待游戏窗口可点击后重试。" : "未能打开面板。请在游戏里打开敌人面板后重试。");
                    reportFailure = true;
                }
            }
            if (reportFailure)
            {
                var failed = RecognitionDiagnostics.Samples.Begin("小怪面板打开失败", round);
                RecognitionDiagnostics.Samples.Step(failed, $"点击 {button} · {result}", screenshot);
                RoundEnemiesChanged?.Invoke();
            }
            AppLogger.Info("RoundEnemy", $"round={round} {(manual ? "manual" : confirming ? "auto-retry" : "auto")}-input={result} point={button.X},{button.Y} elapsed={timer.ElapsedMilliseconds}ms");
            return;
        }

        RoundEnemyReadResult? previousPage;
        var stamp = RoundEnemyReader.PageStamp(frame, layout);
        lock (_runLock)
        {
            if (_roundEnemyState.PanelRead && !_roundEnemyState.EmptyReadTick.HasValue &&
                (buffered || frameTick <= _roundEnemyState.LastReadTick || _roundEnemyState.LastStamp == stamp)) return;
            previousPage = _roundEnemyState.CoveredPage;
        }
        if (hud?.Round is null)
            hud = await economyReader.ReadPreparationAsync(screenshot, frame.Width, frame.Height);
        if (!hud.IsPreparation || hud.Round != round) return;
        bool retrying;
        lock (_runLock)
        {
            if (revision != _runRevision || _pauseBanScreenDetection || !IsCurrentCapture(connection, captureRevision)) return;
            if (!RequestCurrent()) return;
            retrying = _roundEnemyState.EmptyReadTick.HasValue;
            if (retrying) _roundEnemyState.EndConfirmation();
            _roundEnemyState.PanelRead = true;
            _roundEnemyState.LastStamp = stamp;
            _roundEnemyState.LastReadTick = frameTick;
        }
        var samples = RecognitionDiagnostics.Samples;
        var sampleId = samples.Begin("小怪名单", round, includeRecent: true);
        samples.Step(sampleId, "敌人面板", screenshot);
        var readTimer = Stopwatch.StartNew();
        var mode = hud.Mode ?? _economy.Current?.Mode;
        bool StillRequested() { lock (_runLock) return RequestCurrent(); }
        var read = await reader.ReadAsync(screenshot, frame, layout, previousPage, mode, round, manualRequest ? StillRequested : null);
        int scrolls = 0, captures = 0, pageReads = 1, lineRetries = read?.Retries ?? 0;
        bool complete, returnToBoard;
        lock (_runLock)
        {
            if (revision != _runRevision || _pauseBanScreenDetection || !IsCurrentCapture(connection, captureRevision)
                || _roundEnemyState.Info?.Round != round) return;
            if (!RequestCurrent()) { samples.Step(sampleId, "请求已结束，忽略迟到的名单"); return; }
            manual = _roundEnemyState.Info.IsReading || _roundEnemyState.ManualContinuation;
            returnToBoard = manual || retrying || _roundEnemyState.CanClosePanel;
            if (read is not { HasNames: true } && !retrying && (manual || AutomaticRoundEnemies && _roundEnemyState.CanClosePanel))
            {
                // Retain ownership until a subsequent main scan sees the panel or the player leaves it.
                samples.Step(sampleId, "未读到名单，等待下一帧");
                _roundEnemyState.EmptyReadTick = Environment.TickCount64;
                AppLogger.Info("RoundEnemy", $"round={round} empty-read-retry=scheduled elapsed={readTimer.ElapsedMilliseconds}ms");
                return;
            }
            if (read is { HasNames: true }) _roundEnemyState.ApplyRead(read);
            else _roundEnemyState.Fail("未读到小怪，可以点击「识别小怪」重试。");
            complete = _roundEnemyState.Info.HasCompleteList;
            _roundEnemyState.EndConfirmation();
            if (returnToBoard && read is { HasNames: true } && !complete)
                _roundEnemyState.ContinuePartial(manual);
        }
        RoundEnemiesChanged?.Invoke();
        if (!complete && returnToBoard && connection is not null && read is { HasNames: true })
        {
            for (int page = 0; page < 5 && !read.EndsAtBottom; page++)
            {
                if (!CurrentPanelRead() || _captureScreen() is not { } before) break;
                captures++;
                using var beforeStream = new MemoryStream(before);
                using var beforeFrame = new Bitmap(beforeStream);
                var beforeLayout = RoundEnemyReader.Inspect(beforeFrame, false);
                if (beforeLayout.Title.IsEmpty || RoundEnemyReader.PageStamp(beforeFrame, beforeLayout) != stamp) break;
                var visibleHud = await economyReader.ReadPreparationAsync(before, beforeFrame.Width, beforeFrame.Height);
                if (!visibleHud.IsPreparation || visibleHud.Round != round || !CurrentPanelRead()) break;
                var target = new Point(beforeLayout.Names.Left + beforeLayout.Names.Width / 2,
                    beforeLayout.Names.Top + beforeLayout.Names.Height * 2 / 3);
                if (!RoundEnemyScroll(connection, target, beforeFrame.Size, captureRevision)) break;
                scrolls++;
                ulong? moving = null;
                RoundEnemyReadResult? next = null;
                for (int sample = 0; sample < 4; sample++)
                {
                    await Task.Delay(120);
                    if (!CurrentPanelRead() || _captureScreen() is not { } fresh) break;
                    captures++;
                    using var freshStream = new MemoryStream(fresh);
                    using var freshFrame = new Bitmap(freshStream);
                    var freshLayout = RoundEnemyReader.Inspect(freshFrame, false);
                    if (freshLayout.Title.IsEmpty) break;
                    var freshStamp = RoundEnemyReader.PageStamp(freshFrame, freshLayout);
                    if (freshStamp == stamp || moving != freshStamp) { moving = freshStamp; continue; }
                    var freshHud = await economyReader.ReadPreparationAsync(fresh, freshFrame.Width, freshFrame.Height);
                    if (!freshHud.IsPreparation || freshHud.Round != round || !CurrentPanelRead()) break;
                    next = await reader.ReadAsync(fresh, freshFrame, freshLayout, read, mode, round, manualRequest ? StillRequested : null);
                    samples.Step(sampleId, $"滚动后第 {pageReads+1} 页", fresh);
                    pageReads++;
                    lineRetries += next?.Retries ?? 0;
                    stamp = freshStamp;
                    break;
                }
                if (next is not { HasNames: true }) break;
                lock (_runLock)
                {
                    if (!CurrentPanelRead()) return;
                    _roundEnemyState.LastStamp = stamp;
                    _roundEnemyState.LastReadTick = Environment.TickCount64;
                    _roundEnemyState.ApplyRead(next);
                    complete = _roundEnemyState.Info!.HasCompleteList;
                }
                RoundEnemiesChanged?.Invoke();
                read = next;
                if (complete) break;
            }
        }
        bool CurrentPanelRead()
        {
            lock (_runLock) return RequestCurrent() && !_disposed && revision == _runRevision && !_pauseBanScreenDetection && !_banScreenActive
                && IsCurrentCapture(connection, captureRevision) && _roundEnemyState.Info?.Round == round
                && _economy.Current is { IsPreparation: true } current && current.Round == round
                && (manual || AutomaticRoundEnemies);
        }
        AppLogger.Info("RoundEnemy", $"round={round} source={(buffered ? "buffered-panel" : manual ? "manual" : returnToBoard ? "automatic" : "visible-panel")} names={read?.Names.Length ?? 0} bounties={read?.Bounties.Length ?? 0} complete={complete} empty-read-retry={retrying} pages={pageReads} scrolls={scrolls} scrollCaptures={captures} retries={lineRetries} elapsed={readTimer.ElapsedMilliseconds}ms");
        var info = CurrentRoundEnemies;
        string summary = $"名单{(complete ? "完整" : "待补全")} · {string.Join("、", info?.RouteNames ?? [])} · 悬赏 {string.Join("、", info?.Bounties ?? [])}";
        samples.Step(sampleId, summary);
        if (!returnToBoard || !CurrentPanelRead() || !manual && read is not { HasNames: true }) return;
        var returnTimer = Stopwatch.StartNew();
        if (connection is null || !IsCurrentCapture(connection, captureRevision) || _captureScreen() is not { } latest)
        {
            samples.Step(sampleId, summary + " · 返回前未取得画面");
            AppLogger.Info("RoundEnemy", $"round={round} return-skipped=capture-unavailable");
            return;
        }
        using var closeStream = new MemoryStream(latest);
        using var closeFrame = new Bitmap(closeStream);
        var closeLayout = RoundEnemyReader.Inspect(closeFrame, locateButton: false);
        if (closeLayout.Title.IsEmpty || complete && (closeLayout.MoreBelow || closeLayout.BottomUnknown) ||
            !complete && closeFrame.Size == frame.Size && RoundEnemyReader.PageStamp(closeFrame, closeLayout) != stamp)
        {
            samples.Step(sampleId, summary + " · 返回前面板发生变化", latest);
            AppLogger.Info("RoundEnemy", $"round={round} return-skipped=panel-no-longer-visible");
            return;
        }
        var closingHud = await economyReader.ReadPreparationAsync(latest, closeFrame.Width, closeFrame.Height);
        lock (_runLock)
        {
            if (!RequestCurrent() || _disposed || revision != _runRevision || _pauseBanScreenDetection || _banScreenActive ||
                !IsCurrentCapture(connection, captureRevision) || _roundEnemyState.Info?.Round != round ||
                !closingHud.IsPreparation || closingHud.Round != round)
            {
                samples.Step(sampleId, summary + " · 返回前回合或请求已变化", latest);
                AppLogger.Info("RoundEnemy", $"round={round} return-skipped=context-changed-or-round-unconfirmed");
                return;
            }
        }
        var center = new Point(closeFrame.Width / 2, closeFrame.Height / 2);
        var closed = SendCapturedClick(connection, center, closeFrame.Size, captureRevision);
        AppLogger.Info("RoundEnemy", $"round={round} auto-close-input={closed} point={center.X},{center.Y} elapsed={returnTimer.ElapsedMilliseconds}ms");
        samples.Step(sampleId, $"{summary} · 返回点击 {center} · {closed}", latest);
        if (closed != ClickResult.Sent) return;
        await Task.Delay(150);
        if (!CurrentPanelRead() || _captureScreen() is not { } after) return;
        using var afterStream = new MemoryStream(after);
        using var afterFrame = new Bitmap(afterStream);
        var returned = RoundEnemyReader.Inspect(afterFrame, locateButton: true);
        bool boardVisible = returned.Title.IsEmpty && returned.Button.HasValue;
        AppLogger.Info("RoundEnemy", $"round={round} returned-to-board={boardVisible}");
        samples.Step(sampleId, summary + (boardVisible ? " · 已返回棋盘" : " · 未确认返回棋盘"), after);
        if (boardVisible && !complete)
        {
            lock (_runLock)
            {
                if (!CurrentPanelRead() || _roundEnemyState.Info?.IsPartial != true) return;
                _roundEnemyState.MarkPartialReturned();
            }
            RoundEnemiesChanged?.Invoke();
        }
    }

    private void LogRoundEnemyAutoSkip(int round, string reason)
    {
        lock (_runLock)
        {
            if (_roundEnemyState.Info?.Round != round || _roundEnemyState.AutoSkipLogged) return;
            _roundEnemyState.AutoSkipLogged = true;
        }
        AppLogger.Info("RoundEnemy", $"round={round} auto-open-deferred reason={reason}");
    }
}

internal sealed class RoundEnemyState
{
    internal RoundEnemyInfo? Info { get; private set; }
    internal bool AutoAttempted { get; private set; }
    internal bool AutoSkipLogged { get; set; }
    private bool _manualOpenAttempted;
    internal bool PanelRead { get; set; }
    internal RoundEnemyReadResult? CoveredPage => _coveredPage;
    internal ulong LastStamp { get; set; }
    internal long LastReadTick { get; set; }
    internal bool AwaitingPanel { get; private set; }
    internal long? EmptyReadTick { get; set; }
    internal bool CanClosePanel { get; private set; }
    internal bool ManualContinuation { get; private set; }
    private bool _repeatedSentClick;
    private long? _lastClosedTick;
    private long _closedMilliseconds;
    internal long ManualStartTick { get; private set; }
    internal long ManualVersion { get; private set; }
    internal bool WithinManualDeadline => DateTime.UtcNow < _manualUntil;
    private DateTime _manualUntil;
    private bool _coveredFromTop;
    private RoundEnemyReadResult? _coveredPage;

    internal bool NeedsPanelFrame(int round) => Info is not { } info || info.Round != round ||
        (!info.HasCompleteList && (!info.IsPartial || info.IsReading) && !PanelRead);

    internal bool Observe(int round)
    {
        if (Info is { } previous && previous.Round >= round) return false;
        Reset();
        Info = new(round, []);
        return true;
    }

    internal bool TryAutoOpen()
    {
        if (AutoAttempted) return false;
        AutoAttempted = true;
        return true;
    }

    private int _autoRetries;
    internal bool HasUnsentRetry => _autoRetries > 0 && !AutoAttempted;
    private DateTime _retryAfter;
    internal bool RetryUnsent(DateTime now)
    {
        if (_autoRetries >= 2) return false;
        _retryAfter = now.AddSeconds(++_autoRetries * 2);
        AutoAttempted = false;
        return true;
    }

    internal bool CanOpen(bool automatic, bool sceneRecognized, DateTime now) => !AwaitingPanel && sceneRecognized &&
        (Info?.IsReading == true ? !_manualOpenAttempted : automatic && !AutoAttempted && now >= _retryAfter);

    internal bool RequestManual(DateTime now)
    {
        if (Info is not { IsReading: false, HasCompleteList: false } info) return false;
        if (EmptyReadTick.HasValue) EndConfirmation();
        PanelRead = _manualOpenAttempted = false;
        ManualStartTick = Environment.TickCount64;
        ManualVersion++;
        _manualUntil = now + OcrScannerService.ManualRecognitionLimit;
        Info = info with { IsReading = true, Hint = "正在识别小怪，请保持游戏窗口可见。" };
        return true;
    }

    internal bool TryManualOpen()
    {
        if (Info?.IsReading != true || _manualOpenAttempted) return false;
        _manualOpenAttempted = AutoAttempted = true;
        return true;
    }

    internal bool ExpireManual(DateTime now)
    {
        if (Info?.IsReading != true && !ManualContinuation || now < _manualUntil) return false;
        ManualVersion++;
        AutoAttempted = true;
        EndConfirmation();
        Fail("识别超时（8 秒），可重新识别。");
        return true;
    }

    internal void BeginConfirmation()
    {
        AwaitingPanel = CanClosePanel = true;
        _lastClosedTick = null;
        _closedMilliseconds = 0;
    }

    internal void PauseConfirmationClock(bool captureLost = false)
    {
        // The player may have opened the panel while capture was unavailable.
        if (captureLost)
        {
            CanClosePanel = ManualContinuation = false;
            if (EmptyReadTick.HasValue) EndConfirmation();
        }
        _lastClosedTick = null;
    }

    internal bool ClosedLongEnough(long frameTick)
    {
        var previous = _lastClosedTick;
        _lastClosedTick = frameTick;
        if (previous is null) return false;
        _closedMilliseconds += frameTick - previous.Value;
        return _closedMilliseconds >= 4000;
    }

    internal void EndConfirmation()
    {
        EmptyReadTick = null;
        AwaitingPanel = CanClosePanel = ManualContinuation = false;
    }

    internal void ContinuePartial(bool manual)
    {
        CanClosePanel = true;
        ManualContinuation = manual;
    }

    internal bool TryRepeatSentClick()
    {
        if (_repeatedSentClick) return false;
        _repeatedSentClick = true;
        return true;
    }

    internal void Fail(string hint) => Info = Info! with { IsReading = false, Hint = hint };
    internal void MarkPartialReturned()
    {
        EndConfirmation();
        Info = Info! with { Hint = "已返回棋盘，名单待补全，可点击「识别小怪」重试。" };
    }
    internal void PanelClosed()
    {
        PanelRead = false;
        if (!AwaitingPanel) CanClosePanel = ManualContinuation = false;
    }
    internal void ApplyRead(RoundEnemyReadResult read)
    {
        var previous = Info!;
        bool overlaps = _coveredPage is { } page && (read.Names.Intersect(page.Names, StringComparer.Ordinal).Any() ||
            read.Bounties.Intersect(page.Bounties, StringComparer.Ordinal).Any() || read.Leaders.Intersect(page.Leaders).Any());
        // A partly clipped final row is resolved when the next page includes its preceding name.
        bool connected = read.StartsAtTop || _coveredFromTop && overlaps &&
            (_coveredPage?.UnreadTailAnchor is not { } anchor ||
                Array.IndexOf(read.Names, anchor) is var at && at >= 0 && at + 1 < read.Names.Length);
        _coveredFromTop = connected && (!read.HasUnreadRows || read.UnreadTailAnchor is not null);
        _coveredPage = read;
        bool complete = _coveredFromTop && !read.HasUnreadRows && read.EndsAtBottom &&
            (read.Names.Length > 0 || read.Leaders.Length > 0 || !read.StartsAtTop && (previous.Names.Count > 0 || previous.Leaders.Count > 0));
        bool replace = complete && read.StartsAtTop;
        var names = replace ? read.Names : previous.Names.Union(read.Names, StringComparer.Ordinal).ToArray();
        var bounties = replace ? read.Bounties : previous.Bounties.Union(read.Bounties, StringComparer.Ordinal).ToArray();
        Info = new(previous.Round, Array.AsReadOnly(names))
        {
            Bounties = Array.AsReadOnly(bounties),
            Leaders = replace ? read.Leaders : previous.Leaders.Union(read.Leaders, StringComparer.Ordinal).ToArray(),
            IsPartial = !complete,
            Hint = complete ? null : read.HasUnreadRows ? "有名称待确认，可点击「识别小怪」重试。"
                : !read.EndsAtBottom ? "尚未确认到底，向下滚动可继续补录。"
                : names.Count() == 0 && read.Leaders.Length == 0 && bounties.Count() > 0 ? "已读到悬赏，普通敌人名单待确认。"
                : "从面板顶部开始识别，滚动时保留上一屏的一项。"
        };
    }

    internal void Reset()
    {
        Info = null;
        AutoAttempted = AutoSkipLogged = _manualOpenAttempted = PanelRead = false;
        _coveredFromTop = false;
        _coveredPage = null;
        _autoRetries = 0;
        EndConfirmation();
        _repeatedSentClick = false;
        ManualVersion++;
        _manualUntil = _retryAfter = default;
    }
}
