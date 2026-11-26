using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;

namespace ASPAssistant.Core.ViewModels;

/// <summary>One way to play an auto plan (先小后大 / 先大后小): a button on the plan card.</summary>
public sealed class AutoStyleChoice(AutoPlanItem plan, AutoPlanStyle style, bool isRecommended)
{
    public AutoPlanItem Plan { get; } = plan;
    public AutoPlanStyle Style { get; } = style;
    public bool IsRecommended { get; } = isRecommended;
    public string Name => AutoPlanStyles.Name(Style);
    /// <summary>Short enough for two buttons side by side on the narrowest panel.</summary>
    public string Label => IsRecommended ? $"{Name} · 推荐" : $"{Name} ›";
    public string Hint => AutoPlanStyles.Description(Style) + (IsRecommended ? $"\n推荐理由：{Plan.Source.StyleReason}" : "")
        + (Plan.Source.RouteComparison is { } replay ? $"\n{replay.Detail(Style)}" : "");
}

/// <summary>A 本家 on the 自动 page: its 成型阵容 lineup, where its stackers sit and the two ways to play it.</summary>
public sealed class AutoPlanItem
{
    public AutoPlanItem(AutoPlanCandidate source, int rank, PlannerLineupItem lineup)
    {
        Source = source;
        Rank = rank;
        Lineup = lineup;
        var other = source.RecommendedStyle == AutoPlanStyle.SmallFirst ? AutoPlanStyle.CoreFirst : AutoPlanStyle.SmallFirst;
        Styles = [new AutoStyleChoice(this, source.RecommendedStyle, true), new AutoStyleChoice(this, other, false)];
    }

    public AutoPlanCandidate Source { get; }
    /// <summary>The 成型阵容 lineup as a planner result card (members, covenant pills, strategy piece).</summary>
    public PlannerLineupItem Lineup { get; }
    public int Rank { get; }
    public bool IsFirst => Rank == 1;
    public string MainCovenant => Source.MainCovenant;
    /// <summary>“炎 6”: the 本家 and the people the 成型阵容 lineup gives it.</summary>
    public string Title => $"{Source.MainCovenant} {Source.MainGoal.Count}";
    public string Subtitle { get; init; } = "";
    public IReadOnlyList<PlannerGoalPill> Covenants => Lineup.Goals;
    public IReadOnlyList<PlannerOperatorItem> Operators => Lineup.Operators;
    public PlannerStrategyPiece? StrategyPiece => Lineup.StrategyPiece;
    public bool HasStrategyPiece => StrategyPiece is not null;
    /// <summary>“叠层手：折光 Ⅱ · 黑键 Ⅳ · 纯烬艾雅法拉 Ⅴ”.</summary>
    public string StackText { get; init; } = "";
    /// <summary>“四本前约 12 层/回合 · 满配约 25 层/回合”.</summary>
    public string LayerText { get; init; } = "";
    public bool HasLayerText => LayerText.Length > 0;
    public string StyleReason => Source.StyleReason + (Source.RouteComparison is { } replay ? $"\n{replay.Summary}" : "");
    public string BossNote { get; init; } = "";
    public string Note => string.Join("\n", new[] { string.Join("；", Source.Notes), BossNote, Lineup.EquipmentText }.Where(t => t.Length > 0));
    public bool HasNote => Source.Notes.Count > 0 || BossNote.Length > 0 || Lineup.HasEquipment;
    /// <summary>The recommended style first.</summary>
    public IReadOnlyList<AutoStyleChoice> Styles { get; }
}

public partial class LineupPlannerViewModel
{
    private readonly AutoPlanner _autoPlanner;
    private CancellationTokenSource? _autoCts;
    private AutoPlanRequest? _autoRequest;
    private IReadOnlyList<AutoPlanCandidate> _unrankedCandidates = [];
    private (string Boss, int? Level) _bossContext = ("", null);
    private bool _autoDirty = true;
    private string? _autoKey;
    private bool _pageStateChanging;
    private PlanningInputs? _manualInputs;
    private PlanningInputs? _automaticInputs;
    private AutoPlanRoute? _currentAutoRoute;
    private AutoPlanRunState _autoRunState = new(0, null, null);
    private bool _autoAdviceDirty;
    private readonly string _autoCatalogStamp;
    private AutoRouteConditions? _currentAutoConditions;
    private Lineup? _savedAutoSource;
    private bool _savedAutoBlocked;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutoRouteContext))]
    private string _autoRouteContext = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutoRouteNotice))]
    private string _autoRouteNotice = "";
    public bool HasAutoRouteContext => AutoRouteContext.Length > 0;
    public bool HasAutoRouteNotice => AutoRouteNotice.Length > 0;

    public string? OpenSavedAutoRoute(Lineup lineup)
    {
        if (lineup.AutoRoute is not { } archive) return "此阵容没有完整自动路线，请重新生成并保存";
        if (archive.Error.Length > 0) return $"无法读取自动路线：{archive.Error}。阵容仍可编辑和追踪";
        var entering = !ShowAuto;
        if (entering)
        {
            _manualInputs = CapturePlanningInputs();
            _automaticInputs ??= _manualInputs with { RepairSource = null, Targets = [], Banned = [], Locks = [] };
        }
        _pageStateChanging = true;
        _cts?.Cancel(); _cts = null; IsBusy = false;
        PauseAutoPlanning();
        CloseRoute();
        ShowAuto = true; ShowResults = false;
        if (entering) RestorePlanningInputs(_automaticInputs!);
        _pageStateChanging = false;
        _savedAutoSource = lineup;
        _currentAutoConditions = archive.Conditions;
        var plan = new AutoPlanRoute { Candidate = archive.Plan.Candidate, Style = archive.Plan.Style, Advice = archive.Plan.Advice,
            Route = new PlannedRoute { Stages = archive.Plan.Route.Stages.Select(s => s with
                { Operators = s.Operators.Select(o => o with { IconPath = _allOperatorIcons.GetValueOrDefault(o.Name, "") }).ToList() }).ToList() } };
        _routeItem = BuildItem(plan.Candidate.Final, 0);
        _currentRoute = plan.Route; _currentAutoRoute = plan;
        RouteFromAuto = true; ShowRoute = true;
        RouteTitle = $"{lineup.Name} · 已保存路线";
        RouteSubtitle = $"{plan.Candidate.Final.People} 人 · 运营建议按本局刷新";
        AutoRouteContext = FormatAutoRouteContext(plan, archive.Conditions);
        foreach (var (stage, index) in plan.Route.Stages.Select((s, i) => (s, i)))
        {
            var item = BuildStageItem(stage, plan.Route.ExpiresAt(index), plan.Candidate.Final, plan.Advice[index]);
            RouteStages.Add(item);
            RouteSteps.Add(new() { Name = item.ShortTitle, Number = index + 1, HasLine = index < plan.Route.Stages.Count - 1, Stage = item });
        }
        RefreshSavedAutoRouteStatus();
        RefreshRouteProgress(expandCurrent: true);
        OnPropertyChanged(nameof(SaveRouteLabel));
        return null;
    }

    internal static string FormatAutoRouteContext(AutoPlanRoute plan, AutoRouteConditions conditions)
    {
        return $"生成背景：{plan.Candidate.MainCovenant} · {AutoPlanStyles.Name(plan.Style)} · {conditions.People}人 · {AvailableStrategies.Single(s => s.Strategy == conditions.Strategy).Name}\n"
            + $"推荐理由：{plan.Candidate.StyleReason}\n"
            + $"当时禁用盟约：{(conditions.BannedCovenants.Length == 0 ? "无" : string.Join("、", conditions.BannedCovenants))}；"
            + $"禁用干员：{(conditions.BannedOperators.Length == 0 ? "无" : string.Join("、", conditions.BannedOperators))}"
            + (conditions.LockedOperators.Length == 0 ? "" : $"\n当时必带：{string.Join("、", conditions.LockedOperators)}");
    }

    public void NotifySavedAutoRouteSourceChanged(Lineup lineup)
    {
        if (_savedAutoSource?.Id != lineup.Id) return;
        _savedAutoSource = lineup;
        RefreshSavedAutoRouteStatus();
    }

    private void RefreshSavedAutoRouteStatus()
    {
        if (_savedAutoSource is not { AutoRoute: { } archive } source) return;
        var issues = new List<string>();
        if (!archive.Matches(source)) issues.Add("阵容已修改，原路线与当前阵容不一致");
        if (archive.CatalogFingerprint != _autoCatalogStamp) issues.Add("干员或盟约资料已变化");
        var plan = archive.Plan;
        var knownGoals = _planner.Covenants.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        if (plan.Candidate.Goals.Concat(plan.Candidate.SmallGoals).Append(plan.Candidate.MainGoal)
            .Concat(plan.Route.Stages.SelectMany(s => s.Definition.Required.Concat(s.Definition.SmallGoals ?? [])))
            .Any(g => !knownGoals.Contains(g.Name))) issues.Add("部分目标盟约资料不存在");
        var banned = _banVm.BannedCovenants.ToHashSet(StringComparer.Ordinal);
        var allowed = _planner.DefaultTransformAllowed(banned, _banVm.MissingCovenants);
        var invalid = plan.Candidate.Final.Operators.Select(o => o.TransformedCovenant)
            .Concat(plan.Candidate.Final.MechCovenants)
            .Concat(plan.Route.Stages.SelectMany(s => s.Operators.Select(o => o.TransformedCovenant)))
            .OfType<string>().Distinct().Where(n => !allowed.Contains(n) || !_equipmentRules.CanTransformInto(n)).ToList();
        if (invalid.Count > 0) issues.Add($"本局不可转入：{string.Join("、", invalid)}");
        if (plan.Candidate.Goals.Concat(plan.Candidate.SmallGoals).Append(plan.Candidate.MainGoal)
            .Concat(plan.Route.Stages.SelectMany(s => s.Definition.Required.Concat(s.Definition.SmallGoals ?? [])))
            .Any(g => _banVm.MissingCovenants.Contains(g.Name))) issues.Add("原路线含本局未出现的盟约");
        var routeOperators = plan.Route.Stages.SelectMany(stage => stage.Operators).ToList();
        if (routeOperators.Any(item => _operatorByName.TryGetValue(item.Name, out var op) && op.IsCustom && op.Tier != item.Tier))
            issues.Add("自选干员等阶已变化");
        if (routeOperators.Select(item => item.Name).Concat(plan.Candidate.Final.Operators.Select(item => item.Name)).Distinct()
            .Select(name => _operatorByName.GetValueOrDefault(name)).OfType<Operator>()
            .Where(op => op.IsCustom).GroupBy(op => op.Tier).Any(group => group.Count() > 2))
            issues.Add("原路线超出同阶自选携带上限");
        var unavailable = plan.Route.Stages.SelectMany(s => s.Operators).Select(o => o.Name)
            .Concat(plan.Candidate.Final.Operators.Select(o => o.Name)).Distinct(StringComparer.Ordinal)
            .Where(name => _banVm.BannedNames.Contains(name) || _operatorByName.TryGetValue(name, out var op)
                && (!op.IsAvailableForPlanning || CovenantBanRules.HasMissingCovenant(op, _banVm.MissingCovenants) || archive.Conditions.ExcludeMixedBannedOperators && _planner.IsExcludedByBans(op, banned)))
            .ToList();
        if (banned.Contains(plan.Candidate.MainCovenant)) issues.Add($"本局禁用主盟约{plan.Candidate.MainCovenant}");
        if (unavailable.Count > 0) issues.Add($"本局不可用人选：{string.Join("、", unavailable)}");
        _savedAutoBlocked = issues.Count > 0;
        AutoRouteNotice = _savedAutoBlocked ? string.Join("；", issues) + "。以下为原路线参考，请修补或重新生成后使用"
            : "已恢复保存路线；旧Ban仅为生成背景，资金与持有提示使用当前局记录";
        if (_savedAutoBlocked)
            for (var i = 0; i < RouteStages.Count; i++) RouteStages[i].Advice = plan.Advice[i];
        _autoAdviceDirty = true;
        RefreshAutoAdvice();
        TrackRouteCommand.NotifyCanExecuteChanged(); SaveRouteCommand.NotifyCanExecuteChanged();
    }

    public void SetAutoRunState(AutoPlanRunState state)
    {
        var poolChanged = _autoRunState.TerminalLevel != state.TerminalLevel || (_autoRunState.Owned is null
            ? state.Owned is not null : state.Owned is null || !_autoRunState.Owned.SetEquals(state.Owned));
        if (!poolChanged && _autoRunState.Economy == state.Economy) return;
        _autoRunState = state;
        if (poolChanged)
            foreach (var item in VisibleLineups)
                item.RefreshSubstitutions(_operatorByName, state);
        _autoAdviceDirty = true;
        RefreshAutoAdvice();
    }

    public void SetBossContext(string boss, int? level)
    {
        if (_bossContext == (boss, level)) return;
        _bossContext = (boss, level);
        if (_unrankedCandidates.Count > 0) RankAutoPlans();
    }

    private void RankAutoPlans()
    {
        AutoPlans.Clear();
        foreach (var entry in BossCovenantRanking.Rank(_unrankedCandidates, _bossContext.Boss, _bossContext.Level))
            AutoPlans.Add(BuildAutoItem(entry.Candidate, AutoPlans.Count + 1, entry.Note));
        OnPropertyChanged(nameof(HasAutoPlans));
    }

    private void RefreshAutoAdvice()
    {
        if (!IsActive || !_autoAdviceDirty || _savedAutoBlocked || _currentAutoRoute is not { } plan) return;
        var advice = _autoPlanner.Advise(plan.Route, plan.Style, plan.Candidate, _autoRunState);
        for (var i = 0; i < RouteStages.Count; i++) RouteStages[i].Advice = advice[i];
        _autoAdviceDirty = false;
    }
    private sealed record PlanningInputs(PlannerStrategy Strategy, bool MechElite, string[] Locks,
        Lineup? RepairSource, List<TargetCovenant> Targets, HashSet<string> Banned);

    public ObservableCollection<AutoPlanItem> AutoPlans { get; } = [];
    public bool HasAutoPlans => AutoPlans.Count > 0;
    public Task? PendingAutoPlan { get; private set; }
    [ObservableProperty] private bool _showAuto;
    [ObservableProperty] private bool _routeFromAuto;
    [ObservableProperty] private bool _isAutoBusy;
    [ObservableProperty] private string _autoStatusText = "";
    [ObservableProperty] private bool _autoNinePeople;
    public string SaveRouteLabel => _savedAutoSource is not null ? "另存路线" : RouteFromAuto ? "保存路线" : "保存";
    public bool ShowGoals => !ShowAuto && !ShowResults;
    public bool ShowRouteView => ShowRoute && (RouteFromAuto ? ShowAuto : ShowResults);
    public bool ShowResultList => !ShowAuto && ShowResults && !ShowRoute;
    public bool ShowAutoList => ShowAuto && !ShowRoute;
    public bool ShowResultsPanel => ShowResultList || ShowRouteView;

    private PlanningInputs CapturePlanningInputs() => new(Strategy, MechElite, LockedOperators.Select(l => l.Name).ToArray(),
        RepairSource, [.. _repairTargets.Select(t => t.Clone())], [.. _repairBanned]);

    private void RestorePlanningInputs(PlanningInputs state)
    {
        Strategy = state.Strategy;
        MechElite = state.MechElite;
        RepairSource = state.RepairSource;
        _repairTargets = state.Targets;
        _repairBanned = state.Banned;
        LockedOperators.Clear();
        foreach (var name in state.Locks.Where(name => _operatorByName[name].IsAvailableForPlanning)) LockedOperators.Add(new(_operatorByName[name], RemoveLocked));
        RefreshLockedOperators();
        LockSearchText = "";
        OnPropertyChanged(nameof(HasLocked));
    }

    [RelayCommand]
    private void ShowAutoPage()
    {
        if (ShowAuto) return;
        InvalidateStageRepairs();
        _manualInputs = CapturePlanningInputs();
        _automaticInputs ??= _manualInputs with { RepairSource = null, Targets = [], Banned = [],
            Locks = IsRepairing ? [] : _manualInputs.Locks };
        _pageStateChanging = true;
        _cts?.Cancel(); _cts = null; IsBusy = false; _dirty = true;
        CloseRoute();
        ShowAuto = true;
        ShowResults = false;
        RestorePlanningInputs(_automaticInputs);
        _pageStateChanging = false;
        EnsureAutoPlans();
    }

    private void LeaveAutoPage()
    {
        if (!ShowAuto) return;
        _automaticInputs = CapturePlanningInputs();
        _pageStateChanging = true;
        PauseAutoPlanning();
        CloseRoute();
        ShowAuto = false;
        RestorePlanningInputs(_manualInputs!);
        _pageStateChanging = false;
        RequestRecompute();
    }

    partial void OnShowAutoChanged(bool value) => NotifyPlanningPage();
    partial void OnShowResultsChanged(bool value) => NotifyPlanningPage();
    partial void OnShowRouteChanged(bool value) => NotifyPlanningPage();
    partial void OnRouteFromAutoChanged(bool value) => NotifyPlanningPage();
    private void NotifyPlanningPage()
    {
        OnPropertyChanged(nameof(SaveRouteLabel));
        OnPropertyChanged(nameof(ShowGoals));
        OnPropertyChanged(nameof(ShowRouteView));
        OnPropertyChanged(nameof(ShowResultList));
        OnPropertyChanged(nameof(ShowAutoList));
        OnPropertyChanged(nameof(ShowResultsPanel));
    }
    partial void OnAutoNinePeopleChanged(bool value) => InvalidateAutoPlans();
    private void PauseAutoPlanning()
    {
        if (!IsAutoBusy) return;
        _autoCts?.Cancel(); _autoCts = null;
        IsAutoBusy = false; _autoKey = null; _autoDirty = true;
    }
    private void InvalidateAutoPlans()
    {
        _autoDirty = true;
        if (_savedAutoSource is not null) { RefreshSavedAutoRouteStatus(); return; }
        EnsureAutoPlans();
    }
    private string AutoKey(AutoPlanRequest request) => string.Join('|',
        string.Join(',', request.BannedCovenants.Order(StringComparer.Ordinal)),
        string.Join(',', request.MissingCovenants.Order(StringComparer.Ordinal)),
        string.Join(',', request.BannedOperators.Order(StringComparer.Ordinal)),
        string.Join(',', request.LockedOperators.Order(StringComparer.Ordinal)),
        request.People, request.Strategy, request.MechElite,
        string.Join(',', _operators.Where(o => o.IsCustom && o.IsAvailableForPlanning).Select(o => $"{o.Name}:{o.Tier}").Order(StringComparer.Ordinal)));

    public AutoPlanRequest BuildAutoRequest()
    {
        var banned = _banVm.BannedCovenants.ToHashSet(StringComparer.Ordinal);
        return new()
        {
            MissingCovenants = _banVm.MissingCovenants.ToHashSet(),
            BannedCovenants = banned, BannedOperators = _banVm.BannedNames.ToHashSet(StringComparer.Ordinal),
            LockedOperators = LockedOperators.Where(l => !l.IsUnavailable).Select(l => l.Name).ToHashSet(StringComparer.Ordinal),
            TransformAllowed = _planner.DefaultTransformAllowed(banned, _banVm.MissingCovenants).Where(_equipmentRules.CanTransformInto).ToHashSet(),
            People = AutoNinePeople ? 9 : 8, Strategy = Strategy, MechElite = MechElite
        };
    }
    private void EnsureAutoPlans()
    {
        if (_savedAutoSource is not null || _pageStateChanging || !_autoDirty || !ShowAuto || !IsActive) return;
        _autoDirty = false;
        var request = BuildAutoRequest();
        var key = AutoKey(request);
        if (key == _autoKey) return;
        _autoKey = key;
        _autoCts?.Cancel();
        var cts = new CancellationTokenSource(); _autoCts = cts;
        if (RouteFromAuto) CloseRoute();
        _unrankedCandidates = [];
        AutoPlans.Clear(); OnPropertyChanged(nameof(HasAutoPlans));
        PendingAutoPlan = RunAutoPlanAsync(request, cts);
    }
    private async Task RunAutoPlanAsync(AutoPlanRequest request, CancellationTokenSource cts)
    {
        IsAutoBusy = true; AutoStatusText = "正在生成主盟约候选…";
        try
        {
            if (DebounceMilliseconds > 0) await Task.Delay(DebounceMilliseconds, cts.Token);
            var watch = Stopwatch.StartNew();
            var result = await Task.Run(() => _autoPlanner.Plan(request, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_autoCts, cts)) return;
            _autoRequest = request;
            _unrankedCandidates = result.Candidates;
            RankAutoPlans();
            AutoStatusText = result.Error ?? (AutoPlans.Count > 0
                ? $"{AutoPlans.Count} 个主盟约可选 · {watch.Elapsed.TotalSeconds:0.0} 秒"
                : result.SearchLimited ? "搜索受限，暂未找到候选；不代表无解" : "当前条件下没有找到可用候选");
            if (request.LockedOperators.Count > 0) AutoStatusText += $" · 必带 {request.LockedOperators.Count} 人";
            if (result.SearchLimited && AutoPlans.Count > 0) AutoStatusText += " · 搜索受限";
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_autoCts, cts)) IsAutoBusy = false; }
    }

    [RelayCommand]
    private Task OpenAutoPlan(AutoStyleChoice? choice) => choice is null ? Task.CompletedTask : PlanAutoRouteAsync(choice);
    public async Task PlanAutoRouteAsync(AutoStyleChoice choice)
    {
        CloseRoute();
        var candidate = choice.Plan.Source;
        var cts = new CancellationTokenSource(); _routeCts = cts;
        _routeItem = new PlannerLineupItem { Source = candidate.Final, Title = choice.Plan.Title,
            Operators = choice.Plan.Operators, Goals = choice.Plan.Covenants, StrategyPiece = choice.Plan.StrategyPiece,
            LineupName = $"自动 {candidate.MainCovenant} {choice.Name}" };
        RouteFromAuto = true; ShowRoute = true; IsRouteBusy = true;
        RouteTitle = $"{candidate.MainCovenant} · {choice.Name}";
        RouteSubtitle = $"{candidate.Final.People} 人 · 运营建议供参考";
        var request = _autoRequest!;
        try
        {
            var plan = await Task.Run(() => _autoPlanner.PlanRoute(candidate, choice.Style, request, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_routeCts, cts)) return;
            foreach (var (stage, index) in plan.Route.Stages.Select((stage, index) => (stage, index)))
            {
                var item = BuildStageItem(stage, plan.Route.ExpiresAt(index), candidate.Final, plan.Advice[index]);
                RouteStages.Add(item);
                RouteSteps.Add(new() { Name = item.ShortTitle, Number = index + 1, HasLine = index < plan.Route.Stages.Count - 1, Stage = item });
            }
            _currentRoute = plan.Route;
            _currentAutoRoute = plan;
            _currentAutoConditions = new([.. request.BannedCovenants], [.. request.BannedOperators], [.. request.LockedOperators],
                [.. request.TransformAllowed], request.People, request.Strategy, request.MechElite, request.ExcludeMixedBannedOperators);
            AutoRouteContext = FormatAutoRouteContext(plan, _currentAutoConditions);
            _autoAdviceDirty = true;
            RefreshAutoAdvice();
            RefreshRouteProgress(expandCurrent: true);
            TrackRouteCommand.NotifyCanExecuteChanged(); SaveRouteCommand.NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_routeCts, cts)) IsRouteBusy = false; }
    }
    private AutoPlanItem BuildAutoItem(AutoPlanCandidate candidate, int rank, string bossNote)
    {
        var final = candidate.Final;
        var subtitle = $"{final.People} 人" + (final.Transforms > 0 ? $" · {final.Transforms} 转" : "") + $" · 附加盟约 {final.AdditionalScore} 分";
        var stackers = candidate.Stackers.Count == 0 ? "没有专属叠层手"
            : "叠层手：" + string.Join(" · ", candidate.Stackers.Select(s => $"{s.Name} {RomanOf(s.Tier)}"));
        return new AutoPlanItem(candidate, rank, BuildItem(final, rank - 1))
        {
            BossNote = bossNote,
            Subtitle = subtitle,
            StackText = stackers,
            LayerText = candidate.LateLayers > 0 ? $"四本前约 {candidate.EarlyLayers:0.#} 层/回合 · 理论可用叠层手约 {candidate.LateLayers:0.#} 层/回合" : ""
        };
    }

}
