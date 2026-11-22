using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Services;

public static class LineupSubstitutionAdvice
{
    public static string Describe(PlannedLineup final, IReadOnlyDictionary<string, Operator> operators, AutoPlanRunState state)
    {
        var lines = new List<string>();
        foreach (var group in final.Substitutes.GroupBy(s => s.Missing).OrderByDescending(g => operators[g.Key].Tier))
        {
            var missing = operators[group.Key];
            if (state.Owned?.Contains(missing.Name) == true) continue;
            int terminal = state.TerminalLevel > 0 ? state.TerminalLevel : Math.Max(1, missing.Tier - 1);
            var choice = group.Select(s => (Op: operators[s.StandIn], Owned: state.Owned?.Contains(s.StandIn) == true, s.AdditionalScore))
                .Where(s => s.Owned || s.Op.Tier <= terminal)
                .OrderByDescending(s => s.Owned)
                .ThenByDescending(s => s.AdditionalScore)
                .ThenByDescending(s => missing.Profile is { } p && s.Op.Profile?.Profession == p.Profession)
                .ThenByDescending(s => PlannerReplayEvidence.EarlyPurchaseRate(s.Op.Name, terminal))
                .ThenBy(s => s.Op.Tier).ThenBy(s => s.Op.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            if (choice.Op is not { } standIn) continue;

            var availability = choice.Owned ? "已记录获取" : $"{standIn.Tier} 本起可搜";
            var restore = missing.Profile switch
            {
                { IsHealer: true } when standIn.Profile?.IsHealer != true => "需要补治疗时换回",
                { IsTank: true } when standIn.Profile?.IsTank != true => "需要前排承伤时换回",
                { IsStacker: true } => "准备启用其叠层特质时换回",
                _ => "在地图模拟中比较下一波守线，再决定是否换回"
            };
            var line = $"缺{missing.Name}可先用{standIn.Name}（{availability}），{missing.Name}到手后{restore}。";
            if (standIn.Profile is { } before && missing.Profile is { } after)
            {
                if (before.IsMelee != after.IsMelee)
                    line += before.IsMelee ? "替补需要可部署近战的地面格。" : "换回时给近战干员留地面格。";
                if (before.IsHealer || after.IsHealer) line += "换人后核对治疗范围是否覆盖前排。";
                else if (before.NormalRange?.Base is { } first && after.NormalRange?.Base is { } second && first != second)
                    line += "两人普攻范围不同，换人后核对朝向。";
            }
            if (choice.AdditionalScore != final.AdditionalScore)
                line += $"过渡时附加盟约评分 {final.AdditionalScore} → {choice.AdditionalScore}。";
            lines.Add(line);
        }
        return string.Join("\n", lines);
    }
}
