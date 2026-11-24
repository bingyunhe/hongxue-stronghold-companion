using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
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
        plan.Route.Stages.Select(s => s.IsFinal ? "成型阵容" : s.Definition.Name).Should().Equal("开局", "前期", "中期", "后期", "成型阵容");
        plan.Route.Stages.Where(s => !s.IsFinal).Select(s => s.Definition.TerminalLevel).Should().Equal(2, 3, 4, 6);
        plan.Route.Stages[^1].Operators.Select(o => o.Name).Should().BeEquivalentTo(fire.Final.Operators.Select(o => o.Name));
        plan.Advice.Should().HaveCount(plan.Route.Stages.Count);
        // By 五本 the 本家 stands whichever way it is played.
        Count(plan.Route.Stages[^1], "炎").Should().Be(6);
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
    public void Advice_ReserveOnlyNamesCardsOfLaterStagesInTheRightTiers(AutoPlanStyle style)
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
                // what the boss lineup still needs is kept on the bench
                advice.Reserve.Select(p => p.Name).Should().BeEquivalentTo(
                    stages[^1].Operators.Select(o => o.Name).Where(n => !onField.Contains(n)).Take(6));
                continue;
            }
            advice.NextLevel.Should().Be(stage.Definition.TerminalLevel + 1);
            advice.UpgradeText.Should().Contain($"升 {advice.NextLevel} 本");
            advice.KeyCards.Should().OnlyContain(n => tiers[n] <= stage.Definition.MaxTier);
        }
    }

    [Fact]
    public void Advice_SmallFirstRushesToFive_CoreFirstStaysForMissingRoles()
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
        smallFirst.Advice[3].Focus.Should().StartWith("叠层");
        coreFirst.Advice.Take(3).Should().OnlyContain(a => a.Focus.StartsWith("凑本家"));
        smallFirst.Advice[2].Levels.Select(l => l.TerminalLevel).Should().Equal(4, 5);
        smallFirst.Advice[2].Levels[1].Focus.Should().Contain("五本");
        smallFirst.Advice[2].Levels[1].UpgradeText.Should().Contain("升 6 本");
        // Even one missing critical role can justify staying; a count of arbitrary endgame cards cannot.
        foreach (var advice in coreFirst.Advice.Where(a => a.NextLevel > 0))
            (advice.Pace == AutoPlanPace.Stay).Should().Be(advice.KeyCards.Count > 0, advice.UpgradeText);
        coreFirst.Advice.Where(a => a.Pace == AutoPlanPace.Rush).Should().OnlyContain(a => a.KeyCards.Count == 0);
    }


    [Fact]
    public void Advice_FiveHasItsOwnMissingStackerAndOwnershipNeverComesFromThePlan()
    {
        var operators = new[] { Op("炎甲", 2, ["炎"]), Op("炎乙", 3, ["炎"]),
            Op("五阶叠层", 5, ["炎"], stack: 8), Op("盾", 2, profession: "重装"),
            Op("奶", 3, profession: "医疗"), Op("六阶增强", 6) };
        var planner = new AutoPlanner(new LineupPlanner(operators, Covenants), new LineupRoutePlanner(operators, Covenants), operators);
        RouteOperator Card(Operator op) => new(op.Name, op.Tier, "", RouteOperatorRole.Final)
            { Covenants = [.. op.CoreCovenants.Concat(op.AdditionalCovenants)],
                IsTank = op.Profile!.IsTank, IsHealer = op.Profile.IsHealer, IsStacker = op.Profile.IsStacker };
        var all = operators.Select(Card).ToList();
        var route = new PlannedRoute { Stages = [
            new(new("中期", 4, 4, 0, 5), all.Where(o => o.Tier <= 4).ToList(), []),
            new(new("后期", 6, 6, 0, 6) { Stacking = true }, all, []),
            new(new("成型", 6, 6, 0, 6), all, []) { IsFinal = true }] };
        var candidate = new AutoPlanCandidate { MainCovenant = "炎", MainGoal = new("炎", 1, 3, false, true),
            Final = new(6, 0, [], new Dictionary<string, int>(), false),
            Stackers = [new("五阶叠层", 5, "", 8)] };
        var owned = operators.Where(o => o.Tier <= 4).Select(o => o.Name).ToHashSet();
        var advice = planner.Advise(route, AutoPlanStyle.SmallFirst, candidate, new(5, null, owned));
        advice[0].Levels.Should().HaveCount(2);
        var five = advice[0].Levels[1];
        five.TerminalLevel.Should().Be(5);
        five.KeyCards.Should().Equal(new[] { "五阶叠层" }, "planned or fielded in the route is not evidence of purchase");
        five.Pace.Should().Be(AutoPlanPace.Stay, "one missing stacker is enough");
        five.PurchaseText.Should().Contain("启动本家叠层").And.Contain("未记录获取：五阶叠层");
        five.PurchaseText.Should().NotContain("六阶增强");
        owned.Add("五阶叠层");
        var ready = planner.Advise(route, AutoPlanStyle.SmallFirst, candidate, new(5, null, owned))[0].Levels[1];
        ready.KeyCards.Should().BeEmpty(); ready.Pace.Should().Be(AutoPlanPace.Steady);
        ready.UpgradeText.Should().Contain("核对实际上场").And.Contain("升 6 本");
        ready.Reserve.Should().NotContain(o => owned.Contains(o.Name));
        var unknown = planner.Advise(route, AutoPlanStyle.SmallFirst, candidate, new(0, null, null))[0].Levels[1];
        unknown.PurchaseText.Should().Contain("持有记录未连接");
        unknown.EconomyText.Should().Contain("待确认");
        var noSix = new PlannedRoute { Stages = route.Stages.Select(s => s with
            { Operators = s.Operators.Where(o => o.Tier <= 5).ToList() }).ToList() };
        var stayFive = planner.Advise(noSix, AutoPlanStyle.SmallFirst, candidate, new(5, null, owned))[0].Levels[1];
        stayFive.UpgradeText.Should().Contain("没有明确六阶需求").And.NotContain("后升 6 本");
    }

    [Theory]
    [InlineData(5, 10, 3, false, "升级后剩 7")]
    [InlineData(5, 2, 3, false, "还差 1")]
    [InlineData(5, null, 3, false, "未知")]
    [InlineData(5, 10, null, false, "未知")]
    [InlineData(0, 10, 3, false, "待确认")]
    [InlineData(4, 10, 3, false, "待确认")]
    [InlineData(5, 10, null, true, "核对终端等级")]
    public void Advice_OnlyUsesEconomyAtTheConfirmedCurrentTerminal(int terminal, int? money, int? cost, bool maxed, string expected)
    {
        var planner = Planner(); var request = new AutoPlanRequest();
        var candidate = planner.Plan(request).Candidates[0];
        var plan = planner.PlanRoute(candidate, AutoPlanStyle.CoreFirst, request);
        var advice = planner.Advise(plan.Route, plan.Style, candidate,
            new(terminal, new(7, money, cost, maxed, null, true), new HashSet<string>()));
        var five = advice[2].Levels.Single(l => l.TerminalLevel == 5);
        five.EconomyText.Should().Contain(expected);
        five.UpgradeText.Should().NotContain("已稳住");
        if (terminal != 4) advice[2].Levels[0].EconomyText.Should().NotContain("升级后剩 7");
    }

    [Fact]
    public void CancelledSearchStopsAndLimitedSearchDoesNotClaimImpossibility()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var search = () => Planner().Plan(new AutoPlanRequest(), cts.Token);
        search.Should().Throw<OperationCanceledException>();
        var operators = Enumerable.Range(0, 49).Select(i => Op($"独行{i}", 2, ["炎"], ["独行"])).ToList();
        CovenantInfo[] covenants = [Core("炎"), Extra("独行", 1)];
        var limited = new AutoPlanner(new LineupPlanner(operators, covenants), new LineupRoutePlanner(operators, covenants), operators)
            .Plan(new AutoPlanRequest());
        limited.SearchLimited.Should().BeTrue();
    }

    [Theory]
    [InlineData(PlannerStrategy.Other, false)]
    [InlineData(PlannerStrategy.Pith, false)]
    [InlineData(PlannerStrategy.Shapeshifter, false)]
    [InlineData(PlannerStrategy.Mech, false)]
    [InlineData(PlannerStrategy.Mech, true)]
    public async Task RealDataAutomaticStrategyKeepsItsPieceAndTargetsThroughTheRoute(PlannerStrategy strategy, bool elite)
    {
        var data = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "data"));
        var operators = await data.LoadOperatorsAsync(); var (covenants, _) = await data.LoadCovenantsAsync();
        var solver = new LineupPlanner(operators, covenants);
        var planner = new AutoPlanner(solver, new LineupRoutePlanner(operators, covenants), operators);
        var request = new AutoPlanRequest { Strategy = strategy, MechElite = elite, TransformAllowed = solver.DefaultTransformAllowed() };
        var result = planner.Plan(request);
        result.Candidates.Should().NotBeEmpty().And.OnlyContain(c => c.Final.Strategy == strategy);
        var candidate = result.Candidates[0];
        candidate.Final.MechElite.Should().Be(elite);
        var route = planner.PlanRoute(candidate, candidate.RecommendedStyle, request);
        route.Route.Stages.Should().HaveCount(5);
        route.Route.Stages[^1].Covenants.Single(c => c.Name == candidate.MainCovenant).Count.Should().BeGreaterThanOrEqualTo(candidate.MainGoal.Count);
        route.Route.Stages.Where(s => !s.IsFinal).Should().OnlyContain(s => s.Operators.Count <= s.Definition.People);
        if (strategy == PlannerStrategy.Mech)
        {
            candidate.Final.Transforms.Should().Be(0, "this pool can reach the same main target natively");
            PlannerLineupItem.StrategyPieceSlots(candidate.Final, EquipmentTestData.Rules).Should()
                .ContainSingle(s => s.OperatorName == LineupEquipmentService.MechName);
        }
        if (strategy == PlannerStrategy.Pith) candidate.Final.HasPith.Should().BeTrue();
    }
}
