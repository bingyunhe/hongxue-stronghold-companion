using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Services;

/// <summary>
/// 自动规划: proposes one plan per core covenant that is available this run, and plays each of them one of two ways.
/// <para>
/// <see cref="Plan"/> finds, for every 本家 (main core covenant), the 大成 lineup with the best 小盟约 and says
/// where its stackers sit: a 本家 that already stacks well below 五本 suits 先大后小, one whose stackers only show
/// up at 五本 and above suits 先小后大. The player picks the 本家 and the style.
/// </para>
/// <para>
/// <see cref="PlanRoute"/> then derives the stage lineups with <see cref="LineupRoutePlanner"/> (先小后大 gives the
/// stages below 五本 the 小盟约 as their goals) and adds, per stage, what to buy ahead (抓位) and when to upgrade
/// the terminal. Upgrade advice is a condition plus the round it falls in: a run has 14 rounds (15 with the
/// hidden round), and <see cref="AutoPlanPacing"/> derives the round of every upgrade from the funds a round
/// hands out against the upgrade price, which drops every round; the pace of a stage holds its level longer.
/// Operator and refresh prices are not modelled, so what is left after an upgrade is shown, not planned with.
/// </para>
/// </summary>
public sealed class AutoPlanner
{
    /// <summary>Terminal level whose shop first offers the big 本家 stackers (tier Ⅴ).</summary>
    public const int StackerLevel = 5;
    /// <summary>Stackers of one covenant a lineup realistically fields at once.</summary>
    private const int FieldedStackers = 3;
    /// <summary>先大后小 is recommended when the stackers below 五本 reach this share of the full set.</summary>
    private const double EarlyShareForCoreFirst = 0.5;
    /// <summary>This many endgame cards first offered at a level make it worth staying there to roll.</summary>
    private const int StayKeyCards = 3;
    private const int MaxReserve = 6;
    /// <summary>大成 lineups compared per 本家 (the planner returns the first ones it finds, ranked by 小盟约).</summary>
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
        var pool = _operators.Where(o => o.IsAvailableForSelection)
            .Where(o => !request.BannedOperators.Contains(o.Name))
            .Where(o => !request.ExcludeMixedBannedOperators || !_planner.IsExcludedByBans(o, request.BannedCovenants))
            .ToList();
        var cores = _planner.Covenants.Where(c => c.BondType == "SEASON" && !request.BannedCovenants.Contains(c.Name)).ToList();
        // Each 本家 is an independent search over read-only data.
        var built = new (AutoPlanCandidate? Candidate, string? Error)[cores.Count];
        Parallel.For(0, cores.Count, new ParallelOptions { CancellationToken = cancellationToken },
            i => built[i] = BuildCandidate(cores[i], pool, request, cancellationToken));

        var candidates = built.Where(b => b.Candidate is not null).Select(b => b.Candidate!)
            .OrderByDescending(c => c.Score).ThenBy(c => c.MainCovenant, StringComparer.Ordinal).ToList();
        // An error is the same for every 本家 (a locked operator that is banned, too many locked operators).
        return new AutoPlanResult { Candidates = candidates, Error = candidates.Count == 0 ? built.FirstOrDefault(b => b.Error is not null).Error : null };
    }

    private (AutoPlanCandidate? Candidate, string? Error) BuildCandidate(CovenantInfo covenant, List<Operator> pool,
        AutoPlanRequest request, CancellationToken cancellationToken)
    {
        var name = covenant.Name;
        // 暴躁铁皮 carries a transformation anyway and 变形同构 hands out the 同构体: both plan one from the start.
        var free = request.Strategy is PlannerStrategy.Mech or PlannerStrategy.Shapeshifter ? 1 : 0;
        var transformOrder = PlannerStrategies.TransformOptions(request.Strategy).OrderBy(t => t == free ? 0 : 1).ThenBy(t => t).ToList();

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
                    BannedOperators = request.BannedOperators,
                    LockedOperators = request.LockedOperators,
                    TransformAllowed = request.TransformAllowed,
                    ExcludeMixedBannedOperators = request.ExcludeMixedBannedOperators,
                    PeopleOptions = [request.People],
                    TransformOptions = [transforms],
                    Strategy = request.Strategy,
                    MechElite = request.MechElite
                }, cancellationToken, maxResultsPerVariant: FinalChoices);
                if (result.Error is not null) return (null, result.Error);
                final = result.AllLineups
                    .OrderBy(l => l.MissingHealer).ThenBy(l => l.FreeSlots)
                    .ThenByDescending(l => l.AdditionalScore)
                    // Same 小盟约: the higher-tier lineup is the stronger boss lineup.
                    .ThenByDescending(l => l.Operators.Sum(o => _byName.TryGetValue(o.Name, out var op) ? op.Tier : 0))
                    .ThenBy(l => l.ForcedTransforms)
                    .FirstOrDefault();
                if (final is not null) break;
            }
            if (final is not null) break;
        }
        if (final is null || goal is null) return (null, null);

        var holders = pool.Where(o => Holds(o, name)).ToList();
        var stackers = pool.Select(o => (Op: o, Layers: LayersOn(o, name))).Where(x => x.Layers > 0)
            .OrderBy(x => x.Op.Tier).ThenByDescending(x => x.Layers).ThenBy(x => x.Op.Name, StringComparer.Ordinal)
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
        if (goal.Level < 2 && LineupPlanner.HasSecondLevel(covenant)) notes.Add($"{name} 只凑得到 {goal.Count} 人");
        if (final.Transforms - final.ForcedTransforms > 0) notes.Add($"需要 {final.Transforms - final.ForcedTransforms} 次转职");
        if (final.MissingHealer) notes.Add("大成阵容没有奶位");
        if (final.FreeSlots > 0) notes.Add($"可用干员不够，空 {final.FreeSlots} 个位置");

        var score = (goal.Level == 2 ? 40 : 0) + 10 * final.AdditionalScore + lateLayers + 0.5 * earlyLayers
                    - 6 * Math.Max(0, final.Transforms - free) - (final.MissingHealer ? 25 : 0) - 10 * final.FreeSlots;
        return (new AutoPlanCandidate
        {
            MainCovenant = name, Final = final, MainGoal = goal, SmallGoals = smalls, Goals = [goal, .. smalls],
            Stackers = stackers, EarlyLayers = Math.Round(earlyLayers, 1), LateLayers = Math.Round(lateLayers, 1),
            ActivationLevel = activation, Holders = holders.Count,
            RecommendedStyle = style, StyleReason = reason, Score = score, Notes = notes
        }, null);
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
    /// 开局 (二本) → 前期 (三本) → 中期 (四本) → 成型 (五本) → 后期 (六本叠层); 大成 is the candidate's lineup.
    /// 先小后大 gives the stages below 五本 the 小盟约 as goals; 先大后小 builds the 本家 from the opening.
    /// </summary>
    public static IReadOnlyList<RouteStageDefinition> Stages(AutoPlanStyle style, AutoPlanCandidate candidate)
    {
        var small = style == AutoPlanStyle.SmallFirst ? candidate.SmallGoals : null;
        IReadOnlyList<PlannerGoal> main = [candidate.MainGoal];
        // 先大后小 pushes the 本家 as far as each shop allows; 先小后大 only asks for it once the stackers are there.
        IReadOnlyList<PlannerGoal> early = style == AutoPlanStyle.CoreFirst ? main : [];
        return
        [
            new("开局", 2, 2, 1, 6) { SmallGoals = small, Required = early },
            new("前期", 3, 3, 1, 8) { SmallGoals = small, Required = early },
            new("中期", 4, 4, 2, 8) { SmallGoals = small, Required = early },
            new("成型", StackerLevel, StackerLevel, 1, 8) { Required = main },
            new("后期", 6, 6, 0, 8) { Stacking = true },
        ];
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
        return new AutoPlanRoute { Candidate = candidate, Style = style, Route = route, Advice = Advise(route, style, candidate) };
    }

    private static string StageTitle(RouteStage stage) => stage.IsFinal ? "大成" : stage.Definition.Name;

    /// <summary>What a stage is about before its rounds are known: the pace decides them.</summary>
    private sealed record StageDraft(RouteStage Stage, RouteStageDefinition? Next, AutoPlanPace Pace,
        List<string> KeyCards, List<string> Unlocks, List<AutoPlanPick> Reserve);

    private static List<AutoPlanStageAdvice> Advise(PlannedRoute route, AutoPlanStyle style, AutoPlanCandidate candidate)
    {
        var stages = route.Stages;
        var main = candidate.MainCovenant;
        // What the run is heading for: the boss lineup and the stacking lineup in front of it.
        var endgame = stages.Where(s => s.IsFinal || s.Definition.Stacking).SelectMany(s => s.Operators)
            .GroupBy(o => o.Name).Select(g => g.First()).ToList();

        // First what each stage is about, then the rounds its pace leads to, then the texts.
        var drafts = new List<StageDraft>();
        var offeredBefore = 0; // highest tier the previous stage's shop offered
        var fielded = new HashSet<string>(StringComparer.Ordinal); // already on the board in an earlier stage (三连)
        for (var i = 0; i < stages.Count; i++)
        {
            var stage = stages[i];
            var def = stage.Definition;
            if (stage.IsFinal) continue;

            var onField = stage.Operators.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
            // Each operator of a later stage, with the first stage that fields it.
            var later = new List<(RouteOperator Op, int Stage)>();
            for (var j = i + 1; j < stages.Count; j++)
                foreach (var op in stages[j].Operators)
                    if (later.All(x => x.Op.Name != op.Name)) later.Add((op, j));

            // 抓位: later cards this shop already offers. Soonest first, then 大成 cards, then the rarer tiers.
            var reserve = later.Where(x => !onField.Contains(x.Op.Name) && x.Op.Tier <= def.MaxTier)
                .OrderBy(x => x.Stage).ThenByDescending(x => x.Op.Role == RouteOperatorRole.Final)
                .ThenByDescending(x => x.Op.Tier).ThenBy(x => x.Op.Name, StringComparer.Ordinal)
                .Take(MaxReserve)
                .Select(x => new AutoPlanPick(x.Op.Name, x.Op.Tier, x.Op.IconPath, x.Op.Role == RouteOperatorRole.Final, StageTitle(stages[x.Stage])))
                .ToList();

            var keyCards = endgame.Where(o => o.Tier > offeredBefore && o.Tier <= def.MaxTier && !fielded.Contains(o.Name))
                .OrderByDescending(o => o.Tier).ThenBy(o => o.Name, StringComparer.Ordinal).Select(o => o.Name).ToList();

            var next = i + 1 < stages.Count && !stages[i + 1].IsFinal && stages[i + 1].Definition.TerminalLevel > def.TerminalLevel
                ? stages[i + 1].Definition : null;
            // Cards the next level newly offers (the ones already here through 三连 are not news).
            var unlocks = next is null ? [] : later.Where(x => !onField.Contains(x.Op.Name) && x.Op.Tier > def.MaxTier && x.Op.Tier <= next.MaxTier)
                .OrderByDescending(x => x.Op.Role == RouteOperatorRole.Final).ThenByDescending(x => x.Op.Tier)
                .ThenBy(x => x.Op.Name, StringComparer.Ordinal).Select(x => x.Op.Name).ToList();

            var pace = next is null ? AutoPlanPace.Max
                // 先小后大: nothing below 五本 is worth rolling for, the 本家 stackers are not in the shop yet.
                : style == AutoPlanStyle.SmallFirst && def.TerminalLevel < StackerLevel ? AutoPlanPace.Rush
                : keyCards.Count == 0 ? AutoPlanPace.Rush
                // Tier Ⅰ / Ⅱ cards keep showing up later: not worth holding the terminal back for.
                : keyCards.Count >= StayKeyCards && def.TerminalLevel > 2 ? AutoPlanPace.Stay
                : AutoPlanPace.Steady;

            drafts.Add(new StageDraft(stage, next, pace, keyCards, unlocks, reserve));
            offeredBefore = Math.Max(offeredBefore, def.MaxTier);
            fielded.UnionWith(onField);
        }

        // Rounds from the economy: funds per round against the upgrade price, which drops every round.
        var rounds = AutoPlanPacing.Schedule([.. drafts.Select(d => d.Stage.Definition.TerminalLevel)], [.. drafts.Select(d => d.Pace)]);
        var advice = new List<AutoPlanStageAdvice>();
        var played = 0;
        foreach (var stage in stages)
        {
            if (stage.IsFinal)
            {
                advice.Add(new AutoPlanStageAdvice(AutoPlanPace.Max, "打 Boss 的阵容：进 Boss 回合前把后期（叠层）阵容换成这一套", "")
                {
                    FromRound = AutoPlanPacing.BossRound, ToRound = AutoPlanPacing.BossRound
                });
                continue;
            }
            var i = played++;
            var draft = drafts[i];
            var def = stage.Definition;
            var timing = rounds[i];
            // The first stage also covers the rounds before its level is reached; the last one runs up to the boss.
            var fromRound = i == 0 ? 1 : timing.ReachedRound;
            var toRound = i + 1 < drafts.Count ? Math.Max(fromRound, rounds[i + 1].ReachedRound - 1)
                : Math.Max(fromRound, AutoPlanPacing.BossRound - 1);
            var upgrades = draft.Next is not null && timing.UpgradeRound > 0;
            var upgrade = UpgradeText(stage, draft.Pace, draft.Next, style, main, draft.KeyCards);
            if (upgrades)
            {
                var funds = AutoPlanPacing.Funds(timing.UpgradeRound);
                upgrade = $"约第 {timing.UpgradeRound} 回合（升本 {timing.UpgradeCost} 费，资金 {funds}，剩 {funds - timing.UpgradeCost}）· {upgrade}";
            }
            // 开局 starts below its own level: say when to take that first upgrade too.
            if (i == 0 && timing.ReachedRound > 1)
                upgrade = $"第 {timing.ReachedRound} 回合先升 {def.TerminalLevel} 本"
                          + (def.TerminalLevel == 2 ? $"（{AutoPlanPacing.UpgradeCost(2, 1, timing.ReachedRound)} 费）" : "") + $"；{upgrade}";
            advice.Add(new AutoPlanStageAdvice(draft.Pace, Focus(stage, style, candidate), upgrade)
            {
                NextLevel = draft.Next?.TerminalLevel ?? 0, Unlocks = draft.Unlocks, Reserve = draft.Reserve, KeyCards = draft.KeyCards,
                FromRound = fromRound, ToRound = toRound,
                UpgradeRound = upgrades ? timing.UpgradeRound : 0, UpgradeCost = upgrades ? timing.UpgradeCost : 0
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
                ? $"叠层：大成盟约保持激活，每回合约叠 {stage.Summary.FinalLayers:0.#} 层"
                : "叠层：大成盟约保持激活，尽量多上叠层手";
        var smalls = ActiveGoals(stage, c => !c.IsCore);
        if (def.SmallGoals is not null)
            return (smalls.Length > 0 ? $"小盟约过渡：{smalls}" : "小盟约过渡") + $"；本家 {mainText}，不专门搜";
        if (style == AutoPlanStyle.SmallFirst)
            return $"本家成型：{mainText}，五阶叠层手上场";
        return def.TerminalLevel >= StackerLevel
            ? $"补小盟约、换高阶卡：{mainText}" + (smalls.Length > 0 ? $"，{smalls}" : "")
            : $"凑本家：{mainText}" + (stage.Summary.FinalLayers > 0 ? $"，每回合约叠 {stage.Summary.FinalLayers:0.#} 层" : "");
    }

    private static string UpgradeText(RouteStage stage, AutoPlanPace pace, RouteStageDefinition? next, AutoPlanStyle style,
        string main, List<string> keyCards)
    {
        if (next is null) return "满本：不再升级，资金用来搜叠层手和大成卡";
        var active = ActiveGoals(stage);
        var ready = active.Length > 0 ? $"激活 {active}" : $"站满 {stage.Definition.People} 人";
        var level = next.TerminalLevel;
        var keys = string.Join("、", keyCards);
        return pace switch
        {
            AutoPlanPace.Rush when style == AutoPlanStyle.SmallFirst && stage.Definition.SmallGoals is not null
                => $"{ready}，血量稳住就升 {level} 本；{main}的卡见到就留，不为它刷新",
            AutoPlanPace.Rush => $"{ready}，然后直接升 {level} 本：这一档没有大成 / 叠层要用的卡，不为搜牌停留",
            AutoPlanPace.Stay => $"{keys} 从这一档开始出现：{ready}，并搜到其中大部分再升 {level} 本",
            _ => $"{ready}，然后升 {level} 本" + (keyCards.Count > 0 ? $"；{keys} 顺手拿，不强求" : ""),
        };
    }
}
