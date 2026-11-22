using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using FluentAssertions;

namespace ASPAssistant.Tests.Services;

public class BossCovenantRankingTests
{
    private static AutoPlanCandidate Plan(string core, int transforms = 0) => new()
    {
        MainCovenant = core, MainGoal = new(core, 2, 6, false, true),
        Final = new(8, transforms, [], new Dictionary<string, int>(), false)
    };

    [Fact]
    public void ComparableMeasuredCoresExchangeSlotsAndUnmeasuredOrTransformedPlansStayPut()
    {
        var baseline = new[] { Plan("阿戈尔"), Plan("炎"), Plan("莱塔尼亚"), Plan("乌萨斯", 1) };
        var result = BossCovenantRanking.Rank(baseline, "假想敌：茧", 8);

        result.Select(x => x.Candidate).Should().Equal(baseline[2], baseline[1], baseline[0], baseline[3]);
        result[0].Note.Should().Contain("95 局中 74 局通关");
        result[1].Note.Should().Contain("暂无 SP5 模拟样本");
    }

    [Theory]
    [InlineData("假想敌：茧", null)]
    [InlineData("假想敌：茧", 5)]
    [InlineData("未确认首领", 20)]
    public void UnknownContextKeepsTheOrdinaryRanking(string boss, int? level)
    {
        var baseline = new[] { Plan("阿戈尔"), Plan("莱塔尼亚") };
        var result = BossCovenantRanking.Rank(baseline, boss, level);
        result.Select(x => x.Candidate).Should().Equal(baseline);
        result.Should().OnlyContain(x => x.Note.Length == 0);
    }
}
