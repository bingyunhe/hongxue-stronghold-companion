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
    public string Hint => AutoPlanStyles.Description(Style) + (IsRecommended ? $"\n推荐理由：{Plan.StyleReason}" : "");
}

/// <summary>A 本家 on the 自动 page: its 大成 lineup, where its stackers sit and the two ways to play it.</summary>
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
    /// <summary>The 大成 lineup as a planner result card (members, covenant pills, strategy piece).</summary>
    public PlannerLineupItem Lineup { get; }
    public int Rank { get; }
    public bool IsFirst => Rank == 1;
    public string MainCovenant => Source.MainCovenant;
    /// <summary>“炎 6”: the 本家 and the people the 大成 lineup gives it.</summary>
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
    public string StyleReason => Source.StyleReason;
    public string Note => string.Join("；", Source.Notes);
    public bool HasNote => Source.Notes.Count > 0;
    /// <summary>The recommended style first.</summary>
    public IReadOnlyList<AutoStyleChoice> Styles { get; }
}

// 自动规划: one candidate per core covenant, each playable 先小后大 or 先大后小 (see AutoPlanner).
public partial class LineupPlannerViewModel
{
    private AutoPlanner _autoPlanner = null!;
    private CancellationTokenSource? _autoCts;
    /// <summary>The request the listed candidates were planned with (their routes use the same bans).</summary>
    private AutoPlanRequest? _autoRequest;
    private bool _autoDirty = true;
    /// <summary><see cref="AutoKey"/> of the request the candidates were (or are being) planned with.</summary>
    private string? _autoKey;

    public ObservableCollection<AutoPlanItem> AutoPlans { get; } = [];
    public bool HasAutoPlans => AutoPlans.Count > 0;

    /// <summary>The candidate search currently running; exposed for tests.</summary>
    public Task? PendingAutoPlan { get; private set; }

    /// <summary>The 自动 page is selected (candidate list, or the route of one of them).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGoals), nameof(ShowRouteView), nameof(ShowResultList), nameof(ShowAutoList), nameof(ShowResultsPanel))]
    private bool _showAuto;

    /// <summary>The route on screen belongs to an auto plan (it carries 抓位 / 升本 advice and lives on the 自动 page).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRouteView), nameof(ShowResultList), nameof(ShowAutoList), nameof(ShowResultsPanel))]
    private bool _routeFromAuto;

    [ObservableProperty] private bool _isAutoBusy;
    [ObservableProperty] private string _autoStatusText = "";
    /// <summary>Plan the 大成 lineups with 9 people instead of 8.</summary>
    [ObservableProperty] private bool _autoNinePeople;

    // The three pages share one route panel: it shows on the page the route was opened from.
    public bool ShowGoals => !ShowResults && !ShowAuto;
    public bool ShowRouteView => ShowRoute && (RouteFromAuto ? ShowAuto : ShowResults);
    public bool ShowResultList => ShowResults && !(ShowRoute && !RouteFromAuto);
    public bool ShowAutoList => ShowAuto && !(ShowRoute && RouteFromAuto);
    /// <summary>The panel that holds both the result list and the route.</summary>
    public bool ShowResultsPanel => ShowResultList || ShowRouteView;

    partial void OnShowResultsChanged(bool value)
    {
        if (value) ShowAuto = false;
        NotifyPageChanged();
    }

    partial void OnShowAutoChanged(bool value)
    {
        if (!value) return;
        ShowResults = false;
        EnsureAutoPlans();
    }

    partial void OnShowRouteChanged(bool value) => NotifyPageChanged();

    private void NotifyPageChanged()
    {
        OnPropertyChanged(nameof(ShowGoals));
        OnPropertyChanged(nameof(ShowRouteView));
        OnPropertyChanged(nameof(ShowResultList));
        OnPropertyChanged(nameof(ShowAutoList));
        OnPropertyChanged(nameof(ShowResultsPanel));
    }

    partial void OnAutoNinePeopleChanged(bool value) => InvalidateAutoPlans();

    [RelayCommand] private void ShowAutoPage() => ShowAuto = true;

    /// <summary>Bans, strategy or locked operators changed: the candidates are stale.</summary>
    private void InvalidateAutoPlans()
    {
        _autoDirty = true;
        EnsureAutoPlans();
    }

    /// <summary>Plans the candidates when the 自动 page is on screen and they are stale.</summary>
    private void EnsureAutoPlans()
    {
        if (!_autoDirty || !ShowAuto || !IsActive) return;
        _autoDirty = false;
        var request = BuildAutoRequest();
        // Every input change lands here, also the ones the candidates do not depend on (a target was toggled).
        var key = AutoKey(request);
        if (key == _autoKey) return;
        _autoKey = key;
        _autoCts?.Cancel();
        var cts = new CancellationTokenSource();
        _autoCts = cts;
        // A route of the old candidates no longer matches the run.
        if (RouteFromAuto) CloseRoute();
        AutoPlans.Clear();
        OnPropertyChanged(nameof(HasAutoPlans));
        PendingAutoPlan = RunAutoPlanAsync(request, cts);
    }

    /// <summary>Everything the candidates depend on: bans, locked operators, people, strategy and the selected 自选 operators.</summary>
    private string AutoKey(AutoPlanRequest request) => string.Join('|',
        string.Join(',', request.BannedCovenants.Order(StringComparer.Ordinal)),
        string.Join(',', request.BannedOperators.Order(StringComparer.Ordinal)),
        string.Join(',', request.LockedOperators.Order(StringComparer.Ordinal)),
        request.People, request.Strategy, request.MechElite,
        string.Join(',', _operators.Where(o => o.IsCustom && o.IsSelectedCustom).Select(o => o.Name).Order(StringComparer.Ordinal)));

    public AutoPlanRequest BuildAutoRequest()
    {
        var bannedCovenants = new HashSet<string>(_banVm.BannedCovenants, StringComparer.Ordinal);
        return new AutoPlanRequest
        {
            BannedCovenants = bannedCovenants,
            BannedOperators = new HashSet<string>(_banVm.BannedNames, StringComparer.Ordinal),
            LockedOperators = LockedOperators.Select(l => l.Name).ToHashSet(StringComparer.Ordinal),
            TransformAllowed = _planner.DefaultTransformAllowed(bannedCovenants).Where(_equipmentRules.CanTransformInto).ToHashSet(),
            People = AutoNinePeople ? 9 : 8,
            Strategy = Strategy,
            MechElite = MechElite
        };
    }

    private async Task RunAutoPlanAsync(AutoPlanRequest request, CancellationTokenSource cts)
    {
        IsAutoBusy = true;
        AutoStatusText = "正在为每个核心盟约生成方案…";
        try
        {
            var sw = Stopwatch.StartNew();
            var result = await Task.Run(() => _autoPlanner.Plan(request, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_autoCts, cts)) return;
            _autoRequest = request;
            AutoPlans.Clear();
            var rank = 0;
            foreach (var candidate in result.Candidates) AutoPlans.Add(BuildAutoItem(candidate, ++rank));
            OnPropertyChanged(nameof(HasAutoPlans));
            AutoStatusText = result.Error
                ?? (AutoPlans.Count == 0 ? "没有可规划的主盟约：核心盟约都被 Ban，或可用干员不够"
                    : $"{AutoPlans.Count} 个主盟约可选 · {sw.Elapsed.TotalSeconds:0.0} 秒"
                      + (request.LockedOperators.Count > 0 ? $" · 必带 {request.LockedOperators.Count} 人" : ""));
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request.
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_autoCts, cts)) return;
            AutoStatusText = $"自动规划出错：{ex.Message}";
            // Plan again the next time the page is shown.
            _autoKey = null;
            _autoDirty = true;
        }
        finally
        {
            if (ReferenceEquals(_autoCts, cts)) IsAutoBusy = false;
        }
    }

    private AutoPlanItem BuildAutoItem(AutoPlanCandidate candidate, int rank)
    {
        var final = candidate.Final;
        var subtitle = $"{final.People} 人" + (final.Transforms > 0 ? $" · {final.Transforms} 转" : "") + $" · 附加盟约 {final.AdditionalScore} 分";
        var stackers = candidate.Stackers.Count == 0 ? "没有专属叠层手"
            : "叠层手：" + string.Join(" · ", candidate.Stackers.Select(s => $"{s.Name} {RomanOf(s.Tier)}"));
        return new AutoPlanItem(candidate, rank, BuildItem(final, rank - 1))
        {
            Subtitle = subtitle,
            StackText = stackers,
            LayerText = candidate.LateLayers > 0 ? $"四本前约 {candidate.EarlyLayers:0.#} 层/回合 · 满配约 {candidate.LateLayers:0.#} 层/回合" : ""
        };
    }

    /// <summary>Opens the stage-by-stage plan (阵容 · 抓位 · 升本) of one candidate played one way.</summary>
    [RelayCommand]
    private Task OpenAutoPlan(AutoStyleChoice? choice) => choice is null ? Task.CompletedTask : PlanAutoRouteAsync(choice);

    public async Task PlanAutoRouteAsync(AutoStyleChoice choice)
    {
        var candidate = choice.Plan.Source;
        var cts = BeginRoute(
            new PlannerLineupItem
            {
                Source = candidate.Final,
                Title = choice.Plan.Title,
                Operators = choice.Plan.Operators,
                Goals = choice.Plan.Covenants,
                LineupName = $"自动 {candidate.MainCovenant} {choice.Name}",
                StrategyPiece = choice.Plan.StrategyPiece
            },
            $"{candidate.MainCovenant} · {choice.Name}",
            string.Join(" · ", candidate.Goals.Select(g => $"{g.Name} {g.Count}").Append($"{candidate.Final.People} 人")),
            fromAuto: true);
        var request = _autoRequest ?? BuildAutoRequest();
        try
        {
            var plan = await Task.Run(() => _autoPlanner.PlanRoute(candidate, choice.Style, request, cts.Token), cts.Token);
            if (!ReferenceEquals(_routeCts, cts)) return;
            ShowPlannedRoute(plan.Route, candidate.Final, plan.Advice);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_routeCts, cts)) IsRouteBusy = false;
        }
    }
}
