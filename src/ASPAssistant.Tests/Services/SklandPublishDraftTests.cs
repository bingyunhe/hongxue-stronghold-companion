using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;

namespace ASPAssistant.Tests.Services;

public sealed class SklandPublishDraftTests
{
    [Fact]
    public void ExportsEditedSlotsRoundBoundariesEquipmentAndStrategyOrder()
    {
        var lineup = new Lineup
        {
            Name = "回合攻略",
            Strategies = [new() { StrategyId = "default" }, new() { StrategyId = "second", Tags = [StrategyTag.次选] },
                new() { StrategyId = "primary", Tags = [StrategyTag.阵容核心] }],
            EditorStages = [new() { Id = "early", Name = "开局", StartRound = 1, EndRound = 4, OperationIdea = "先存钱" },
                new() { Id = "final", Name = "成型阵容", IsFinal = true, RecommendedEquipments = ["装备甲"] }],
            EarlySlots = [new() { OperatorName = "角峰" }],
            Slots = [new() { OperatorName = "迷迭香", IsElite = true, Equipments = ["装备甲", "装备乙"] }]
        };
        var draft = SklandPublishDraft.FromLineup(lineup);
        Assert.Equal(new[] { "primary", "second", "default" }, draft.StrategyIds);
        Assert.Equal("先存钱", draft.Tabs[0].Notes);
        Assert.Equal(4, draft.Tabs[0].End);
        Assert.Equal("角峰", Assert.Single(draft.Tabs[0].Operators).Name);
        Assert.True(Assert.Single(draft.Tabs[1].Operators).Elite);
        Assert.Equal(new[] { "装备甲", "装备乙" }, draft.Tabs[1].Gear);
    }

    [Fact]
    public void PreventsRepostingImportedGuidesAndPublishingUndatedRoundStages()
    {
        var lineup = new Lineup { Slots = [new() { OperatorName = "角峰" }], SklandGuide = new() };
        Assert.Throws<InvalidDataException>(() => SklandPublishDraft.FromLineup(lineup));
        lineup.SklandGuide = null;
        lineup.EarlySlots.Add(new() { OperatorName = "迷迭香" });
        Assert.Throws<InvalidDataException>(() => SklandPublishDraft.FromLineup(lineup));
    }
}
