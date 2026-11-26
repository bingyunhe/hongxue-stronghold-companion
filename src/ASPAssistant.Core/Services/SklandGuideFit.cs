using ASPAssistant.Core.Models;
using ASPAssistant.Core.ViewModels;

namespace ASPAssistant.Core.Services;

public static class SklandGuideFit
{
    public static string Describe(SklandBrowseGuide guide, SklandBrowseStage stage, IReadOnlyDictionary<string, Operator> operators,
        BanViewModel bans, PlannerStrategy strategy, bool hasBanInfo, DeployPoolState run)
    {
        if (!hasBanInfo && run.CarriedCustom == null) return "尚未取得本局信息。开局后可查看各阶段的人选。";
        (string Group, bool? Available) State(string name)
        {
            if (!operators.TryGetValue(name, out var op)) return ("本地未收录", null);
            if (bans.UnavailableReason(op, strategy) is { } reason) return (reason, false);
            if (run.IsOwned(name)) return ("已持有", true);
            if (op.IsCustom && op.CarriedThisRun != true)
                return op.CarriedThisRun == false ? ("自选未携带", false) : ("自选携带待确认", null);
            if (!hasBanInfo) return ("禁位待确认", null);
            if (op.RequiredStrategyId.Length > 0) return ("由本局策略提供", true);
            return op.IsCustom ? ("自选已携带，可购买", true) : ("可购买", true);
        }

        var all = guide.Stages.SelectMany(s => s.Operators).Distinct().ToDictionary(name => name, State);
        var here = stage.Operators.Distinct().ToList();
        var lines = new List<string> { $"整篇攻略　{all.Values.Count(s => s.Available == true)}/{all.Count} 人可用" };
        int pending = all.Values.Count(s => s.Available == null);
        if (pending > 0) lines[0] += $" · {pending} 人待确认";
        if (here.Count == 0) lines.Add("本阶段未列推荐干员");
        else
        {
            lines.Add($"本阶段　{here.Count(n => all[n].Available == true)}/{here.Count} 人可用");
            foreach (var group in here.GroupBy(n => all[n]).OrderBy(g => g.Key.Available == false ? 0 : g.Key.Available == null ? 1 : 2))
                lines.Add($"{group.Key.Group}　{string.Join("、", group)}");
        }
        var other = all.Where(pair => pair.Value.Available == false && !here.Contains(pair.Key))
            .Select(pair => $"{pair.Key}（{pair.Value.Group}）").ToList();
        if (other.Count > 0) lines.Add("其他阶段拿不到　" + string.Join("、", other));
        if (all.Values.Any(s => s.Group == "自选未携带") && run.CarriedCustom is { Count: 0 }
            && !run.CustomCarryEdited && run.CustomOpeningResult != "Prepared:empty-confirmed")
            lines.Add("自选携带未识别，已按 0 人计算");
        return string.Join("\n", lines);
    }
}
