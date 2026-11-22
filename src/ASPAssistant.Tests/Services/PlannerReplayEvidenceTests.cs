using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using FluentAssertions;

namespace ASPAssistant.Tests.Services;

public class PlannerReplayEvidenceTests
{
    [Fact]
    public void PairedStylesUseTheSameRunsAndMeasureTheRequestedMembers()
    {
        var members = new[] { "哈洛德", "水灯心", "涤火杰西卡", "烛煌", "Miss.Christine", "白铁", "华法琳", "罗比菈塔" };
        var team = new PlannedLineup(8, 0, members.Select(n => new PlannedOperator(n, null, [])).ToArray(),
            new Dictionary<string, int>(), false);
        var replay = PlannerReplayEvidence.Compare("维多利亚", team)!;
        replay.Core.Runs.Should().Be(replay.Small.Runs).And.BeGreaterThan(0);
        replay.Core.Coverage.Should().BeInRange(0, replay.Core.Runs * 8);
        replay.Detail(AutoPlanStyle.CoreFirst).Should().Contain("目标 8 人").And.Contain("最少持有");
        replay.Detail(AutoPlanStyle.SmallFirst).Should().Contain($"首领通过 {replay.Small.Clears}/{replay.Small.Runs} 局");
        PlannerReplayEvidence.Compare("炎", team).Should().BeNull();
        PlannerReplayEvidence.Compare("维多利亚", team with { Strategy = PlannerStrategy.Mech }).Should().BeNull();
    }

    [Fact]
    public void AcquisitionUsesCumulativeRunsAndKeepsUnknownOperatorsDistinct()
    {
        var rates = Enumerable.Range(1, 6).Select(t => PlannerReplayEvidence.EarlyPurchaseRate("罗比菈塔", t)!.Value).ToArray();
        rates.Should().BeInAscendingOrder();
        rates.Should().OnlyContain(rate => rate >= 0 && rate <= 1);
        PlannerReplayEvidence.EarlyPurchaseRate("自选样例", 4).Should().BeNull();
    }
}
