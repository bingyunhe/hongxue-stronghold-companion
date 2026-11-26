using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.Services;

public class LineupSubstitutionTests
{
    [Theory]
    [InlineData(PlannerStrategy.Other)]
    [InlineData(PlannerStrategy.Pith)]
    [InlineData(PlannerStrategy.Mech)]
    public async Task OneSlotChangesPreserveGoalsLocksBansAndTransformationGear(PlannerStrategy strategy)
    {
        var data = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "data"));
        var ops = await data.LoadOperatorsAsync();
        var (covenants, _) = await data.LoadCovenantsAsync();
        var planner = new LineupPlanner(ops, covenants);
        var calc = new CovenantCalculator(ops, covenants, covenants.ToDictionary(c => c.Name + "转职", c => c.Name));
        var goal = LineupPlanner.CreateGoal(covenants.Single(c => c.Name == "乌萨斯"), 2);
        var request = new PlannerRequest
        {
            Goals = [goal],
            LockedOperators = new HashSet<string> { "烈夏" }, BannedOperators = new HashSet<string> { "塔露拉" },
            BannedCovenants = new HashSet<string> { "炎" }, TransformAllowed = planner.DefaultTransformAllowed(),
            PeopleOptions = [8], TransformOptions = [1], Strategy = strategy, IncludeSubstitutes = true
        };
        var lineups = planner.Plan(request).AllLineups.ToList();
        lineups.SelectMany(l => l.Substitutes).Should().NotBeEmpty();
        var byName = ops.ToDictionary(o => o.Name);
        foreach (var l in lineups)
        {
            var final = new PlannerLineupItem { Source = l }.ToLineup(calc.EquipmentRules);
            var original = calc.Compute(final).ToDictionary(c => c.Name, c => c.Count);
            foreach (var sub in l.Substitutes)
            {
                sub.Missing.Should().NotBe("烈夏");
                sub.StandIn.Should().NotBe("塔露拉");
                planner.IsExcludedByBans(byName[sub.StandIn], request.BannedCovenants).Should().BeFalse();
                var slot = final.Slots.Single(s => s.OperatorName == sub.Missing);
                slot.Equipments.Should().BeEmpty();
                slot.OperatorName = sub.StandIn;
                final.Slots.Should().HaveCount(8).And.OnlyHaveUniqueItems(s => s.OperatorName);
                var after = calc.Compute(final);
                after.Single(s => s.Name == goal.Name).Count.Should().BeGreaterThanOrEqualTo(goal.Count);
                sub.Changes.Select(c => (c.Covenant.Name, c.Before, c.After)).Should().BeEquivalentTo(
                    after.Where(c => c.Count != original[c.Name]).Select(c => (c.Name, original[c.Name], c.Count)));
                slot.OperatorName = sub.Missing;
            }
        }
    }

    [Fact]
    public void ComparisonShowsGainsLossesNextThresholdAndManualConditions()
    {
        var text = LineupSubstitutionAdvice.DescribeChanges([
            new(new() { Name = "炎", BondType = "SEASON", ActivateCount = 3 }, 6, 5),
            new(new() { Name = "精准", ActivateCount = 2 }, 1, 2),
            new(new() { Name = "独行", ActivateCount = 1 }, 1, 2),
            new(new() { Name = "叠层", ActivateCount = 2 }, 2, 3)]);
        text.Should().Contain("II 档 → I 档，距 II 档差 1 人").And.Contain("未激活 → I 档，距 II 档差 1 人")
            .And.Contain("I 档 → 未激活，需减至 1 人").And.Contain("II 档需另核实特性条件");
        LineupSubstitutionAdvice.DescribeChanges([]).Should().Contain("均保持");
    }

    [Fact]
    public void AdviceFollowsConfirmedPurchasesAndShopLevel()
    {
        var ops = new[] { new Operator { Name = "目标", Tier = 6 }, new Operator { Name = "低阶", Tier = 2 },
            new Operator { Name = "高阶", Tier = 5 } }.ToDictionary(o => o.Name);
        var final = new PlannedLineup(8, 0, [], new Dictionary<string, int>(), false)
        {
            Substitutes = [new("目标", "低阶", 1), new("目标", "高阶", 5)]
        };
        string Tip(int level, params string[] owned) => LineupSubstitutionAdvice.Choices(final, ops, new(level, null, owned.ToHashSet())).FirstOrDefault()?.Advice ?? "";
        Tip(2).Should().Contain("先用低阶").And.Contain("决定是否换回").And.NotContain("先用高阶");
        Tip(2, "高阶").Should().Contain("先用高阶（已记录获取）");
        Tip(6, "目标").Should().BeEmpty();
        Tip(1).Should().BeEmpty();
    }
}
