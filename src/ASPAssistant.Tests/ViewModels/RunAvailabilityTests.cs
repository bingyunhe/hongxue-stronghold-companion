using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.ViewModels;

public class RunAvailabilityTests
{
    [Fact]
    public void PlannerOnlyOffersTransformationsWithRealEquipment()
    {
        CovenantInfo[] covenants = [new() { Name = "精准", ActivateCount = 2 }, new() { Name = "助力", ActivateCount = 2 }];
        var planner = new LineupPlannerViewModel(new LineupPlanner([], covenants), [], new BanViewModel(), EquipmentTestData.Rules);
        planner.AllCovenants.Single(c => c.Name == "助力").CanTransformInto.Should().BeFalse();
        planner.AllCovenants.Single(c => c.Name == "精准").CanTransformInto.Should().BeTrue();
        planner.BuildRequest().TransformAllowed.Should().OnlyContain(c => EquipmentTestData.Rules.CanTransformInto(c));
    }

    [Fact]
    public void OnlyCurrentRunLimitsCoreButKeepsAllAdditionalChoicesAndTheirSelection()
    {
        var vm = new OperatorBrowseViewModel();
        vm.LoadOperators([
            new() { Name = "甲", CoreCovenants = ["炎"], AdditionalCovenants = ["精准"] },
            new() { Name = "乙", CoreCovenants = ["维多利亚"], AdditionalCovenants = ["坚守"] }
        ]);
        vm.HasRunInfo.Should().BeFalse();
        vm.SetCovenantBanChecker(c => c == "炎");
        vm.SetCoreCovenantFilters(["炎"]);
        vm.SetOtherCovenantFilters(["精准"]);
        vm.SetAvailableCovenants(["维多利亚", "坚守"]);
        vm.OnlyCurrentRun = true;
        vm.AvailableCoreCovenants.Should().Equal("", "维多利亚");
        vm.CoreChips.Should().NotContain(c => c.Value == "炎");
        vm.AvailableOtherCovenants.Should().Equal("", "坚守", "精准");
        vm.ActiveFilterTags.Should().NotContain("炎").And.Contain("精准");
        vm.OtherChips.Single(c => c.Value == "精准").IsSelected.Should().BeTrue();
        vm.FilteredOperators.Select(o => o.Name).Should().Equal("甲");
        vm.ClearCovenantHighlights();
        vm.HasRunInfo.Should().BeFalse();
        vm.OnlyCurrentRun.Should().BeFalse();
        vm.AvailableCoreCovenants.Should().Contain("炎");
        vm.CoreChips.Single(c => c.Value == "炎").IsBlocked.Should().BeFalse();
        vm.CoreChips.Single(c => c.Value == "炎").IsSelected.Should().BeFalse();
        vm.AvailableOtherCovenants.Should().Contain("精准");
    }

    [Fact]
    public void ReducedRunHidesMixedCovenantOperatorsAndMissingFilterChoices()
    {
        var vm = new OperatorBrowseViewModel();
        vm.LoadOperators([
            new() { Name = "锏", CoreCovenants = ["卡西米尔", "谢拉格"] },
            new() { Name = "遥", AdditionalCovenants = ["聚合", "奇迹"] },
            new() { Name = "角峰", CoreCovenants = ["谢拉格"], AdditionalCovenants = ["坚守"] }
        ]);
        vm.SetAvailableCovenants(["谢拉格", "聚合"], new HashSet<string> { "卡西米尔", "奇迹" });
        vm.OnlyCurrentRun = true;
        vm.FilteredOperators.Select(o => o.Name).Should().Equal("角峰");
        vm.AvailableOtherCovenants.Should().Contain("坚守").And.NotContain("奇迹");
        vm.SelectedOperatorSource = OperatorSourceFilter.Default;
        vm.OtherChips.Should().NotContain(c => c.Value == "奇迹");
        vm.CoreChips.Should().NotContain(c => c.Value == "卡西米尔");
        vm.ClearCovenantHighlights();
        vm.FilteredOperators.Should().HaveCount(3);
        vm.AvailableOtherCovenants.Should().Contain("奇迹");
    }

    [Fact]
    public void ClearingRunLimitsPreservesManualGoalsLocksAndOperatorBans()
    {
        Operator[] operators = [new() { Name = "固定干员", CoreCovenants = ["维多利亚"] }, new() { Name = "手动禁用", CoreCovenants = ["炎"] }];
        CovenantInfo[] covenants = [new() { Name = "炎", BondType = "SEASON", ActivateCount = 3 }, new() { Name = "维多利亚", BondType = "SEASON", ActivateCount = 3 }];
        var bans = new BanViewModel();
        var planner = new LineupPlannerViewModel(new LineupPlanner(operators, covenants), operators, bans, EquipmentTestData.Rules);
        planner.AllCovenants.Single(c => c.Name == "维多利亚").ToggleLevel1Command.Execute(null);
        planner.AddLocked("固定干员");
        bans.ToggleBan("手动禁用");
        bans.SetCovenantBans(["炎"]);
        planner.SetAvailableCovenants(["维多利亚"]);
        planner.OnlyCurrentRun = true;
        bans.SetCovenantBans([]);
        planner.SetAvailableCovenants([]);
        planner.HasRunInfo.Should().BeFalse();
        planner.OnlyCurrentRun.Should().BeFalse();
        planner.AllCovenants.Should().OnlyContain(c => !c.IsBanned && c.IsVisible);
        planner.SelectedGoalCount.Should().Be(1);
        planner.LockedOperators.Should().ContainSingle().Which.Name.Should().Be("固定干员");
        bans.IsBanned("手动禁用").Should().BeTrue();
    }
}
