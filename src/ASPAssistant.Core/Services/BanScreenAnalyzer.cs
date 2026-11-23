// Modified for ASP Assistant Regulation on 2026-11-09; see NOTICE.
using ASPAssistant.Core.GameModes;
using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Services;

/// <summary>
/// Represents the ban state of a single covenant.
/// </summary>
public record CovenantBanState(string Name, bool IsBanned, double X);

/// <summary>
/// Result produced by one full ban-screen analysis pass.
/// </summary>
public record BanScreenResult(
    IReadOnlyList<CovenantBanState> CoreCovenants,
    IReadOnlyList<CovenantBanState> AdditionalCovenants,
    string? EnemyFaction,
    IReadOnlySet<string> MissingCovenants);

/// <summary>
/// Orchestrates the ban-screen analysis: one screenshot of the "确认本局信息" screen,
/// enemy recognition plus <see cref="BanCovenantReader"/> for both covenant panels.
///
/// Both opening and in-game info pages are supported. Incomplete covenant scans never
/// replace state; complete covenants may publish independently of enemy recognition.
/// </summary>
public sealed class BanScreenAnalyzer
{
    private readonly Func<Task<byte[]?>> _captureInformationScreen;
    private readonly IOcrEngine _ocr;
    private readonly IOcrStrategy _ocrStrategy;
    private readonly EnemyRecognizer _enemyRecognizer;
    private readonly InformationScreenDetector _informationDetector;
    private readonly IReadOnlyList<Operator> _operators;
    private readonly IReadOnlyList<string> _coreCovenantNames;
    private readonly IReadOnlyList<string> _additionalCovenantNames;

    /// <summary>
    /// Raised when a full analysis pass completes successfully.
    /// </summary>
    public event Action<BanScreenResult>? AnalysisCompleted;
    public event Action<string>? AnalysisFailed;
    /// <summary>Awaits progress preparation for a validated opening or a changed complete Ban set.</summary>
    public Func<bool, Task>? PrepareNewRunAsync { get; set; }
    public Func<Task<byte[]?>>? ScrollInformationPanelAsync { get; set; }
    public Func<long> CaptureRevisionProvider { get; set; } = () => 0;
    public event Action<string>? ScrollRequired;
    private bool _scrollReminderShown;

    /// <summary>
    /// Raised when complete enemy information is available, before covenant publication.
    /// </summary>
    public event Action<EnemyResult>? EnemyDetected;

    /// <summary>
    /// Raised when ban-screen analysis starts/ends. true = busy, false = idle.
    /// </summary>
    public event Action<bool>? AnalysisBusyChanged;

    /// <summary>
    /// Raised when auto-ban calculation for the current run completes.
    /// </summary>
    public event Action<IReadOnlyList<string>>? AutoBanListComputed;

    /// <summary>
    /// Raised when a full-screen game-window announcement should be shown/hidden.
    /// </summary>
    public event Action<string>? ShowGameOverlayMessageRequested;
    public event Action? HideGameOverlayMessageRequested;

    // Re-entry guard: only one analysis can run at a time.
    private int _running;

    // 同一张截图重复点击时直接复用上次结果（截图字节哈希相等即视为同一画面）。
    private string? _lastShotHash;
    private BanScreenResult? _lastResult;
    private IReadOnlyList<string>? _lastAutoBans;
    private EnemyResult? _lastEnemy;
    private long _lastCaptureRevision;

    public BanScreenAnalyzer(
        Func<Task<byte[]?>> captureInformationScreen,
        IOcrEngine ocr,
        IOcrStrategy ocrStrategy,
        EnemyRecognizer enemyRecognizer,
        IReadOnlyList<Operator> operators,
        IReadOnlyList<string> coreCovenantNames,
        IReadOnlyList<string> additionalCovenantNames)
    {
        _captureInformationScreen = captureInformationScreen;
        _ocr = ocr;
        _ocrStrategy = ocrStrategy;
        _enemyRecognizer = enemyRecognizer;
        _informationDetector = new InformationScreenDetector(ocr, ocrStrategy);
        _operators = operators;
        _coreCovenantNames = coreCovenantNames;
        _additionalCovenantNames = additionalCovenantNames;
    }

    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Runs the full ban-screen analysis asynchronously.
    /// Silently returns if another analysis is already in progress.
    /// </summary>
    public async Task<bool> RunAnalysisAsync(bool showGameResult = true)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            AppLogger.Warn("BanAnalyzer", "Analysis already running, skipping");
            return false;
        }

        AnalysisBusyChanged?.Invoke(true);
        string? scrollMessage = null;
        byte[]? diagnosticFrame = null;
        var capturedAt = DateTimeOffset.Now;
        var diagnosticRevision = CaptureRevisionProvider();
        object diagnosticResult = new { Status = "分析未完成；请结合日志查看具体原因" };
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            AppLogger.Info("BanAnalyzer", "Starting ban-screen analysis");
            AppConsole.WriteLine("[BanAnalyzer] 截图...");
            var shot = await _captureInformationScreen();
            var captureRevision = CaptureRevisionProvider();
            if (shot == null)
            {
                AppLogger.Warn("BanAnalyzer", "Screenshot failed");
                AnalysisFailed?.Invoke("未获取到可用的游戏画面，请将游戏置前并打开本局信息页后重试。");
                return false;
            }

            var (rawWidth, rawHeight) = GetPngDimensions(shot);
            var shotHash = HashShot(shot);
            if (_lastResult is not null && shotHash == _lastShotHash && captureRevision == _lastCaptureRevision)
            {
                AppLogger.Info("BanAnalyzer", "Identical screenshot, reusing the previous analysis");
                if (PrepareNewRunAsync is { } cachedPrepare && await _informationDetector.IsOpeningScreenAsync(shot, rawWidth, rawHeight))
                {
                    if (captureRevision != CaptureRevisionProvider()) return false;
                    await cachedPrepare(false);
                }
                if (captureRevision != CaptureRevisionProvider()) return false;
                AutoBanListComputed?.Invoke(_lastAutoBans ?? []);
                if (_lastEnemy is not null) EnemyDetected?.Invoke(_lastEnemy);
                AnalysisCompleted?.Invoke(_lastResult);
                return _lastEnemy is not null;
            }

            diagnosticFrame = ImageBytes.ToPng(shot);
            AppLogger.SaveArtifact("ban-attempt", diagnosticFrame, ".png");
            AppLogger.Info("BanAnalyzer", $"Screenshot ready: {rawWidth}x{rawHeight}");

            // 小图（例如被缩放过的录像截图）先把文字放大到 OCR 能读的尺寸；
            // 区域都是百分比，布局不受影响，后续全部使用放大后的尺寸。
            (shot, var imgWidth, var imgHeight) = ScreenshotNormalizer.EnsureAnalysable(shot, rawWidth, rawHeight);
            if (imgWidth != rawWidth)
                AppConsole.WriteLine($"[BanAnalyzer] 截图 {rawWidth}×{rawHeight} → 放大到 {imgWidth}×{imgHeight}");
            AppConsole.WriteLine($"[BanAnalyzer] 截图完成 ({imgWidth}×{imgHeight})");

            BanCovenantReadResult read;
            BanCovenantReadResult? completeCore = null;
            for (var scrolls = 0; ; scrolls++)
            {
                read = await BanCovenantReader.ReadAsync(_ocr, _ocrStrategy, shot, imgWidth, imgHeight,
                    _coreCovenantNames, _additionalCovenantNames);
                if (captureRevision != CaptureRevisionProvider()) return false;
                if (completeCore is null && read.SawCoreHeader && !read.HasIncompleteCoreRows
                    && read.UnnamedCore.Count == 0 && read.Core.Count >= 6
                    && (read.CoreSlots == 0 || read.Core.Count == read.CoreSlots)
                    && read.Core.Select(c => c.Name).Distinct().Count() == read.Core.Count)
                    completeCore = read;
                if (completeCore is not null)
                {
                    if (read.Core.Any(c => completeCore.Core.Any(previous => previous.Name == c.Name && previous.IsBanned != c.IsBanned))
                        || read.Additional.Any(c => completeCore.Additional.Any(previous => previous.Name == c.Name && previous.IsBanned != c.IsBanned)))
                    {
                        scrollMessage = null;
                        AnalysisFailed?.Invoke("滚动期间盟约选择发生变化，未替换现有结果；请保持选择不变后重新录入。");
                        return false;
                    }
                    read = read with
                    {
                        Core = completeCore.Core,
                        CoreSlots = completeCore.CoreSlots,
                        UnnamedCore = completeCore.UnnamedCore,
                        SawCoreHeader = true,
                        HasIncompleteCoreRows = false
                    };
                }
                AppLogger.Info("BanAnalyzer", $"Panel read after {scrolls} scroll(s): core={read.Core.Count}, additional={read.Additional.Count}, incompleteAdditional={read.HasIncompleteAdditionalRows}, needsScroll={read.NeedsScroll}");
                diagnosticResult = new { Status = "本局信息读取中；包含滚动合并的盟约结果，截图对应最后一帧", Read = read, Scrolls = scrolls };
                if (!read.NeedsScroll) break;
                scrollMessage = "附加盟约未完整显示。请向下滚动左侧盟约面板，确保最后一行图标和名称完整可见后，再点击“重新录入”。";
                if (scrolls == 3 || ScrollInformationPanelAsync is not { } scroll
                    || !await _informationDetector.IsOpeningScreenAsync(shot, imgWidth, imgHeight))
                {
                    AppLogger.Warn("BanAnalyzer", scrollMessage);
                    AnalysisFailed?.Invoke(scrollMessage);
                    return false;
                }
                var next = await scroll();
                if (captureRevision != CaptureRevisionProvider()) return false;
                if (next is null)
                {
                    AppLogger.Warn("BanAnalyzer", "Information panel scroll or subsequent screenshot failed");
                    AnalysisFailed?.Invoke(scrollMessage);
                    return false;
                }
                shotHash = HashShot(next);
                var (nextWidth, nextHeight) = GetPngDimensions(next);
                (shot, imgWidth, imgHeight) = ScreenshotNormalizer.EnsureAnalysable(next, nextWidth, nextHeight);
                capturedAt = DateTimeOffset.Now;
                diagnosticFrame = ImageBytes.ToPng(shot);
                diagnosticResult = new { Status = "滚动后分析未完成；此前盟约结果属于前一帧，请结合日志查看" };
                AppLogger.SaveArtifact("ban-scroll", diagnosticFrame, ".png");
            }
            scrollMessage = null;

            // 标题判定已经包含在上面那次 OCR 里（省掉一次独立的检测调用）；
            // 只有标题缺失时才用检测器区分“不是信息页”和“信息页被滚动”。
            if (!read.SawAdditionalHeader)
            {
                var looksLikeInfo = await _informationDetector.IsInformationScreenAsync(shot, imgWidth, imgHeight);
                AnalysisFailed?.Invoke(looksLikeInfo
                    ? "未找到完整的核心/附加盟约区域，请打开本局信息页并保持面板顶部可见。"
                    : "当前画面不是本局信息页，请切换到“确认本局信息”后重试。");
                return false;
            }
            // 两类盟约都可能只显示“本局阵容包含的条目”（标题会注明“部分盟约所含干员阵容
            // 不完整”），所以不能要求与数据文件里的总数相等；真正的完整性判据是：
            // 至少读到一项，且没有出现“有图标却没有名字”的槽位。
            // 检测到图标网格时，完整性 = 每个槽都读到了名字；没检测到网格（异常画面）时，
            // 退回数量下限，避免“只读到 1 个”也被当成完整结果发布出去。
            var hasCore = read.Core.Count >= 6 && (read.CoreSlots == 0 || read.Core.Count == read.CoreSlots);
            var hasAdditional = read.AdditionalSlots > 0
                ? read.Additional.Count == read.AdditionalSlots
                : read.Additional.Count >= 4;
            var unnamed = read.UnnamedCore.Count + read.UnnamedAdditional.Count;
            if (!hasCore || !hasAdditional || unnamed > 0 || read.HasIncompleteRows
                || read.Core.Select(c => c.Name).Distinct().Count() != read.Core.Count
                || read.Additional.Select(c => c.Name).Distinct().Count() != read.Additional.Count)
            {
                var clipped = read.HasIncompleteRows || read.UnnamedAdditional.Any(p => p.Y / imgHeight > .88);
                AnalysisFailed?.Invoke(clipped
                    ? $"面板存在未完整进入画面的盟约行（核心 {read.Core.Count}、附加 {read.Additional.Count}），未替换现有结果；请滚动到末行名称完整可见后重新扫描。"
                    : $"本次信息未完整读出（核心 {read.Core.Count}、附加 {read.Additional.Count}、未命名 {unnamed}），未替换现有结果；请保持页面完整可见后重试。");
                return false;
            }
            var enemy = await _enemyRecognizer.RecognizeAsync(shot, imgWidth, imgHeight);
            if (captureRevision != CaptureRevisionProvider()) return false;
            var hasEnemy = enemy is not null && enemy.BossName.Length > 0 && enemy.EnemyTypes.Count > 0;
            var enemyFaction = hasEnemy ? enemy!.BossName : null;

            sw.Stop();
            // Low-level solo and express show four selected cores out of eight, even on the
            // in-game page without a difficulty label. Co-op can show eight active cores.
            var reducedSolo = read.CoreSlots == 8 && read.AdditionalSlots == 10
                && read.Core.Count(c => c.IsBanned) == 4 && read.Additional.Count(c => c.IsBanned) == 2;
            var missing = new HashSet<string>(StringComparer.Ordinal);
            if (reducedSolo)
            {
                missing.UnionWith(_coreCovenantNames.Except(read.Core.Select(c => c.Name)));
                missing.UnionWith(_additionalCovenantNames.Except(read.Additional.Select(c => c.Name)));
            }
            var result = new BanScreenResult(read.Core, read.Additional, enemyFaction, missing);
            if (PrepareNewRunAsync is { } prepare)
            {
                var bansChanged = _lastResult is not null
                    && (!result.MissingCovenants.SetEquals(_lastResult.MissingCovenants)
                        || !result.CoreCovenants.Where(c => c.IsBanned).Select(c => c.Name).ToHashSet(StringComparer.Ordinal)
                            .SetEquals(_lastResult.CoreCovenants.Where(c => c.IsBanned).Select(c => c.Name))
                        || !result.AdditionalCovenants.Where(c => c.IsBanned).Select(c => c.Name).ToHashSet(StringComparer.Ordinal)
                            .SetEquals(_lastResult.AdditionalCovenants.Where(c => c.IsBanned).Select(c => c.Name)));
                var isOpening = bansChanged || await _informationDetector.IsOpeningScreenAsync(shot, imgWidth, imgHeight);
                if (captureRevision != CaptureRevisionProvider()) return false;
                if (isOpening)
                    await prepare(bansChanged);
            }
            if (captureRevision != CaptureRevisionProvider()) return false;
            var autoBans = ComputeAutoBans(result);
            LogRecognitionSummary(result, autoBans);
            AutoBanListComputed?.Invoke(autoBans);
            if (hasEnemy) EnemyDetected?.Invoke(enemy!);
            else AppLogger.Warn("BanAnalyzer", "Covenant Ban read succeeded, but enemy region was incomplete; publishing covenant result only");
            LogResults(result, sw.ElapsedMilliseconds);
            diagnosticResult = new { Status = hasEnemy ? "识别完成" : "盟约已录入，敌人信息未完整读出",
                Note = "盟约可能包含滚动合并结果；画面对应最后一次分析截图", Result = result, Enemy = enemy, ElapsedMs = sw.ElapsedMilliseconds };

            _lastShotHash = shotHash;
            _lastCaptureRevision = captureRevision;
            _lastResult = result;
            _lastAutoBans = autoBans;
            _lastEnemy = hasEnemy ? enemy : null;
            _scrollReminderShown = false;
            AnalysisCompleted?.Invoke(result);
            if (showGameResult || !hasEnemy)
                await ShowTimedGameOverlayMessageAsync(hasEnemy
                    ? "识别成功！鸿雪·卫戍助手祝您卫戍愉快！"
                    : "盟约已录入，敌人信息可重新截屏录入", 2000);
            return hasEnemy;
        }
        catch (Exception ex)
        {
            diagnosticResult = new { Status = "本局信息识别异常", Error = ex.ToString() };
            AppLogger.Error("BanAnalyzer", "Analysis failed", ex);
            AnalysisFailed?.Invoke("本次录入失败，未替换现有结果；详细原因已记录到日志。");
            return false;
        }
        finally
        {
            if (diagnosticFrame is not null && diagnosticRevision == CaptureRevisionProvider())
                RecognitionDiagnostics.Record(capturedAt, "本局信息页识别", diagnosticFrame, diagnosticResult);
            AnalysisBusyChanged?.Invoke(false);
            Interlocked.Exchange(ref _running, 0);
            if (scrollMessage is not null && !_scrollReminderShown)
            {
                _scrollReminderShown = true;
                ScrollRequired?.Invoke(scrollMessage);
            }
        }
    }

    private async Task ShowTimedGameOverlayMessageAsync(string message, int durationMs)
    {
        ShowGameOverlayMessageRequested?.Invoke(message);
        await Task.Delay(durationMs);
        HideGameOverlayMessageRequested?.Invoke();
    }

    // ── Auto-ban computation ──────────────────────────────────────────────────

    private IReadOnlyList<string> ComputeAutoBans(BanScreenResult result)
    {
        var bannedCoreCovenants = result.CoreCovenants
            .Where(c => c.IsBanned)
            .Select(c => c.Name)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToHashSet(StringComparer.Ordinal);
        var bannedAdditionalCovenants = result.AdditionalCovenants
            .Where(c => c.IsBanned)
            .Select(c => c.Name)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToHashSet(StringComparer.Ordinal);
        var bannedCovenants = bannedCoreCovenants
            .Concat(bannedAdditionalCovenants)
            .ToHashSet(StringComparer.Ordinal);

        var autoBannedOperators = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in _operators)
        {
            if (string.IsNullOrWhiteSpace(op.Name))
                continue;

            // Share the planner's core-first covenant ban rule.
            if (CovenantBanRules.HasMissingCovenant(op, result.MissingCovenants)
                || op.RequiredStrategyId.Length == 0 && CovenantBanRules.IsExcluded(op, bannedCovenants))
                autoBannedOperators.Add(op.Name);
        }

        AppLogger.Info("BanAnalyzer",
            $"Auto-ban computed: total={autoBannedOperators.Count} " +
            $"ruleContext(coreBanned={bannedCoreCovenants.Count}, additionalBanned={bannedAdditionalCovenants.Count}, banned={bannedCovenants.Count}) " +
            $"operators=[{string.Join(", ", autoBannedOperators)}]");

        return autoBannedOperators.ToList();
    }

    private static void LogRecognitionSummary(BanScreenResult result, IReadOnlyList<string> autoBans)
    {
        string enemy = string.IsNullOrWhiteSpace(result.EnemyFaction) ? "(未识别)" : result.EnemyFaction;
        string core = string.Join(", ", result.CoreCovenants
            .Select(c => $"{c.Name}:{(c.IsBanned ? "禁用" : "进入")}"));
        string additional = string.Join(", ", result.AdditionalCovenants
            .Select(c => $"{c.Name}:{(c.IsBanned ? "禁用" : "进入")}"));
        string bans = string.Join(", ", autoBans);

        AppLogger.Info("BanAnalyzer",
            $"识别结果汇总: 敌人={enemy}; 核心盟约=[{core}]; 附加盟约=[{additional}]; 自动禁用干员=[{bans}]");
    }

    // ── Results output ────────────────────────────────────────────────────────

    private static void LogResults(BanScreenResult result, long elapsedMs)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("══════════════ ban-screen analysis ══════════════");

        sb.Append("本场敌人: ");
        sb.AppendLine(result.EnemyFaction ?? "(未识别)");

        sb.AppendLine("核心盟约:");
        if (result.CoreCovenants.Count == 0)
        {
            sb.AppendLine("  (未识别)");
        }
        else
        {
            foreach (var c in result.CoreCovenants.OrderBy(c => c.X))
                sb.AppendLine($"  {c.Name,-12} {(c.IsBanned ? "【禁用】" : "  启用 ")}");
        }

        sb.AppendLine("附加盟约:");
        if (result.AdditionalCovenants.Count == 0)
        {
            sb.AppendLine("  (未识别)");
        }
        else
        {
            foreach (var c in result.AdditionalCovenants.OrderBy(c => c.X))
                sb.AppendLine($"  {c.Name,-12} {(c.IsBanned ? "【禁用】" : "  启用 ")}");
        }

        sb.Append($"耗时: {elapsedMs} ms");
        sb.AppendLine();
        sb.AppendLine("═════════════════════════════════════════════════");

        var output = sb.ToString();
        AppLogger.Info("BanAnalyzer", output);
        AppConsole.Write(output);
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    /// <summary>截图内容哈希：用来判断“是不是同一张画面”，命中就直接复用上次结果。</summary>
    private static string HashShot(byte[] shot) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(shot));

    private static (int Width, int Height) GetPngDimensions(byte[] image) => ImageBytes.GetDimensions(image);
}
