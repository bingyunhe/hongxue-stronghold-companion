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
            foreach (var sub in l.Substitutes)
            {
                sub.Missing.Should().NotBe("烈夏");
                sub.StandIn.Should().NotBe("塔露拉");
                planner.IsExcludedByBans(byName[sub.StandIn], request.BannedCovenants).Should().BeFalse();
                var slot = final.Slots.Single(s => s.OperatorName == sub.Missing);
                slot.Equipments.Should().BeEmpty();
                slot.OperatorName = sub.StandIn;
                final.Slots.Should().HaveCount(8).And.OnlyHaveUniqueItems(s => s.OperatorName);
                calc.Compute(final).Single(s => s.Name == goal.Name).Count.Should().BeGreaterThanOrEqualTo(goal.Count);
                slot.OperatorName = sub.Missing;
            }
        }
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
        string Tip(int level, params string[] owned) => LineupSubstitutionAdvice.Describe(final, ops, new(level, null, owned.ToHashSet()));
        Tip(2).Should().Contain("先用低阶").And.Contain("决定是否换回").And.NotContain("先用高阶");
        Tip(2, "高阶").Should().Contain("先用高阶（已记录获取）");
        Tip(6, "目标").Should().BeEmpty();
        Tip(1).Should().BeEmpty();
    }
}
