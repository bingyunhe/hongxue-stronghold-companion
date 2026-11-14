using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.ViewModels;

/// <summary>The 自动 page of the 规划 tab: candidates per core covenant and the route of one of them.</summary>
public class LineupPlannerAutoTests
{
    private static Operator Op(string name, int tier, string[]? core = null, string[]? extra = null) => new()
    {
        Name = name, Tier = tier, CoreCovenants = [.. core ?? []], AdditionalCovenants = [.. extra ?? []],
        IconPath = $"icons/operators/{name}.png"
    };

    private static (LineupPlannerViewModel Vm, BanViewModel Bans) Create()
    {
        var operators = new List<Operator>();
        for (var i = 1; i <= 8; i++) operators.Add(Op($"炎{i}", (i - 1) % 6 + 1, ["炎"]));
        for (var i = 1; i <= 8; i++) operators.Add(Op($"维{i}", (i - 1) % 6 + 1, ["维多利亚"]));
        operators.Add(Op("炎突", 2, ["炎"], ["突袭"]));
        operators.Add(Op("维突", 3, ["维多利亚"], ["突袭"]));
        operators.Add(Op("突1", 1, extra: ["突袭"]));
        operators.Add(Op("精1", 1, extra: ["精准"]));
        operators.Add(Op("精2", 2, extra: ["精准"]));
        operators.Add(Op("精3", 4, extra: ["精准"]));
        var covenants = new[]
        {
            new CovenantInfo { Name = "炎", BondType = "SEASON", ActivateCount = 3 },
            new CovenantInfo { Name = "维多利亚", BondType = "SEASON", ActivateCount = 3 },
            new CovenantInfo { Name = "突袭", BondType = "REGULAR", ActivateCount = 2 },
            new CovenantInfo { Name = "精准", BondType = "REGULAR", ActivateCount = 2 },
        };
        var bans = new BanViewModel();
        var vm = new LineupPlannerViewModel(new LineupPlanner(operators, covenants), operators, bans, EquipmentTestData.Rules) { DebounceMilliseconds = 0 };
        return (vm, bans);
    }

    private static async Task<LineupPlannerViewModel> CreateOnAutoPageAsync()
    {
        var (vm, _) = Create();
        vm.IsActive = true;
        vm.ShowAutoPageCommand.Execute(null);
        await vm.PendingAutoPlan!;
        return vm;
    }

    [Fact]
    public async Task AutoPage_PlansOneCandidatePerCoreCovenantOnceItIsShown()
    {
        var (vm, _) = Create();
        vm.IsActive = true;
        vm.PendingAutoPlan.Should().BeNull("nothing is planned before the page is opened");

        vm.ShowAutoPageCommand.Execute(null);
        await vm.PendingAutoPlan!;

        vm.AutoPlans.Select(p => p.MainCovenant).Should().BeEquivalentTo("炎", "维多利亚");
        vm.AutoPlans.Select(p => p.Rank).Should().Equal(1, 2);
        vm.HasAutoPlans.Should().BeTrue();
        vm.IsAutoBusy.Should().BeFalse();
        vm.AutoStatusText.Should().Contain("2 个主盟约");
        foreach (var plan in vm.AutoPlans)
        {
            plan.Title.Should().Be($"{plan.MainCovenant} 6");
            plan.Operators.Should().HaveCount(8).And.OnlyContain(o => o.IconPath.Length > 0);
            plan.Covenants.Should().Contain(c => c.IsGoal && c.Text.StartsWith(plan.MainCovenant));
            plan.Styles.Select(s => s.Style).Should().BeEquivalentTo([AutoPlanStyle.SmallFirst, AutoPlanStyle.CoreFirst]);
            plan.Styles[0].IsRecommended.Should().BeTrue();
            plan.Styles[0].Style.Should().Be(plan.Source.RecommendedStyle);
            plan.Styles[1].IsRecommended.Should().BeFalse();
            plan.StyleReason.Should().NotBeEmpty();
        }
    }

    [Fact]
    public void Pages_AreMutuallyExclusive()
    {
        var (vm, _) = Create();
        vm.ShowGoals.Should().BeTrue();

        vm.ShowAutoPageCommand.Execute(null);
        (vm.ShowAuto, vm.ShowResults, vm.ShowGoals, vm.ShowAutoList, vm.ShowResultsPanel).Should().Be((true, false, false, true, false));

        vm.ShowResultPageCommand.Execute(null);
        (vm.ShowAuto, vm.ShowResults, vm.ShowGoals, vm.ShowAutoList, vm.ShowResultList).Should().Be((false, true, false, false, true));

        vm.ShowAutoPageCommand.Execute(null);
        vm.ShowGoalPageCommand.Execute(null);
        (vm.ShowAuto, vm.ShowResults, vm.ShowGoals).Should().Be((false, false, true));
    }

    [Fact]
    public void AutoPage_WaitsUntilTheTabIsOnScreen()
    {
        var (vm, _) = Create();

        vm.ShowAutoPageCommand.Execute(null);
        vm.PendingAutoPlan.Should().BeNull();

        vm.IsActive = true;
        vm.PendingAutoPlan.Should().NotBeNull();
    }

    [Fact]
    public async Task OpeningAPlan_ShowsItsStagesWithAdviceAndReturnsToTheList()
    {
        var vm = await CreateOnAutoPageAsync();
        var choice = vm.AutoPlans[0].Styles.Single(s => s.Style == AutoPlanStyle.SmallFirst);

        await vm.PlanAutoRouteAsync(choice);

        (vm.ShowRoute, vm.RouteFromAuto, vm.ShowRouteView, vm.ShowAutoList, vm.ShowResultsPanel).Should().Be((true, true, true, false, true));
        vm.IsRouteBusy.Should().BeFalse();
        vm.RouteTitle.Should().Be($"{choice.Plan.MainCovenant} · 先小后大");
        vm.RouteStages.Select(s => s.Title).Should().Equal("开局", "前期", "中期", "成型", "后期", "大成");
        vm.RouteSteps.Select(s => s.Name).Should().Equal("开局", "前期", "中期", "成型", "后期", "大成");
        vm.RouteStages.Should().OnlyContain(s => s.HasAdvice);
        vm.RouteStages.Where(s => !s.IsFinal).Should().OnlyContain(s => s.HasUpgrade && s.PaceText.Length > 0);
        // 先小后大: every stage below 五本 is rushed
        vm.RouteStages.Take(3).Should().OnlyContain(s => s.IsRushPace && s.PaceText == "速升");
        vm.RouteStages.Single(s => s.IsStacking).PaceText.Should().Be("满本");
        // Rounds in every stage header, from funds against upgrade cost: 开局 from round 1, the boss in round 14
        vm.RouteStages.Take(3).Select(s => s.RoundText).Should().Equal("第 1–3 回合", "第 4–5 回合", "第 6–7 回合");
        vm.RouteStages.Take(3).Select(s => s.LevelText).Should().Equal("第 1–3 回合 · 终端 2", "第 4–5 回合 · 终端 3", "第 6–7 回合 · 终端 4");
        vm.RouteStages.Single(s => s.IsStacking).RoundText.Should().EndWith("–13 回合");
        vm.RouteStages[^1].LevelText.Should().Be("第 14 回合 · 打 Boss");
        vm.RouteStages[^1].Subtitle.Should().Contain("第 15 回合");
        vm.RouteStages[0].Subtitle.Should().Contain("按资金和升本费用推算").And.Contain("5 / 8 / 11 / 12 / 13");
        // 五本 in round 8: by then the upgrade is down to 10 and the round hands out 11 funds
        vm.RouteStages[0].UpgradeText.Should().StartWith("第 2 回合先升 2 本（4 费）；约第 4 回合（升本 6 费，资金 7，剩 1）");
        vm.RouteStages[2].UpgradeText.Should().StartWith("约第 8 回合（升本 10 费，资金 11，剩 1）");
        vm.RouteStages.Take(3).Select(s => s.FundsText).Should().Equal(
            "每回合资金约 4 → 6（不含道具）", "每回合资金约 7 → 8（不含道具）", "每回合资金约 9 → 10（不含道具）");
        vm.RouteStages.Single(s => s.IsStacking).FundsText.Should().Be("每回合资金约 12（不含道具）");
        vm.RouteStages[^1].HasFunds.Should().BeFalse();
        vm.RouteStages[^1].PaceText.Should().BeEmpty();
        vm.RouteStages[^1].Operators.Select(o => o.Name).Should().BeEquivalentTo(choice.Plan.Operators.Select(o => o.Name));
        vm.RouteStages.SelectMany(s => s.Reserve).Should().OnlyContain(r => r.HasDetails && r.ExtraInfo.Length > 0);
        vm.TrackRouteCommand.CanExecute(null).Should().BeTrue();

        vm.CloseRouteCommand.Execute(null);

        (vm.ShowRoute, vm.RouteFromAuto, vm.ShowAuto, vm.ShowAutoList).Should().Be((false, false, true, true));
        vm.RouteStages.Should().BeEmpty();
        vm.AutoPlans.Should().NotBeEmpty();
    }

    [Fact]
    public async Task TheTwoStylesOfOnePlan_DifferInTheEarlyStages()
    {
        var vm = await CreateOnAutoPageAsync();
        var plan = vm.AutoPlans[0];
        string[] EarlyFocus() => vm.RouteStages.Take(3).Select(s => s.FocusText).ToArray();

        await vm.PlanAutoRouteAsync(plan.Styles.Single(s => s.Style == AutoPlanStyle.CoreFirst));
        EarlyFocus().Should().OnlyContain(f => f.StartsWith("凑本家"));
        vm.RouteTitle.Should().EndWith("先大后小");

        await vm.PlanAutoRouteAsync(plan.Styles.Single(s => s.Style == AutoPlanStyle.SmallFirst));
        EarlyFocus().Should().OnlyContain(f => f.StartsWith("小盟约过渡"));
        vm.RouteStages.Should().HaveCount(6, "the second route replaces the first");
    }

    [Fact]
    public async Task BanChange_ReplansTheCandidatesAndClosesTheirRoute()
    {
        var (vm, bans) = Create();
        vm.IsActive = true;
        vm.ShowAutoPageCommand.Execute(null);
        await vm.PendingAutoPlan!;
        await vm.PlanAutoRouteAsync(vm.AutoPlans[0].Styles[0]);

        bans.SetCovenantBans(["炎"]);
        await vm.PendingAutoPlan!;

        vm.AutoPlans.Select(p => p.MainCovenant).Should().Equal("维多利亚");
        vm.ShowRoute.Should().BeFalse();
        vm.ShowAutoList.Should().BeTrue();
    }

    [Fact]
    public async Task ChangesTheCandidatesDoNotDependOn_KeepThemAndTheirRoute()
    {
        var vm = await CreateOnAutoPageAsync();
        var planned = vm.PendingAutoPlan;
        var first = vm.AutoPlans[0];
        await vm.PlanAutoRouteAsync(first.Styles[0]);

        // Toggling a target starts a target search; its results must not close the auto route.
        vm.AllCovenants.Single(c => c.Name == "炎").ToggleLevel1Command.Execute(null);
        await vm.PendingSearch!;

        vm.PendingAutoPlan.Should().BeSameAs(planned);
        vm.AutoPlans[0].Should().BeSameAs(first);
        vm.ShowRoute.Should().BeTrue();
        vm.RouteFromAuto.Should().BeTrue();
        vm.TotalResults.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task NinePeopleAndLockedOperators_ArePartOfTheRequest()
    {
        var vm = await CreateOnAutoPageAsync();

        vm.AutoNinePeople = true;
        await vm.PendingAutoPlan!;
        vm.AutoPlans.Should().NotBeEmpty().And.OnlyContain(p => p.Source.Final.People == 9 && p.Subtitle.StartsWith("9 人"));

        vm.AddLocked("精3").Should().BeTrue();
        await vm.PendingAutoPlan!;
        vm.AutoPlans.Should().NotBeEmpty().And.OnlyContain(p => p.Operators.Any(o => o.Name == "精3" && o.IsLocked));
        vm.AutoStatusText.Should().Contain("必带 1 人");
    }

    [Fact]
    public async Task SavedAutoRoute_KeepsTheStagesAsLineupPhases()
    {
        var vm = await CreateOnAutoPageAsync();
        Lineup? saved = null;
        vm.SaveRequested += l => saved = l;
        var choice = vm.AutoPlans[0].Styles[0];
        await vm.PlanAutoRouteAsync(choice);

        vm.SaveRouteCommand.Execute(null);

        saved.Should().NotBeNull();
        saved!.Name.Should().Be($"自动 {choice.Plan.MainCovenant} {choice.Name} 路线");
        saved.Slots.Select(s => s.OperatorName).Should().BeEquivalentTo(choice.Plan.Operators.Select(o => o.Name));
        saved.EarlySlots.Select(s => s.OperatorName).Should().Equal(vm.RouteStages.Single(s => s.Title == "前期").Operators.Select(o => o.Name));
        saved.MidSlots.Select(s => s.OperatorName).Should().Equal(vm.RouteStages.Single(s => s.Title == "中期").Operators.Select(o => o.Name));
    }

    [Fact]
    public async Task TrackedAutoRoute_HandsOverTheWholeRoute()
    {
        var vm = await CreateOnAutoPageAsync();
        PlannedRoute? tracked = null;
        vm.TrackRouteRequested += r => tracked = r;
        await vm.PlanAutoRouteAsync(vm.AutoPlans[0].Styles[0]);

        vm.TrackRouteCommand.Execute(null);

        tracked.Should().NotBeNull();
        tracked!.Stages.Should().HaveCount(6);
        // 开局 expires at 三本 … 成型 at 六本: the tracker drops each stage's 过渡 cards as the terminal passes it
        Enumerable.Range(0, 6).Select(tracked.ExpiresAt).Should().Equal(3, 4, 5, 6, 0, 0);
    }
}
