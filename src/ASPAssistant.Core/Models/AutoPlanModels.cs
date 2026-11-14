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

/// <summary>When a stage's terminal level is reached, and when and for how much it is left for the next one.</summary>
/// <param name="ReachedRound">Round in which the stage's terminal level is reached.</param>
/// <param name="UpgradeRound">Round of the upgrade to the next level (0 = the last stage).</param>
/// <param name="UpgradeCost">Funds that upgrade costs in that round.</param>
public readonly record struct AutoPlanStageRounds(int ReachedRound, int UpgradeRound, int UpgradeCost);

/// <summary>
/// The economy of a run as far as it decides when the terminal is upgraded, used to turn the pace of each stage
/// into round numbers.
/// <para>
/// A run has 14 rounds, the final leader comes in round 14, and a run that meets the conditions goes on to the
/// hidden 隐秘核心 round 15. A round hands out 4 funds at first and one more every round up to 12; an upgrade
/// costs 5 / 8 / 11 / 12 / 13 funds for 二本 … 六本 and gets one cheaper every round it is not taken. These
/// numbers are as reported from play (the game data in this project lists none of them); items and strategy
/// effects are left out, and unspent funds are taken not to carry over (keeping them is a strategy's effect).
/// </para>
/// <para>
/// 速升 upgrades in the first round the funds cover it; 稳升 and 停留搜牌 hold the level one and two rounds
/// longer, which also leaves more of that round's funds for buying. Operator and refresh prices are not modelled.
/// </para>
/// </summary>
public static class AutoPlanPacing
{
    /// <summary>Funds of round 1, items aside (as reported from play: “大概 4 开始自增”).</summary>
    public const int FirstRoundFunds = 4;
    /// <summary>Funds stop growing here, items aside (as reported from play: “最多到 12 好像”).</summary>
    public const int MaxFunds = 12;

    /// <summary>Funds handed out in a round, items aside: 4 in round 1, one more each round, at most 12 (from round 9).</summary>
    public static int Funds(int round) => Math.Clamp(FirstRoundFunds + round - 1, FirstRoundFunds, MaxFunds);

    /// <summary>Round of the final leader: the 大成 lineup has to stand by then.</summary>
    public const int BossRound = 14;
    /// <summary>隐秘核心 round after the final leader (only when its conditions are met).</summary>
    public const int HiddenRound = 15;
    /// <summary>Rounds 后期 (六本叠层) keeps before the boss, however long the earlier levels are held.</summary>
    public const int StackingRounds = 3;

    /// <summary>Funds the upgrade to a terminal level costs when it is first offered (as reported from play).</summary>
    public static int UpgradePrice(int level) => level switch { 2 => 5, 3 => 8, 4 => 11, 5 => 12, 6 => 13, _ => 0 };

    /// <summary>
    /// Funds the upgrade to <paramref name="level"/> costs in <paramref name="round"/> when the level below was
    /// reached in <paramref name="reachedRound"/>: one cheaper for every round since.
    /// </summary>
    public static int UpgradeCost(int level, int reachedRound, int round)
        => Math.Max(0, UpgradePrice(level) - Math.Max(0, round - reachedRound));

    /// <summary>
    /// First round after <paramref name="reachedRound"/> whose funds cover the upgrade to <paramref name="level"/>
    /// (a level is played for at least one round).
    /// </summary>
    public static int EarliestUpgradeRound(int level, int reachedRound)
    {
        var round = reachedRound + 1;
        while (UpgradeCost(level, reachedRound, round) > Funds(round)) round++;
        return round;
    }

    /// <summary>Rounds a level is held beyond the first round its upgrade is affordable: 速升 0, 稳升 1, 停留搜牌 2.</summary>
    public static int Hold(AutoPlanPace pace) => pace switch { AutoPlanPace.Rush => 0, AutoPlanPace.Stay => 2, _ => 1 };

    /// <summary>
    /// Rounds of a run played through stages at the ascending terminal <paramref name="levels"/>, each left at
    /// its <paramref name="paces"/> entry (the last stage's pace is not used). The run starts at 一本 in round 1 and
    /// rushes to the first stage's level. When holding would leave the last stage fewer than
    /// <see cref="StackingRounds"/> rounds before the boss, the longest holds are cut first.
    /// </summary>
    public static IReadOnlyList<AutoPlanStageRounds> Schedule(IReadOnlyList<int> levels, IReadOnlyList<AutoPlanPace> paces)
    {
        if (levels.Count == 0) return [];
        var holds = levels.Select((_, i) => i < levels.Count - 1 && i < paces.Count ? Hold(paces[i]) : 0).ToArray();
        while (true)
        {
            var rounds = new AutoPlanStageRounds[levels.Count];
            var reached = 1;
            for (var level = 2; level <= levels[0]; level++) reached = EarliestUpgradeRound(level, reached);
            for (var i = 0; i < levels.Count; i++)
            {
                if (i == levels.Count - 1 || levels[i + 1] <= levels[i])
                {
                    rounds[i] = new AutoPlanStageRounds(reached, 0, 0);
                    continue;
                }
                var next = levels[i] + 1;
                var round = EarliestUpgradeRound(next, reached) + holds[i];
                rounds[i] = new AutoPlanStageRounds(reached, round, UpgradeCost(next, reached, round));
                reached = round;
                // A stage list that skips a level takes the levels in between as soon as they are affordable.
                for (var level = next + 1; level <= levels[i + 1]; level++) reached = EarliestUpgradeRound(level, reached);
            }
            var longest = holds.Max();
            if (longest == 0 || rounds[^1].ReachedRound <= BossRound - StackingRounds) return rounds;
            holds[Array.IndexOf(holds, longest)]--;
        }
    }
}

public sealed class AutoPlanRequest
{
    public IReadOnlySet<string> BannedCovenants { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> BannedOperators { get; init; } = new HashSet<string>();
    /// <summary>Operators every plan has to contain (already on the board).</summary>
    public IReadOnlySet<string> LockedOperators { get; init; } = new HashSet<string>();
    /// <summary>Covenants an operator may be transformed into (see <c>LineupPlanner.DefaultTransformAllowed</c>).</summary>
    public IReadOnlySet<string> TransformAllowed { get; init; } = new HashSet<string>();
    public bool ExcludeMixedBannedOperators { get; init; } = true;
    /// <summary>People in the 大成 lineup (8, or 9 with the extra place).</summary>
    public int People { get; init; } = 8;
    public PlannerStrategy Strategy { get; init; } = PlannerStrategy.Other;
    public bool MechElite { get; init; }
}

/// <summary>An operator that stacks layers on the plan's 本家 every round (normal version, estimated).</summary>
public sealed record AutoPlanStacker(string Name, int Tier, string IconPath, double LayersPerRound);

/// <summary>One 本家 the run could be built around, with the 大成 lineup found for it.</summary>
public sealed class AutoPlanCandidate
{
    public required string MainCovenant { get; init; }
    /// <summary>The 大成 lineup: the 本家 at <see cref="MainGoal"/> plus the best 小盟约 the free places allow.</summary>
    public required PlannedLineup Final { get; init; }
    public required PlannerGoal MainGoal { get; init; }
    /// <summary>小盟约 the 大成 lineup activates, as goals (Ⅱ of the three-person ones first).</summary>
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
}

/// <summary>A card to buy ahead of the stage that fields it (抓位).</summary>
public sealed record AutoPlanPick(string Name, int Tier, string IconPath, bool IsFinal, string NeededAt);

/// <summary>What to do in one stage of an auto plan besides fielding its lineup.</summary>
public sealed record AutoPlanStageAdvice(AutoPlanPace Pace, string Focus, string UpgradeText)
{
    /// <summary>Terminal level the advice upgrades to (0 = no further upgrade).</summary>
    public int NextLevel { get; init; }
    /// <summary>First and last round the stage is played in (from funds and upgrade prices, see <see cref="AutoPlanPacing"/>).</summary>
    public int FromRound { get; init; }
    public int ToRound { get; init; }
    /// <summary>Round in which the terminal is upgraded to <see cref="NextLevel"/> (0 = no further upgrade).</summary>
    public int UpgradeRound { get; init; }
    /// <summary>Funds that upgrade costs in <see cref="UpgradeRound"/> (it gets one cheaper every round).</summary>
    public int UpgradeCost { get; init; }
    /// <summary>Cards of later stages that only the next terminal level offers.</summary>
    public IReadOnlyList<string> Unlocks { get; init; } = [];
    /// <summary>Cards of later stages the current shop already offers: buy and bench them when they show up.</summary>
    public IReadOnlyList<AutoPlanPick> Reserve { get; init; } = [];
    /// <summary>Endgame cards (大成 / 后期 叠层) first offered at this terminal level: the reason to stay and roll.</summary>
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
