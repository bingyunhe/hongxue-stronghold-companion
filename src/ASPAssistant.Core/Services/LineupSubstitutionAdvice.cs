using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Services;

public sealed record SubstitutionChoice(LineupSubstitute Swap, Operator Missing, Operator StandIn, bool Owned, int FinalScore)
{
    public string Label => Swap.Label;
    public string Changes => LineupSubstitutionAdvice.DescribeChanges(Swap.Changes);
    public string Advice
    {
        get
        {
            var availability = Owned ? "已记录获取" : $"{StandIn.Tier} 本起可搜";
            var restore = Missing.Profile switch
            {
                { IsHealer: true } when StandIn.Profile?.IsHealer != true => "需要补治疗时换回",
                { IsTank: true } when StandIn.Profile?.IsTank != true => "需要前排承伤时换回",
                { IsStacker: true } => "准备启用其叠层特质时换回",
                _ => "在地图模拟中比较下一波守线，再决定是否换回"
            };
            var line = $"缺{Missing.Name}可先用{StandIn.Name}（{availability}），{Missing.Name}到手后{restore}。";
            if (StandIn.Profile is { } before && Missing.Profile is { } after)
            {
                if (before.IsMelee != after.IsMelee)
                    line += before.IsMelee ? "替补需要可部署近战的地面格。" : "换回时给近战干员留地面格。";
                if (before.IsHealer || after.IsHealer) line += "换人后核对治疗范围是否覆盖前排。";
                else if (before.NormalRange?.Base is { } first && after.NormalRange?.Base is { } second && first != second)
                    line += "两人普攻范围不同，换人后核对朝向。";
            }
            if (Swap.AdditionalScore != FinalScore)
                line += $"过渡时附加盟约评分 {FinalScore} → {Swap.AdditionalScore}。";
            return line;
        }
    }
}

public static class LineupSubstitutionAdvice
{
    public static string DescribeChanges(IEnumerable<CovenantCountChange> changes)
    {
        var lines = new List<string>();
        foreach (var (covenant, before, after) in changes)
        {
            var first = LineupPlanner.CreateGoal(covenant, 1).Count;
            bool solo = covenant.Name == LineupPlanner.SoloCovenant;
            bool manual = LineupPlanner.IsSecondLevelManual(covenant);
            int second = LineupPlanner.CreateGoal(covenant, 2).Count;
            int Level(int n) => solo ? (n == 1 ? 1 : 0) : n < first ? 0 :
                LineupPlanner.HasSecondLevel(covenant) && !manual && n >= second ? 2 : 1;
            string[] levels = ["未激活", "I 档", "II 档"];
            int oldLevel = Level(before), level = Level(after);
            var status = oldLevel == level ? levels[level] : $"{levels[oldLevel]} → {levels[level]}";
            string next = level switch
            {
                0 when solo && after > 1 => "，需减至 1 人",
                0 => $"，距 I 档差 {Math.Max(1, first - after)} 人",
                1 when manual => "，II 档需另核实特性条件",
                1 when LineupPlanner.HasSecondLevel(covenant) => $"，距 II 档差 {second - after} 人",
                _ => ""
            };
            lines.Add($"{covenant.Name} {before} → {after}　{status}{next}");
        }
        return lines.Count == 0 ? "盟约人数与档位均保持。" : string.Join("\n", lines);
    }

    public static IReadOnlyList<SubstitutionChoice> Choices(PlannedLineup final, IReadOnlyDictionary<string, Operator> operators, AutoPlanRunState state)
    {
        var choices = new List<SubstitutionChoice>();
        foreach (var group in final.Substitutes.GroupBy(s => s.Missing).OrderByDescending(g => operators[g.Key].Tier))
        {
            var missing = operators[group.Key];
            if (state.Owned?.Contains(missing.Name) == true) continue;
            int terminal = state.TerminalLevel > 0 ? state.TerminalLevel : Math.Max(1, missing.Tier - 1);
            var ranked = group.Select(s => (Swap: s, Op: operators[s.StandIn], Owned: state.Owned?.Contains(s.StandIn) == true, s.AdditionalScore))
                .Where(s => s.Owned || s.Op.Tier <= terminal)
                .OrderByDescending(s => s.Owned)
                .ThenByDescending(s => s.AdditionalScore)
                .ThenByDescending(s => missing.Profile is { } p && s.Op.Profile?.Profession == p.Profession)
                .ThenByDescending(s => PlannerReplayEvidence.EarlyPurchaseRate(s.Op.Name, terminal))
                .ThenBy(s => s.Op.Tier).ThenBy(s => s.Op.Name, StringComparer.Ordinal);
            foreach (var choice in ranked)
                choices.Add(new(choice.Swap, missing, choice.Op, choice.Owned, final.AdditionalScore));
        }
        return choices;
    }
}
