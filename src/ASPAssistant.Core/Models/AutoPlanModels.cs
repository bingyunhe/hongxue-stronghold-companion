using ASPAssistant.Core.Services;

namespace ASPAssistant.Core.Models;

/// <summary>The order an auto plan builds the 本家 (main core covenant) and the 小盟约 (additional covenants) in.</summary>
public enum AutoPlanStyle
{
    /// <summary>先小后大: 小盟约 carry the early game; the 本家 comes together at 五本, where its stackers appear.</summary>
    SmallFirst,
    /// <summary>先大后小: the 本家 is activated from the opening and stacked all run; 小盟约 are filled in later.</summary>
    CoreFirst,
}

/// <summary>How long to stay at a terminal level before upgrading.</summary>
public enum AutoPlanPace
{
    /// <summary>速升: nothing of this level is needed for the endgame.</summary>
    Rush,
    /// <summary>稳升: upgrade once the stage lineup stands.</summary>
    Steady,
    /// <summary>停留搜牌: several endgame cards are first offered at this level.</summary>
    Stay,
    /// <summary>满本: the terminal is not upgraded any further.</summary>
    Max,
}

public static class AutoPlanStyles
{
    public static string Name(AutoPlanStyle style) => style == AutoPlanStyle.SmallFirst ? "先小后大" : "先大后小";

    public static string Description(AutoPlanStyle style) => style == AutoPlanStyle.SmallFirst
        ? "前期靠小盟约过渡并速升五本，本家叠层手到手后再凑大盟约叠层"
        : "开局先激活本家并一路叠层，阵容成型后再补小盟约";

    public static string PaceName(AutoPlanPace pace) => pace switch
    {
        AutoPlanPace.Rush => "速升",
        AutoPlanPace.Stay => "停留搜牌",
        AutoPlanPace.Max => "满本",
        _ => "稳升",
    };
}

public sealed class AutoPlanRequest
{
    public IReadOnlySet<string> MissingCovenants { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> BannedCovenants { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> BannedOperators { get; init; } = new HashSet<string>();
    /// <summary>Operators every plan has to contain (already on the board).</summary>
    public IReadOnlySet<string> LockedOperators { get; init; } = new HashSet<string>();
    /// <summary>Covenants an operator may be transformed into (see <c>LineupPlanner.DefaultTransformAllowed</c>).</summary>
    public IReadOnlySet<string> TransformAllowed { get; init; } = new HashSet<string>();
    public bool ExcludeMixedBannedOperators { get; init; } = true;
    /// <summary>People in the 成型阵容 lineup (8, or 9 with the extra place).</summary>
    public int People { get; init; } = 8;
    public PlannerStrategy Strategy { get; init; } = PlannerStrategy.Other;
    public bool MechElite { get; init; }
}

/// <summary>An operator that stacks layers on the plan's 本家 every round (normal version, estimated).</summary>
public sealed record AutoPlanStacker(string Name, int Tier, string IconPath, double LayersPerRound);

/// <summary>One 本家 the run could be built around, with the 成型阵容 lineup found for it.</summary>
public sealed class AutoPlanCandidate
{
    internal PlannerReplayEvidence.Comparison? RouteComparison { get; init; }
    public required string MainCovenant { get; init; }
    /// <summary>The 成型阵容 lineup: the 本家 at <see cref="MainGoal"/> plus the best 小盟约 the free places allow.</summary>
    public required PlannedLineup Final { get; init; }
    public required PlannerGoal MainGoal { get; init; }
    /// <summary>小盟约 the 成型阵容 lineup activates, as goals (Ⅱ of the three-person ones first).</summary>
    public IReadOnlyList<PlannerGoal> SmallGoals { get; init; } = [];
    /// <summary><see cref="MainGoal"/> followed by <see cref="SmallGoals"/>: the goals of the route.</summary>
    public IReadOnlyList<PlannerGoal> Goals { get; init; } = [];
    /// <summary>Operators that stack the 本家 itself, low tier first (generic 相邻 stackers are not listed).</summary>
    public IReadOnlyList<AutoPlanStacker> Stackers { get; init; } = [];
    /// <summary>Layers per round on the 本家 from the best stackers a four-terminal can offer (tier ≤ Ⅳ).</summary>
    public double EarlyLayers { get; init; }
    /// <summary>Layers per round on the 本家 from the best stackers of any tier.</summary>
    public double LateLayers { get; init; }
    /// <summary>Lowest terminal level whose shop offers enough holders to activate the 本家 (0 = none).</summary>
    public int ActivationLevel { get; init; }
    /// <summary>Holders of the 本家 that are available this run.</summary>
    public int Holders { get; init; }
    public AutoPlanStyle RecommendedStyle { get; init; }
    public string StyleReason { get; init; } = "";
    /// <summary>Ranking among the candidates (higher first); only meaningful relative to each other.</summary>
    public double Score { get; init; }
    /// <summary>Caveats: needs a transformation, only reaches Ⅰ, no 奶位, …</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed class AutoPlanResult
{
    public IReadOnlyList<AutoPlanCandidate> Candidates { get; init; } = [];
    public string? Error { get; init; }
    public bool SearchLimited { get; init; }
}

/// <summary>A card to buy ahead of the stage that fields it (抓位).</summary>
public sealed record AutoPlanPick(string Name, int Tier, string IconPath, bool IsFinal, string NeededAt);

/// <summary>Confirmed HUD state and recorded ownership; null ownership means no pool has been connected.</summary>
public sealed record AutoPlanRunState(int TerminalLevel, ShopEconomy? Economy, IReadOnlySet<string>? Owned);

/// <summary>A terminal-level decision inside a saved route stage (中期 contains both 四本 and 五本).</summary>
public sealed record AutoPlanLevelAdvice(int TerminalLevel, AutoPlanPace Pace, string Focus,
    string PurchaseText, string UpgradeText, string EconomyText, IReadOnlyList<AutoPlanPick> Reserve,
    IReadOnlyList<string> KeyCards)
{
    public string Title { get; init; } = $"{TerminalLevel} 本 · {AutoPlanStyles.PaceName(Pace)}";
    public bool HasReserve => Reserve.Count > 0;
}

/// <summary>What to do in one stage of an auto plan besides fielding its lineup.</summary>
public sealed record AutoPlanStageAdvice(AutoPlanPace Pace, string Focus, string UpgradeText)
{
    public IReadOnlyList<AutoPlanLevelAdvice> Levels { get; init; } = [];
    /// <summary>Terminal level the advice upgrades to (0 = no further upgrade).</summary>
    public int NextLevel { get; init; }
    /// <summary>Cards of later stages the current shop already offers: buy and bench them when they show up.</summary>
    public IReadOnlyList<AutoPlanPick> Reserve { get; init; } = [];
    /// <summary>Endgame cards (成型阵容 / 后期 叠层) first offered at this terminal level: the reason to stay and roll.</summary>
    public IReadOnlyList<string> KeyCards { get; init; } = [];
}

/// <summary>A candidate played one way: the stage lineups plus the advice of each stage.</summary>
public sealed class AutoPlanRoute
{
    public required AutoPlanCandidate Candidate { get; init; }
    public AutoPlanStyle Style { get; init; }
    public required PlannedRoute Route { get; init; }
    /// <summary>One entry per <see cref="PlannedRoute.Stages"/> entry, in the same order.</summary>
    public IReadOnlyList<AutoPlanStageAdvice> Advice { get; init; } = [];
}
