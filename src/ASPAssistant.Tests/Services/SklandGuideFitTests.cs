using System.Text.Json;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.Services;

public class SklandGuideFitTests
{
    [Fact]
    public void CountsUniquePeopleAndExplainsOtherStagesAndUncertainCarry()
    {
        var first = new SklandBrowseStage("成型", 1, 0, 0, ["已买","普通","自选甲","自选乙","禁用","未知"], [], [], [], "");
        var early = first with { Operators = ["普通","缺盟约","策略棋子"] };
        var item = JsonSerializer.SerializeToElement(new { });
        var guide = new SklandBrowseGuide("用例","","","","","","","",[],[],[first,early],item,item);
        var ops = first.Operators.Concat(early.Operators).Distinct().Where(n => n != "未知")
            .ToDictionary(n => n, n => new Operator { Name = n, IsCustom = n.StartsWith("自选") });
        var bans = new BanViewModel();
        var run = new DeployPoolState();
        string Fit(bool hasBans) => SklandGuideFit.Describe(guide, first, ops, bans, PlannerStrategy.Other, hasBans, run);
        Fit(false).Should().Contain("尚未取得本局信息");
        run.Owned.Add("已买");
        run.CarriedCustom = [];
        ops["自选甲"].SetRunEntry(true, new("自选甲",5));
        ops["自选乙"].SetRunEntry(false);
        ops["缺盟约"].AdditionalCovenants.Add("不屈");
        ops["策略棋子"].RequiredStrategyId = PlannerStrategies.PithId;
        bans.SetCovenantBans([], new HashSet<string> { "不屈" });
        bans.SetBans(["禁用"]);
        run.CarriedCustom.Add(new("自选甲",5));
        Fit(true).Should().Contain("整篇攻略　3/8 人可用").And.Contain("本阶段　3/6 人可用")
            .And.Contain("已持有　已买").And.Contain("可购买　普通").And.Contain("自选已携带，可购买　自选甲")
            .And.Contain("自选未携带　自选乙").And.Contain("干员被禁　禁用").And.Contain("本地未收录　未知")
            .And.Contain("缺盟约（本局缺少盟约）").And.Contain("策略棋子（策略不符）");
        run.CarriedCustom.Clear();
        ops["自选甲"].SetRunEntry(false);
        Fit(false).Should().Contain("禁位待确认").And.Contain("按 0 人计算");
        run.CustomCarryEdited = true;
        Fit(true).Should().NotContain("按 0 人计算");
    }
}
