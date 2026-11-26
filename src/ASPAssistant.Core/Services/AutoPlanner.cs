using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Services;

/// <summary>
/// 自动规划: proposes one plan per core covenant that is available this run, and plays each of them one of two ways.
/// <para>
/// <see cref="Plan"/> finds, for every 本家 (main core covenant), the 成型阵容 lineup with the best 小盟约 and says
/// where its stackers sit: a 本家 that already stacks well below 五本 suits 先大后小, one whose stackers only show
/// up at 五本 and above suits 先小后大. The player picks the 本家 and the style.
/// </para>
/// <para>Routes reuse the existing five stages; advice is qualitative and never estimates upgrade rounds.</para>
/// </summary>
public sealed class AutoPlanner
{
    /// <summary>Terminal level whose shop first offers the big 本家 stackers (tier Ⅴ).</summary>
    public const int StackerLevel = 5;
    /// <summary>Stackers of one covenant a lineup realistically fields at once.</summary>
    private const int FieldedStackers = 3;
    /// <summary>先大后小 is recommended when the stackers below 五本 reach this share of the full set.</summary>
    private const double EarlyShareForCoreFirst = 0.5;
    private const int MaxReserve = 6;
    /// <summary>成型阵容 lineups compared per 本家 (the planner returns the first ones it finds, ranked by 小盟约).</summary>
    private const int FinalChoices = 16;

    private readonly LineupPlanner _planner;
    private readonly LineupRoutePlanner _routePlanner;
    private readonly IReadOnlyList<Operator> _operators;
    private readonly Dictionary<string, Operator> _byName;
    private readonly Dictionary<string, CovenantInfo> _covenants;

    public AutoPlanner(LineupPlanner planner, LineupRoutePlanner routePlanner, IEnumerable<Operator> operators)
    {
        _planner = planner;
        _routePlanner = routePlanner;
        _operators = [.. operators.Where(o => o.RequiredStrategyId.Length == 0)];
        _byName = _operators.GroupBy(o => o.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _covenants = planner.Covenants.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    // ── Candidates: one 本家 each ───────────────────────────────────────────

    public AutoPlanResult Plan(AutoPlanRequest request, CancellationToken cancellationToken = default)
    {
        var pool = _operators.Where(o => o.IsAvailableForPlanning)
            .Where(o => !request.BannedOperators.Contains(o.Name) && !CovenantBanRules.HasMissingCovenant(o, request.MissingCovenants))
            .Where(o => !request.ExcludeMixedBannedOperators || !_planner.IsExcludedByBans(o, request.BannedCovenants))
            .ToList();
        var cores = _planner.Covenants.Where(c => c.BondType == "SEASON" && !request.BannedCovenants.Contains(c.Name)).ToList();
        // Each 本家 is an independent search over read-only data.
        var built = new (AutoPlanCandidate? Candidate, string? Error, bool Limited)[cores.Count];
        Parallel.For(0, cores.Count, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 2 },
            i => built[i] = BuildCandidate(cores[i], pool, request, cancellationToken));

        var candidates = built.Where(b => b.Candidate is not null).Select(b => b.Candidate!)
            .OrderByDescending(c => c.Score).ThenBy(c => c.MainCovenant, StringComparer.Ordinal).ToList();
        // An error is the same for every 本家 (a locked operator that is banned, too many locked operators).
        return new AutoPlanResult { Candidates = candidates, Error = candidates.Count == 0 ? built.FirstOrDefault(b => b.Error is not null).Error : null, SearchLimited = built.Any(b => b.Limited) };
    }

    private (AutoPlanCandidate? Candidate, string? Error, bool Limited) BuildCandidate(CovenantInfo covenant, List<Operator> pool,
        AutoPlanRequest request, CancellationToken cancellationToken)
    {
        bool limited = false;
        var name = covenant.Name;
        var transformOrder = PlannerStrategies.TransformOptions(request.Strategy).Order().ToArray();

        // Ⅱ with as few transformations as possible, then Ⅰ.
        PlannedLineup? final = null;
        PlannerGoal? goal = null;
        foreach (var level in new[] { 2, 1 })
        {
            var levelGoal = LineupPlanner.CreateGoal(covenant, level);
            if (goal is not null && levelGoal.Count == goal.Count) break; // no Ⅱ: Ⅰ was just tried
            goal = levelGoal;
            foreach (var transforms in transformOrder)
            {
                var result = _planner.Plan(new PlannerRequest
                {
                    Goals = [levelGoal],
                    BannedCovenants = request.BannedCovenants,
                    MissingCovenants = request.MissingCovenants,
                    BannedOperators = request.BannedOperators,
                    LockedOperators = request.LockedOperators,
                    TransformAllowed = request.TransformAllowed,
                    ExcludeMixedBannedOperators = request.ExcludeMixedBannedOperators,
                    PeopleOptions = [request.People],
                    TransformOptions = [transforms],
                    Strategy = request.Strategy,
                    MechElite = request.MechElite
                }, cancellationToken, maxResultsPerVariant: FinalChoices);
                limited |= result.Variants.Any(v => v.SearchLimited);
                if (result.Error is not null) return (null, result.Error, limited);
                final = result.AllLineups.Where(l => l.ForcedTransforms == 0)
                    .OrderBy(l => l.MissingHealer).ThenBy(l => l.FreeSlots)
                    .ThenByDescending(l => l.AdditionalScore)
                    // Same 小盟约: the higher-tier lineup is the stronger boss lineup.
                    .ThenByDescending(l => l.Operators.Sum(o => _byName.TryGetValue(o.Name, out var op) ? op.Tier : 0))
                    .FirstOrDefault();
                if (final is not null) break;
            }
            if (final is not null) break;
        }
        if (final is null || goal is null) return (null, null, limited);

        var finalCustom = final.Operators.Select(o => _byName.GetValueOrDefault(o.Name)).OfType<Operator>()
            .Where(o => o.IsCustom).ToHashSet();
        IEnumerable<Operator> WithinCarryLimit(IEnumerable<Operator> ordered)
        {
            var places = new[] { 2 - finalCustom.Count(o => o.Tier == 5), 2 - finalCustom.Count(o => o.Tier == 6) };
            foreach (var op in ordered)
            {
                if (op.IsCustom && !finalCustom.Contains(op))
                {
                    if (places[op.Tier - 5] == 0) continue;
                    places[op.Tier - 5]--;
                }
                yield return op;
            }
        }
        var holders = WithinCarryLimit(pool.Where(o => Holds(o, name)).OrderBy(o => o.Tier)).ToList();
        var stacking = pool.Select(o => (Op: o, Layers: LayersOn(o, name))).Where(x => x.Layers > 0)
            .OrderBy(x => x.Op.Tier).ThenByDescending(x => x.Layers).ThenBy(x => x.Op.Name, StringComparer.Ordinal).ToList();
        var availableStackers = WithinCarryLimit(stacking.Select(x => x.Op)).ToHashSet();
        var stackers = stacking.Where(x => availableStackers.Contains(x.Op))
            .Select(x => new AutoPlanStacker(x.Op.Name, x.Op.Tier, x.Op.IconPath, x.Layers)).ToList();
        IEnumerable<AutoPlanStacker> Best(int maxTier) => stackers.Where(s => s.Tier <= maxTier)
            .OrderByDescending(s => s.LayersPerRound).ThenBy(s => s.Tier).Take(FieldedStackers);
        var early = Best(StackerLevel - 1).ToList();
        var late = Best(TerminalLevelTracker.MaxLevel).ToList();
        var earlyLayers = early.Sum(s => s.LayersPerRound);
        var lateLayers = late.Sum(s => s.LayersPerRound);
        // Pith's 调和 piece counts as one more holder of every core covenant that is present.
        var need = Math.Max(1, (covenant.ActivateCount ?? 3) - (request.Strategy == PlannerStrategy.Pith ? 1 : 0));
        var activation = Enumerable.Range(1, TerminalLevelTracker.MaxLevel).FirstOrDefault(l => holders.Count(o => o.Tier <= l) >= need);

        var (style, reason) = RecommendStyle(name, need, activation, early, late, earlyLayers, lateLayers);

        var smalls = final.AdditionalCovenants
            .Where(kv => _covenants.ContainsKey(kv.Key))
            .Select(kv => LineupPlanner.CreateGoal(_covenants[kv.Key], kv.Value >= 3 ? 2 : 1))
            .OrderByDescending(g => g.Level).ThenBy(g => g.Name, StringComparer.Ordinal).ToList();

        var notes = new List<string>();
        if (limited) notes.Add("搜索受限，仅展示当前已找到的候选");
        if (!limited && goal.Level < 2 && LineupPlanner.HasSecondLevel(covenant)) notes.Add($"{name} 只凑得到 {goal.Count} 人");
        if (final.Transforms - final.ForcedTransforms > 0) notes.Add($"需要 {final.Transforms - final.ForcedTransforms} 次转职");
        if (final.MissingHealer) notes.Add("成型阵容阵容没有奶位");
        if (final.FreeSlots > 0) notes.Add($"可用干员不够，空 {final.FreeSlots} 个位置");

        var score = (goal.Level == 2 ? 40 : 0) + 10 * final.AdditionalScore + lateLayers + 0.5 * earlyLayers
                    - 6 * final.Transforms - (final.MissingHealer ? 25 : 0) - 10 * final.FreeSlots;
        return (new AutoPlanCandidate
        {
            MainCovenant = name, Final = final, MainGoal = goal, SmallGoals = smalls, Goals = [goal, .. smalls],
            Stackers = stackers, EarlyLayers = Math.Round(earlyLayers, 1), LateLayers = Math.Round(lateLayers, 1),
            ActivationLevel = activation, Holders = holders.Count,
            RecommendedStyle = style, StyleReason = reason, Score = score, Notes = notes,
            RouteComparison = PlannerReplayEvidence.Compare(name, final)
        }, null, limited);
    }

    private static (AutoPlanStyle Style, string Reason) RecommendStyle(string name, int need, int activation,
        List<AutoPlanStacker> early, List<AutoPlanStacker> late, double earlyLayers, double lateLayers)
    {
        static string Names(IEnumerable<AutoPlanStacker> stackers) => string.Join("、", stackers.Select(s => s.Name));
        // Too few low-tier holders: the 本家 cannot even be activated in the early game.
        if (activation is 0 or > 3)
            return (AutoPlanStyle.SmallFirst, activation == 0
                ? $"本局凑不齐 {need} 名原生{name}干员，前期只能靠小盟约"
                : $"低阶{name}干员不够，{activation} 本才凑得齐 {need} 人：前期先靠小盟约过渡");
        if (lateLayers <= 0)
            return activation <= 2
                ? (AutoPlanStyle.CoreFirst, $"{name}没有专属叠层手，二本就能凑齐 {need} 人：先激活本家")
                : (AutoPlanStyle.SmallFirst, $"{name}没有专属叠层手，三本才凑得齐 {need} 人：前期先靠小盟约过渡");
        if (earlyLayers >= EarlyShareForCoreFirst * lateLayers)
            return (AutoPlanStyle.CoreFirst,
                $"四本前就能给{name}每回合叠约 {earlyLayers:0.#} 层（{Names(early)}），开局先激活本家一路叠");
        var lateOnly = late.Where(s => s.Tier >= StackerLevel).ToList();
        return (AutoPlanStyle.SmallFirst, lateOnly.Count > 0
            ? $"{name}的叠层主力在五本以后（{Names(lateOnly)}），四本前每回合只有约 {earlyLayers:0.#} 层：前期先靠小盟约过渡"
            : $"四本前每回合只能给{name}叠约 {earlyLayers:0.#} 层：前期先靠小盟约过渡");
    }

    private static bool Holds(Operator op, string covenant)
        => op.CoreCovenants.Contains(covenant) || op.AdditionalCovenants.Contains(covenant);

    /// <summary>
    /// Layers per round an operator adds to <paramref name="covenant"/> itself (normal version). Generic stackers
    /// (相邻 / 给相邻) work for whatever stands next to them and amplifiers need a stacker, so neither counts here.
    /// </summary>
    private static double LayersOn(Operator op, string covenant)
    {
        if (op.Profile?.Stack is not { } stack) return 0;
        // 乌萨斯 temporary layers vanish unless converted.
        var perRound = stack.PerRound * (stack.Temporary ? 0.5 : 1);
        return stack.Kind switch
        {
            "covenant" => stack.Covenants.Contains(covenant) ? perRound : 0,
            "self" => Holds(op, covenant) ? perRound : 0,
            _ => 0,
        };
    }

    // ── One candidate, one style: stage lineups + advice ────────────────────

    /// <summary>
    /// 开局 (二本) → 前期 (三本) → 中期 (四本) → 成型 (五本) → 后期 (六本叠层); 成型阵容 is the candidate's lineup.
    /// 先小后大 gives the stages below 五本 the 小盟约 as goals; 先大后小 builds the 本家 from the opening.
    /// </summary>
    public static IReadOnlyList<RouteStageDefinition> Stages(AutoPlanStyle style, AutoPlanCandidate candidate)
    {
        var small = style == AutoPlanStyle.SmallFirst ? candidate.SmallGoals : null;
        IReadOnlyList<PlannerGoal> main = [candidate.MainGoal];
        // 先大后小 pushes the 本家 as far as each shop allows; 先小后大 only asks for it once the stackers are there.
        IReadOnlyList<PlannerGoal> early = style == AutoPlanStyle.CoreFirst ? main : [];
        return RouteStageDefinition.Defaults.Select(d => d.Stacking ? d : d with
        {
            SmallGoals = d.TerminalLevel < StackerLevel ? small : null,
            Required = d.TerminalLevel < StackerLevel ? early : main
        }).ToList();
    }

    public AutoPlanRoute PlanRoute(AutoPlanCandidate candidate, AutoPlanStyle style, AutoPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        var route = _routePlanner.Plan(new RouteRequest
        {
            Final = candidate.Final,
            Goals = candidate.Goals,
            BannedCovenants = request.BannedCovenants,
            BannedOperators = request.BannedOperators,
            ExcludeMixedBannedOperators = request.ExcludeMixedBannedOperators,
            Stages = Stages(style, candidate)
        }, cancellationToken);
        return new AutoPlanRoute { Candidate = candidate, Style = style, Route = route,
            Advice = Advise(route, style, candidate, new(0, null, null)) };
    }

    private static string StageTitle(RouteStage stage) => stage.IsFinal ? "成型阵容" : stage.Definition.Name;

    public IReadOnlyList<AutoPlanStageAdvice> Advise(PlannedRoute route, AutoPlanStyle style,
        AutoPlanCandidate candidate, AutoPlanRunState state)
    {
        var advice = new List<AutoPlanStageAdvice>();
        var endgame = route.Stages.Where(s => s.IsFinal || s.Definition.Stacking).SelectMany(s => s.Operators)
            .DistinctBy(o => o.Name).ToList();
        foreach (var (stage, index) in route.Stages.Select((stage, index) => (stage, index)))
        {
            if (stage.IsFinal)
            {
                var focus = "成型阵容：打 Boss 前核对阵容、盟约及站位";
                var purchase = string.Join("\n", stage.Operators.GroupBy(o => state.Owned?.Contains(o.Name) == true)
                    .Select(g => $"{(g.Key ? "已记录获取" : "未记录获取")}：{string.Join("、", g.Select(o => o.Name))}"));
                advice.Add(new(AutoPlanPace.Max, focus, "")
                {
                    Levels = [new(0, AutoPlanPace.Max, focus, purchase,
                        "按成型阵容补缺口，核对精锐与转职；是否升级按剩余局势决定，不要求买齐过渡人选",
                        "持有记录不代表实际上场，先确认关键功能位与目标盟约生效", [], []) { Title = "Boss 前核对" }]
                });
                continue;
            }
            var onField = stage.Operators.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
            var later = route.Stages.Skip(index + 1).SelectMany(s => s.Operators.Select(o => (Op: o, Stage: s)))
                .DistinctBy(x => x.Op.Name).ToList();
            var next = route.Stages.Skip(index + 1).FirstOrDefault(s => !s.IsFinal)?.Definition;
            var levels = new List<AutoPlanLevelAdvice>();
            // 五本 is an operating decision within 中期, not another persisted lineup or another search.
            var lastLevel = next is null ? stage.Definition.TerminalLevel : next.TerminalLevel - 1;
            for (var level = stage.Definition.TerminalLevel; level <= lastLevel; level++)
            {
                var team = (level == stage.Definition.TerminalLevel ? stage.Operators
                    : endgame.Where(o => o.Tier <= level).Concat(stage.Operators).DistinctBy(o => o.Name))
                    .Where(o => o.Tier <= level).ToList();
                var goals = stage.Definition.SmallGoals is not null && level < StackerLevel
                    ? candidate.SmallGoals : stage.Definition.Stacking ? candidate.Goals : [candidate.MainGoal];
                var owned = state.Owned;
                var priorities = new List<(RouteOperator Op, string Reason, bool Key)>();
                foreach (var op in team)
                {
                    var reasons = new List<string>();
                    foreach (var goal in goals.Where(g => op.Covenants.Contains(g.Name)))
                    {
                        var needed = Math.Max(1, (_covenants[goal.Name].ActivateCount ?? 3)
                            - (candidate.Final.HasPith && _covenants[goal.Name].BondType == "SEASON" ? 1 : 0));
                        var have = team.Count(o => owned?.Contains(o.Name) == true && o.Covenants.Contains(goal.Name));
                        if (have < needed) reasons.Add($"补{goal.Name}激活人选");
                    }
                    if (op.IsTank && !team.Any(o => o.IsTank && owned?.Contains(o.Name) == true)) reasons.Add("补盾位");
                    if (level >= LineupRoutePlanner.HealerFromTerminal && op.IsHealer
                        && !team.Any(o => o.IsHealer && owned?.Contains(o.Name) == true)) reasons.Add("补奶位");
                    if (candidate.Stackers.Any(s => s.Name == op.Name)
                        && !candidate.Stackers.Any(s => s.Tier <= level && owned?.Contains(s.Name) == true)) reasons.Add("启动本家叠层");
                    priorities.Add((op, reasons.Count > 0 ? string.Join("、", reasons) : "顺手补强", reasons.Count > 0));
                }
                var keys = priorities.Where(p => p.Key && owned?.Contains(p.Op.Name) != true).Select(p => p.Op.Name).ToList();
                var pace = level == TerminalLevelTracker.MaxLevel ? AutoPlanPace.Max
                    : style == AutoPlanStyle.SmallFirst && level < StackerLevel ? AutoPlanPace.Rush
                    : keys.Count > 0 ? AutoPlanPace.Stay : AutoPlanPace.Steady;
                var purchase = string.Join("\n", priorities.OrderByDescending(p => p.Key)
                    .ThenBy(p => owned?.Contains(p.Op.Name) == true).ThenBy(p => p.Op.Tier)
                    .GroupBy(p => (p.Reason, Owned: owned?.Contains(p.Op.Name) == true))
                    .Select(g => $"{g.Key.Reason} · {(g.Key.Owned ? "已记录获取" : "未记录获取")}：{string.Join("、", g.Select(p => p.Op.Name))}"));
                var currentNames = level == stage.Definition.TerminalLevel ? onField : team.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
                var reserve = later.Where(x => !currentNames.Contains(x.Op.Name) && x.Op.Tier <= level
                        && owned?.Contains(x.Op.Name) != true)
                    .OrderByDescending(x => x.Op.Role == RouteOperatorRole.Final).Take(MaxReserve)
                    .Select(x => new AutoPlanPick(x.Op.Name, x.Op.Tier, x.Op.IconPath,
                        x.Op.Role == RouteOperatorRole.Final, StageTitle(x.Stage))).ToList();
                var focus = level == StackerLevel
                    ? $"五本：补{candidate.MainCovenant}激活人选和本家叠层手；有六阶需求时再准备升六本"
                    : Focus(stage, style, candidate);
                var upgrade = pace switch
                {
                    AutoPlanPace.Max => "满本：核对上场盟约，按缺口补叠层与成型阵容；同作用人选不必全部买齐",
                    AutoPlanPace.Rush => $"若当前阵容能稳住战斗，准备升 {level + 1} 本；本家的卡见到就留，不为凑齐理想阵容反复刷新",
                    AutoPlanPace.Stay => $"若关键作用尚缺，优先补 {string.Join("、", keys)}；同作用人选可替代，不必全部买齐。作用齐备且能稳住战斗后升 {level + 1} 本",
                    _ => $"本级计划关键候选均已记录获取；仍须核对实际上场、功能位与盟约。能稳住战斗后升 {level + 1} 本，本级无法补齐的目标留待升本，其他增强牌顺手拿"
                };
                if (level == StackerLevel && !endgame.Any(o => o.Tier == TerminalLevelTracker.MaxLevel)
                    && !candidate.Stackers.Any(s => s.Tier == TerminalLevelTracker.MaxLevel))
                    upgrade = "本路线没有明确六阶需求，先保留资金补关键作用与叠层；关键作用齐备后，是否升六本按局势决定";
                var economy = "资金与升级费待确认，仅显示参考条件";
                if (state.TerminalLevel == level && state.Economy is { } hud)
                {
                    economy = level == TerminalLevelTracker.MaxLevel
                        ? hud.Money is { } funds ? $"商店确认记录：资金 {funds}；终端已满级，按缺口保留搜牌与补强资金" : "终端已满级，资金待确认"
                        : hud.UpgradeMaxed ? "商店记录已满级，请核对终端等级"
                        : hud.Money is { } money && hud.UpgradeCost is { } cost
                            ? money >= cost ? $"商店确认记录：资金 {money}，升级费 {cost}，升级后剩 {money - cost}；付得起不代表适合升级"
                                : $"商店确认记录：资金 {money}，升级费 {cost}，还差 {cost - money}；暂缓升级，买牌仍需另留资金"
                            : "当前资金或升级费未知，暂不判断能否支付";
                }
                if (owned is null) purchase = "持有记录未连接；以下为计划人选：" + purchase;
                levels.Add(new(level, pace, focus, purchase, upgrade, economy, reserve, keys));
            }
            var first = levels[0];
            advice.Add(new(first.Pace, first.Focus, first.UpgradeText)
            {
                NextLevel = next is null ? 0 : first.TerminalLevel + 1,
                Reserve = first.Reserve, KeyCards = first.KeyCards, Levels = levels
            });
        }
        return advice;
    }

    /// <summary>“坚守 3、不屈 2”: the goal covenants this stage has active.</summary>
    private static string ActiveGoals(RouteStage stage, Func<RouteCovenantStatus, bool>? filter = null)
        => string.Join("、", stage.Covenants.Where(c => c.IsGoal && c.Level > 0 && (filter?.Invoke(c) ?? true)).Select(c => $"{c.Name} {c.Count}"));

    private static string Focus(RouteStage stage, AutoPlanStyle style, AutoPlanCandidate candidate)
    {
        var def = stage.Definition;
        var main = candidate.MainCovenant;
        var mainCount = stage.Covenants.FirstOrDefault(c => c.Name == main)?.Count ?? 0;
        var mainText = $"{main} {mainCount}/{candidate.MainGoal.Count}";
        if (def.Stacking)
            return stage.Summary.FinalLayers > 0
                ? $"叠层：成型阵容盟约保持激活，每回合约叠 {stage.Summary.FinalLayers:0.#} 层"
                : "叠层：成型阵容盟约保持激活，尽量多上叠层手";
        var smalls = ActiveGoals(stage, c => !c.IsCore);
        if (def.SmallGoals is not null)
            return (smalls.Length > 0 ? $"小盟约过渡：{smalls}" : "小盟约过渡") + $"；本家 {mainText}，不专门搜";
        if (style == AutoPlanStyle.SmallFirst)
            return $"本家成型：{mainText}，五阶叠层手上场";
        return def.TerminalLevel >= StackerLevel
            ? $"补小盟约、换高阶卡：{mainText}" + (smalls.Length > 0 ? $"，{smalls}" : "")
            : $"凑本家：{mainText}" + (stage.Summary.FinalLayers > 0 ? $"，每回合约叠 {stage.Summary.FinalLayers:0.#} 层" : "");
    }

}
