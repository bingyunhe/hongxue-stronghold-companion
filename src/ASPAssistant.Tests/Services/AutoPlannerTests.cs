using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using FluentAssertions;

namespace ASPAssistant.Tests.Services;

/// <summary>自动规划: one candidate per core covenant, each playable 先小后大 or 先大后小.</summary>
public class AutoPlannerTests
{
    private static CovenantInfo Core(string name) => new() { Name = name, BondType = "SEASON", ActivateCount = 3 };
    private static CovenantInfo Extra(string name, int count = 2) => new() { Name = name, BondType = "REGULAR", ActivateCount = count };

    private static Operator Op(string name, int tier, string[]? core = null, string[]? extra = null, string profession = "近卫",
        double stack = 0)
    {
        var op = new Operator { Name = name, Tier = tier, CoreCovenants = [.. core ?? []], AdditionalCovenants = [.. extra ?? []] };
        op.Profile = new OperatorProfile
        {
            Profession = profession,
            Roles = profession switch { "重装" => [OperatorProfile.TankRole], "医疗" => [OperatorProfile.HealerRole], _ => [] },
            Stack = stack > 0 ? new OperatorStackInfo { Kind = "covenant", Covenants = [.. core ?? []], Amount = stack, PerRound = stack } : null
        };
        return op;
    }

    private static readonly CovenantInfo[] Covenants = [Core("炎"), Core("维多利亚"), Extra("精准"), Extra("坚守"), Extra("突袭")];

    /// <summary>
    /// 炎: its stackers are tier Ⅲ (12) and Ⅵ (18), so most of the stacking comes late.
    /// 维多利亚: tier Ⅰ / Ⅱ stackers (6 + 8) already carry, and three holders are tier ≤ Ⅱ.
    /// </summary>
    private static List<Operator> Operators() =>
    [
        Op("炎1", 1, ["炎"]), Op("炎2", 2, ["炎"]), Op("炎3", 3, ["炎"], stack: 12), Op("炎4", 4, ["炎"]),
        Op("炎5", 4, ["炎"]), Op("炎6", 5, ["炎"]), Op("炎7", 6, ["炎"], stack: 18), Op("炎8", 5, ["炎"], ["精准"]),
        Op("维1", 1, ["维多利亚"], stack: 6), Op("维2", 2, ["维多利亚"], stack: 8), Op("维3", 2, ["维多利亚"]),
        Op("维4", 3, ["维多利亚"]), Op("维5", 4, ["维多利亚"]), Op("维6", 4, ["维多利亚"], ["突袭"]), Op("维7", 5, ["维多利亚"], stack: 6),
        Op("精1", 1, extra: ["精准"], profession: "狙击"), Op("精2", 2, extra: ["精准"], profession: "狙击"),
        Op("精3", 3, extra: ["精准", "突袭"], profession: "狙击"),
        Op("盾1", 1, extra: ["坚守"], profession: "重装"), Op("盾2", 2, extra: ["坚守"], profession: "重装"),
        Op("盾3", 4, extra: ["坚守", "突袭"], profession: "重装"),
        Op("奶1", 2, profession: "医疗"), Op("奶2", 4, extra: ["精准"], profession: "医疗"),
    ];

    private static AutoPlanner Planner(List<Operator>? operators = null)
    {
        operators ??= Operators();
        return new AutoPlanner(new LineupPlanner(operators, Covenants), new LineupRoutePlanner(operators, Covenants), operators);
    }

    private static int Count(RouteStage stage, string covenant) => stage.Covenants.FirstOrDefault(c => c.Name == covenant)?.Count ?? 0;
    private static RouteStage Stage(AutoPlanRoute plan, string name) => plan.Route.Stages.Single(s => !s.IsFinal && s.Definition.Name == name);

    [Fact]
    public void Plan_OffersEveryAvailableCoreCovenantAtSixPeople()
    {
        var result = Planner().Plan(new AutoPlanRequest());

        result.Error.Should().BeNull();
        result.Candidates.Select(c => c.MainCovenant).Should().BeEquivalentTo("炎", "维多利亚");
        result.Candidates.Should().BeInDescendingOrder(c => c.Score);
        foreach (var candidate in result.Candidates)
        {
            candidate.MainGoal.Count.Should().Be(6);
            candidate.Final.People.Should().Be(8);
            candidate.Final.Counts[candidate.MainCovenant].Should().BeGreaterThanOrEqualTo(6);
            candidate.Final.Operators.Should().HaveCount(8);
            candidate.Goals[0].Should().Be(candidate.MainGoal);
            candidate.Goals.Skip(1).Should().BeEquivalentTo(candidate.SmallGoals).And.OnlyContain(g => !g.IsCore);
            candidate.Stackers.Should().BeInAscendingOrder(s => s.Tier).And.OnlyContain(s => s.LayersPerRound > 0);
        }
    }

    [Fact]
    public void Plan_RecommendsTheStyleFromWhereTheStackersSit()
    {
        var candidates = Planner().Plan(new AutoPlanRequest()).Candidates.ToDictionary(c => c.MainCovenant);

        // 炎: 12 layers below 五本 against 30 with the tier Ⅵ stacker
        candidates["炎"].EarlyLayers.Should().Be(12);
        candidates["炎"].LateLayers.Should().Be(30);
        candidates["炎"].RecommendedStyle.Should().Be(AutoPlanStyle.SmallFirst);
        candidates["炎"].StyleReason.Should().Contain("炎7");
        // 维多利亚: 14 of 20 layers come from tier Ⅰ / Ⅱ, and a two-terminal activates it
        candidates["维多利亚"].EarlyLayers.Should().Be(14);
        candidates["维多利亚"].LateLayers.Should().Be(20);
        candidates["维多利亚"].ActivationLevel.Should().Be(2);
        candidates["维多利亚"].RecommendedStyle.Should().Be(AutoPlanStyle.CoreFirst);
    }

    [Fact]
    public void Plan_SkipsBannedCoreCovenantsAndBannedOperators()
    {
        var result = Planner().Plan(new AutoPlanRequest
        {
            BannedCovenants = new HashSet<string> { "炎" },
            BannedOperators = new HashSet<string> { "维7" }
        });

        result.Candidates.Select(c => c.MainCovenant).Should().Equal("维多利亚");
        result.Candidates[0].Final.Operators.Should().NotContain(o => o.Name == "维7");
        result.Candidates[0].Stackers.Should().NotContain(s => s.Name == "维7");
    }

    [Fact]
    public void Plan_WithoutLowTierHolders_RecommendsSmallFirst()
    {
        // 炎 holders start at tier Ⅳ (two of them): three only come together at 五本
        var operators = Operators().Where(o => !o.CoreCovenants.Contains("炎") || o.Tier >= 4).ToList();
        operators.Add(Op("炎9", 5, ["炎"]));
        operators.Add(Op("炎10", 6, ["炎"]));

        var fire = Planner(operators).Plan(new AutoPlanRequest()).Candidates.Single(c => c.MainCovenant == "炎");

        fire.ActivationLevel.Should().Be(5);
        fire.RecommendedStyle.Should().Be(AutoPlanStyle.SmallFirst);
        fire.StyleReason.Should().Contain("5 本");
    }

    [Fact]
    public void Plan_FallsBackToTheFirstLevelWhenSixCannotBeReached()
    {
        // Only four 炎 holders are left and no transformation gear is available.
        var operators = Operators().Where(o => o.Name is not ("炎5" or "炎6" or "炎7" or "炎8")).ToList();

        var fire = Planner(operators).Plan(new AutoPlanRequest()).Candidates.Single(c => c.MainCovenant == "炎");

        fire.MainGoal.Count.Should().Be(3);
        fire.Notes.Should().Contain(n => n.Contains("只凑得到 3 人"));
    }

    [Fact]
    public void Plan_ReportsALockedOperatorThatIsBanned()
    {
        var result = Planner().Plan(new AutoPlanRequest
        {
            LockedOperators = new HashSet<string> { "炎1" },
            BannedOperators = new HashSet<string> { "炎1" }
        });

        result.Candidates.Should().BeEmpty();
        result.Error.Should().Contain("炎1");
    }

    [Fact]
    public void Plan_KeepsLockedOperatorsInEveryCandidate()
    {
        var result = Planner().Plan(new AutoPlanRequest { LockedOperators = new HashSet<string> { "盾1" } });

        result.Candidates.Should().NotBeEmpty();
        result.Candidates.Should().OnlyContain(c => c.Final.Operators.Any(o => o.Name == "盾1"));
    }

    [Theory]
    [InlineData(AutoPlanStyle.SmallFirst)]
    [InlineData(AutoPlanStyle.CoreFirst)]
    public void Route_HasTheFiveStagesAndEndsInTheCandidateLineup(AutoPlanStyle style)
    {
        var planner = Planner();
        var request = new AutoPlanRequest();
        var fire = planner.Plan(request).Candidates.Single(c => c.MainCovenant == "炎");

        var plan = planner.PlanRoute(fire, style, request);

        plan.Style.Should().Be(style);
        plan.Route.Stages.Select(s => s.IsFinal ? "大成" : s.Definition.Name).Should().Equal("开局", "前期", "中期", "成型", "后期", "大成");
        plan.Route.Stages.Where(s => !s.IsFinal).Select(s => s.Definition.TerminalLevel).Should().Equal(2, 3, 4, 5, 6);
        plan.Route.Stages[^1].Operators.Select(o => o.Name).Should().BeEquivalentTo(fire.Final.Operators.Select(o => o.Name));
        plan.Advice.Should().HaveCount(plan.Route.Stages.Count);
        // By 五本 the 本家 stands whichever way it is played.
        Count(Stage(plan, "成型"), "炎").Should().Be(6);
    }

    [Fact]
    public void Route_CoreFirstBuildsTheMainCovenantFromTheOpening_SmallFirstWaitsForFive()
    {
        var planner = Planner();
        var request = new AutoPlanRequest();
        var fire = planner.Plan(request).Candidates.Single(c => c.MainCovenant == "炎");

        var coreFirst = planner.PlanRoute(fire, AutoPlanStyle.CoreFirst, request);
        var smallFirst = planner.PlanRoute(fire, AutoPlanStyle.SmallFirst, request);

        // 先大后小: as many 炎 as each shop offers (Ⅰ, Ⅱ and one 三连 Ⅲ at 二本), six from 四本 on.
        Count(Stage(coreFirst, "开局"), "炎").Should().Be(3);
        Count(Stage(coreFirst, "中期"), "炎").Should().Be(6);
        new[] { "开局", "前期", "中期" }.Select(n => Count(Stage(coreFirst, n), "炎")).Should().BeInAscendingOrder();
        // 先小后大: the early stages spend their places on 小盟约 instead.
        new[] { "开局", "前期", "中期" }.Sum(n => Count(Stage(smallFirst, n), "炎"))
            .Should().BeLessThan(new[] { "开局", "前期", "中期" }.Sum(n => Count(Stage(coreFirst, n), "炎")));
        Count(Stage(smallFirst, "中期"), "炎").Should().BeLessThan(6);
        foreach (var name in new[] { "开局", "前期", "中期" })
            Stage(smallFirst, name).Covenants.Should().Contain(c => !c.IsCore && c.Level > 0, name);
    }

    [Theory]
    [InlineData(AutoPlanStyle.SmallFirst)]
    [InlineData(AutoPlanStyle.CoreFirst)]
    public void Advice_ReserveAndUnlocksOnlyNameCardsOfLaterStagesInTheRightTiers(AutoPlanStyle style)
    {
        var planner = Planner();
        var request = new AutoPlanRequest();
        var fire = planner.Plan(request).Candidates.Single(c => c.MainCovenant == "炎");
        var plan = planner.PlanRoute(fire, style, request);
        var tiers = Operators().ToDictionary(o => o.Name, o => o.Tier);
        var stages = plan.Route.Stages;

        for (var i = 0; i < stages.Count; i++)
        {
            var stage = stages[i];
            var advice = plan.Advice[i];
            advice.Focus.Should().NotBeEmpty();
            if (stage.IsFinal)
            {
                advice.Reserve.Should().BeEmpty();
                advice.Unlocks.Should().BeEmpty();
                continue;
            }
            var onField = stage.Operators.Select(o => o.Name).ToHashSet();
            var later = stages.Skip(i + 1).SelectMany(s => s.Operators).Select(o => o.Name).ToHashSet();
            // 抓位: not fielded yet, used later, and already in this shop
            advice.Reserve.Should().OnlyContain(p => !onField.Contains(p.Name) && later.Contains(p.Name) && tiers[p.Name] <= stage.Definition.MaxTier);
            advice.Reserve.Should().OnlyHaveUniqueItems(p => p.Name);
            advice.UpgradeText.Should().NotBeEmpty();
            if (advice.NextLevel == 0)
            {
                // 后期 (叠层): the last terminal level
                stage.Definition.Stacking.Should().BeTrue();
                advice.Pace.Should().Be(AutoPlanPace.Max);
                advice.Unlocks.Should().BeEmpty();
                // what the boss lineup still needs is kept on the bench
                advice.Reserve.Select(p => p.Name).Should().BeEquivalentTo(
                    stages[^1].Operators.Select(o => o.Name).Where(n => !onField.Contains(n)).Take(6));
                continue;
            }
            advice.NextLevel.Should().Be(stages[i + 1].Definition.TerminalLevel);
            advice.UpgradeText.Should().Contain($"升 {advice.NextLevel} 本");
            advice.Unlocks.Should().OnlyContain(n => later.Contains(n) && !onField.Contains(n)
                && tiers[n] > stage.Definition.MaxTier && tiers[n] <= stages[i + 1].Definition.MaxTier);
            advice.KeyCards.Should().OnlyContain(n => tiers[n] <= stage.Definition.MaxTier);
        }
    }

    [Fact]
    public void Advice_SmallFirstRushesToFive_CoreFirstStaysWhereTheEndgameCardsAre()
    {
        var planner = Planner();
        var request = new AutoPlanRequest();
        var fire = planner.Plan(request).Candidates.Single(c => c.MainCovenant == "炎");

        var smallFirst = planner.PlanRoute(fire, AutoPlanStyle.SmallFirst, request);
        var coreFirst = planner.PlanRoute(fire, AutoPlanStyle.CoreFirst, request);

        // 开局 / 前期 / 中期 are below 五本
        smallFirst.Advice.Take(3).Should().OnlyContain(a => a.Pace == AutoPlanPace.Rush);
        smallFirst.Advice.Take(3).Should().OnlyContain(a => a.UpgradeText.Contains("见到就留"));
        smallFirst.Advice.Take(3).Should().OnlyContain(a => a.Focus.StartsWith("小盟约过渡"));
        smallFirst.Advice[3].Focus.Should().StartWith("本家成型");
        coreFirst.Advice.Take(3).Should().OnlyContain(a => a.Focus.StartsWith("凑本家"));
        // a level is only worth staying at when it first offers at least three endgame cards
        foreach (var advice in coreFirst.Advice.Where(a => a.NextLevel > 0))
            (advice.Pace == AutoPlanPace.Stay).Should().Be(advice.KeyCards.Count >= 3 && advice.NextLevel > 3, advice.UpgradeText);
        coreFirst.Advice.Where(a => a.Pace == AutoPlanPace.Rush).Should().OnlyContain(a => a.KeyCards.Count == 0);
    }

    [Theory]
    // 5 / 8 / 11 / 12 / 13 in the round the level below is reached, one cheaper every round after that
    [InlineData(2, 1, 1, 5)]
    [InlineData(2, 1, 2, 4)]
    [InlineData(3, 2, 2, 8)]
    [InlineData(3, 2, 4, 6)]
    [InlineData(4, 4, 6, 9)]
    [InlineData(5, 6, 8, 10)]
    [InlineData(6, 8, 9, 12)]
    // …but never below zero
    [InlineData(2, 1, 9, 0)]
    public void Pacing_UpgradeGetsOneCheaperEveryRound(int level, int reachedRound, int round, int cost)
        => AutoPlanPacing.UpgradeCost(level, reachedRound, round).Should().Be(cost);

    [Theory]
    // 速升 all the way: 二本 in round 2 (4 of 5 funds), 三本 in 4 (6 of 7), 四本 in 6 (9 of 9), 五本 in 8 (10 of 11), 六本 in 9 (12 of 12)
    [InlineData(2, 1, 2)]
    [InlineData(3, 2, 4)]
    [InlineData(4, 4, 6)]
    [InlineData(5, 6, 8)]
    [InlineData(6, 8, 9)]
    // a level reached late: the higher funds cover the next upgrade sooner, but the level is still played for one round
    [InlineData(3, 5, 6)]
    [InlineData(4, 5, 7)]
    [InlineData(6, 11, 12)]
    public void Pacing_EarliestUpgradeIsTheFirstRoundWhoseFundsCoverIt(int level, int reachedRound, int expected)
    {
        var round = AutoPlanPacing.EarliestUpgradeRound(level, reachedRound);

        round.Should().Be(expected);
        AutoPlanPacing.UpgradeCost(level, reachedRound, round).Should().BeLessThanOrEqualTo(AutoPlanPacing.Funds(round));
        if (round > reachedRound + 1)
            AutoPlanPacing.UpgradeCost(level, reachedRound, round - 1).Should().BeGreaterThan(AutoPlanPacing.Funds(round - 1));
    }

    private static readonly int[] StageLevels = [2, 3, 4, 5, 6];

    [Fact]
    public void Pacing_ScheduleRushesAsSoonAsTheFundsCoverTheUpgrade()
    {
        var rounds = AutoPlanPacing.Schedule(StageLevels,
            [AutoPlanPace.Rush, AutoPlanPace.Rush, AutoPlanPace.Rush, AutoPlanPace.Rush, AutoPlanPace.Max]);

        rounds.Select(r => r.ReachedRound).Should().Equal(2, 4, 6, 8, 9);
        rounds.Select(r => r.UpgradeRound).Should().Equal(4, 6, 8, 9, 0);
        rounds.Select(r => r.UpgradeCost).Should().Equal(6, 9, 10, 12, 0);
    }

    [Fact]
    public void Pacing_ScheduleHoldsALevelOneRoundForSteadyAndTwoForStay()
    {
        var rounds = AutoPlanPacing.Schedule(StageLevels,
            [AutoPlanPace.Steady, AutoPlanPace.Rush, AutoPlanPace.Rush, AutoPlanPace.Stay, AutoPlanPace.Max]);

        // 三本 one round after round 4, for 5 instead of 6; 六本 two rounds after round 9, for 10 instead of 12.
        // In between the higher funds of the later rounds catch up: 五本 still comes in round 8.
        rounds.Select(r => r.ReachedRound).Should().Equal(2, 5, 7, 8, 11);
        rounds.Select(r => r.UpgradeRound).Should().Equal(5, 7, 8, 11, 0);
        rounds.Select(r => r.UpgradeCost).Should().Equal(5, 9, 11, 10, 0);
    }

    [Fact]
    public void Pacing_ScheduleCutsTheHoldsThatWouldLeaveLessThanThreeRoundsOfStacking()
    {
        // Held all the way, 六本 would only come in round 15: the holds are cut until it comes in round 11.
        var rounds = AutoPlanPacing.Schedule(StageLevels,
            [AutoPlanPace.Steady, AutoPlanPace.Stay, AutoPlanPace.Stay, AutoPlanPace.Stay, AutoPlanPace.Max]);

        rounds[^1].ReachedRound.Should().Be(AutoPlanPacing.BossRound - AutoPlanPacing.StackingRounds);
        rounds.Select(r => r.ReachedRound).Should().Equal(2, 4, 7, 9, 11);
        rounds.Select(r => r.UpgradeRound).Should().Equal(4, 7, 9, 11, 0);
        rounds.Select(r => r.UpgradeCost).Should().Equal(6, 8, 10, 11, 0);
    }

    [Theory]
    // 4 in round 1, one more every round, 12 from round 9 on (items aside)
    [InlineData(1, 4)]
    [InlineData(2, 5)]
    [InlineData(5, 8)]
    [InlineData(8, 11)]
    [InlineData(9, 12)]
    [InlineData(14, 12)]
    [InlineData(15, 12)]
    public void Pacing_FundsGrowFromFourToTwelve(int round, int funds) => AutoPlanPacing.Funds(round).Should().Be(funds);

    [Theory]
    [InlineData(AutoPlanStyle.SmallFirst)]
    [InlineData(AutoPlanStyle.CoreFirst)]
    public void Advice_SpreadsTheStagesOverTheFourteenRounds(AutoPlanStyle style)
    {
        var planner = Planner();
        var request = new AutoPlanRequest();
        var fire = planner.Plan(request).Candidates.Single(c => c.MainCovenant == "炎");

        var advice = planner.PlanRoute(fire, style, request).Advice;

        // 开局 from round 1, every stage follows the one before, 后期 runs up to the boss in round 14
        advice[0].FromRound.Should().Be(1);
        for (var i = 1; i < advice.Count; i++)
            advice[i].FromRound.Should().Be(advice[i - 1].ToRound + 1);
        advice.Should().OnlyContain(a => a.ToRound >= a.FromRound);
        (advice[^2].ToRound, advice[^1].FromRound, advice[^1].ToRound).Should().Be((13, 14, 14));
        (advice[^2].UpgradeRound, advice[^1].UpgradeRound).Should().Be((0, 0));
        advice[^2].ToRound.Should().BeGreaterThanOrEqualTo(advice[^2].FromRound + AutoPlanPacing.StackingRounds - 1);
        foreach (var stage in advice.Where(a => a.NextLevel > 0))
        {
            stage.UpgradeRound.Should().Be(stage.ToRound + 1);
            // the round's funds cover the upgrade, and the text says what it costs and what is left to buy with
            var funds = AutoPlanPacing.Funds(stage.UpgradeRound);
            stage.UpgradeCost.Should().BeInRange(1, funds);
            stage.UpgradeText.Should().Contain($"约第 {stage.UpgradeRound} 回合（升本 {stage.UpgradeCost} 费，资金 {funds}，剩 {funds - stage.UpgradeCost}）")
                .And.Contain($"升 {stage.NextLevel} 本");
        }
        // 二本 is not affordable in round 1 (5 against 4 funds); a round later it costs 4
        advice[0].UpgradeText.Should().StartWith("第 2 回合先升 2 本（4 费）");
        if (style == AutoPlanStyle.SmallFirst)
        {
            // 速升 through 二本 / 三本 / 四本: 五本 in round 8
            advice.Take(3).Select(a => a.UpgradeRound).Should().Equal(4, 6, 8);
            advice.Take(3).Select(a => a.UpgradeCost).Should().Equal(6, 9, 10);
        }
    }

    [Fact]
    public void Stages_SmallFirstHandsTheEarlyStagesTheSmallCovenants()
    {
        var fire = Planner().Plan(new AutoPlanRequest()).Candidates.Single(c => c.MainCovenant == "炎");

        var smallFirst = AutoPlanner.Stages(AutoPlanStyle.SmallFirst, fire);
        var coreFirst = AutoPlanner.Stages(AutoPlanStyle.CoreFirst, fire);

        smallFirst.Take(3).Should().OnlyContain(s => ReferenceEquals(s.SmallGoals, fire.SmallGoals) && s.Required.Count == 0);
        smallFirst[3].SmallGoals.Should().BeNull();
        smallFirst[3].Required.Should().Equal(fire.MainGoal);
        coreFirst.Take(4).Should().OnlyContain(s => s.SmallGoals == null && s.Required.SequenceEqual(new[] { fire.MainGoal }));
        smallFirst[^1].Stacking.Should().BeTrue();
        coreFirst[^1].Stacking.Should().BeTrue();
    }

    [Fact]
    public async Task RealData_PlansEveryCoreCovenantAndBothStylesOfTheBestOne()
    {
        var data = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "TestData", "Regulation"));
        var operators = (await data.LoadOperatorsAsync()).Where(o => o.RequiredStrategyId.Length == 0).ToList();
        var (covenants, _) = await data.LoadCovenantsAsync();
        var lineupPlanner = new LineupPlanner(operators, covenants);
        var planner = new AutoPlanner(lineupPlanner, new LineupRoutePlanner(operators, covenants), operators);
        var request = new AutoPlanRequest { TransformAllowed = lineupPlanner.DefaultTransformAllowed() };

        var result = planner.Plan(request);

        result.Candidates.Select(c => c.MainCovenant).Should().BeEquivalentTo(covenants.Where(c => c.BondType == "SEASON").Select(c => c.Name));
        result.Candidates.Should().OnlyContain(c => c.MainGoal.Count == 6 && c.Final.Transforms == 0 && !c.Final.MissingHealer && c.Stackers.Count > 0);
        // Both styles are recommended somewhere: stackers that sit low (乌萨斯) and stackers that sit high (谢拉格).
        result.Candidates.Single(c => c.MainCovenant == "乌萨斯").RecommendedStyle.Should().Be(AutoPlanStyle.CoreFirst);
        result.Candidates.Single(c => c.MainCovenant == "谢拉格").RecommendedStyle.Should().Be(AutoPlanStyle.SmallFirst);

        var best = result.Candidates[0];
        var tiers = operators.ToDictionary(o => o.Name, o => o.Tier);
        var early = new[] { "开局", "前期", "中期" };
        var routes = new[] { AutoPlanStyle.SmallFirst, AutoPlanStyle.CoreFirst }.ToDictionary(s => s, s => planner.PlanRoute(best, s, request));
        foreach (var plan in routes.Values)
        {
            foreach (var stage in plan.Route.Stages.Where(s => !s.IsFinal))
            {
                stage.Operators.Should().OnlyContain(o => tiers[o.Name] <= stage.Definition.MaxTier + 1);
                stage.Operators.Count(o => tiers[o.Name] > stage.Definition.MaxTier).Should().BeLessThanOrEqualTo(stage.Definition.TripleSlots);
            }
            Count(Stage(plan, "成型"), best.MainCovenant).Should().Be(6);
        }
        early.Sum(n => Count(Stage(routes[AutoPlanStyle.CoreFirst], n), best.MainCovenant))
            .Should().BeGreaterThan(early.Sum(n => Count(Stage(routes[AutoPlanStyle.SmallFirst], n), best.MainCovenant)));
        routes[AutoPlanStyle.SmallFirst].Advice.Take(3).Should().OnlyContain(a => a.Pace == AutoPlanPace.Rush);
    }

    [Fact]
    public async Task RealData_BannedCovenantsAreNotOffered()
    {
        var data = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "TestData", "Regulation"));
        var operators = (await data.LoadOperatorsAsync()).Where(o => o.RequiredStrategyId.Length == 0).ToList();
        var (covenants, _) = await data.LoadCovenantsAsync();
        var planner = new AutoPlanner(new LineupPlanner(operators, covenants), new LineupRoutePlanner(operators, covenants), operators);
        var banned = new HashSet<string> { "炎", "乌萨斯", "聚合" };

        var result = planner.Plan(new AutoPlanRequest { BannedCovenants = banned });

        result.Candidates.Should().HaveCount(8);
        result.Candidates.Should().NotContain(c => banned.Contains(c.MainCovenant));
        // 聚合 is banned: operators whose covenants are all banned are out of every lineup
        var excluded = operators.Where(o => CovenantBanRules.IsExcluded(o, banned)).Select(o => o.Name).ToHashSet();
        excluded.Should().NotBeEmpty();
        result.Candidates.SelectMany(c => c.Final.Operators).Should().NotContain(o => excluded.Contains(o.Name));
    }
}
