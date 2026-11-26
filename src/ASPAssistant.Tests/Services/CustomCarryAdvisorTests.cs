using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.Services;

public class CustomCarryAdvisorTests
{
    [Fact]
    public void PicksTwoPerTierAndLeavesNeutralSlotsEmpty()
    {
        var ops = Enumerable.Range(0, 12).Select(i => new Operator { Name = $"自选{i:00}", IsCustom = true }).ToList();
        for (int i = 0; i < ops.Count; i++) ops[i].SetRosterEntry(new(ops[i].Name, i < 6 ? 5 : 6));
        var lineup = new Lineup { Slots = [.. ops.Take(3).Concat(ops.Skip(6).Take(3)).Select(o => new LineupSlot { OperatorName = o.Name })] };
        lineup.Slots[2].Tags.Add(LineupTag.阵容核心);
        var bans = new BanViewModel();
        bans.SetBans([ops[6].Name]);
        var calculator = new CovenantCalculator(ops, [], new Dictionary<string,string>());
        var picks = CustomCarryAdvisor.Recommend(lineup, ops, calculator, bans, PlannerStrategy.Other);
        picks.Select(p => p.Name).Should().Equal(ops[0].Name, ops[2].Name, ops[7].Name, ops[8].Name);
        picks.Single(p => p.Name == ops[2].Name).Reason.Should().Be("阵容核心");
        lineup.Slots = [lineup.Slots[0]];
        CustomCarryAdvisor.Recommend(lineup, ops, calculator, bans, PlannerStrategy.Other).Should().ContainSingle();
        ops[0].SetRosterEntry(new(ops[0].Name));
        CustomCarryAdvisor.Recommend(lineup, ops, calculator, bans, PlannerStrategy.Other).Should().BeEmpty();
    }

    [Fact]
    public void RecommendsAComboOnlyWhenItReachesATargetAndAccountsForStrategy()
    {
        Operator[] ops = [new() { Name = "甲", IsCustom = true, CoreCovenants = ["炎"] },
            new() { Name = "乙", IsCustom = true, CoreCovenants = ["炎"] },
            new() { Name = PlannerStrategies.PithName, RequiredStrategyId = PlannerStrategies.PithId, AdditionalCovenants = ["调和"] }];
        ops[0].SetRosterEntry(new("甲",5));
        ops[1].SetRosterEntry(new("乙",6));
        var info = new CovenantInfo { Name = "炎", BondType = "SEASON", ActivateCount = 2 };
        var calc = new CovenantCalculator(ops, [info], new Dictionary<string,string>());
        var lineup = new Lineup { TargetCovenants = [new() { Name = "炎", Tier = 1 }] };
        var bans = new BanViewModel();
        var pair = CustomCarryAdvisor.Recommend(lineup, ops, calc, bans, PlannerStrategy.Other);
        pair.Should().HaveCount(2).And.OnlyContain(pick => pick.Reason.Contains("炎"));
        CustomCarryAdvisor.Recommend(lineup, ops, calc, bans, PlannerStrategy.Pith).Should().ContainSingle();
        bans.SetCovenantBans(["炎"]);
        CustomCarryAdvisor.Recommend(lineup, ops, calc, bans, PlannerStrategy.Pith).Should().BeEmpty();
    }
}
