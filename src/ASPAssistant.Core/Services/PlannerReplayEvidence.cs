using System.IO.Compression;
using System.Text.Json;
using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Services;

internal static class PlannerReplayEvidence
{
    private sealed record Sample(int Style, int Activation, int EarlyLeaks, bool Clear, int[][] Hands);
    private sealed record Dataset(string[] Names, int PurchaseRuns, int[][] Purchases, Dictionary<string, Sample[]> Routes);
    internal sealed record RouteResult(int Runs, int Active, int ActivationRounds, int EarlyLeaks, int Formed, int FormRounds, int Coverage, int Clears)
    {
        public string Describe(int members) =>
            $"本家激活 {Active}/{Runs} 局" + (Active > 0 ? $"，平均第 {(double)ActivationRounds / Active:0.#} 回合" : "") +
            $"，五本前每局漏 {(double)EarlyLeaks / Runs:0.#} 个。\n目标 {members} 人在回合初同时持有 {Formed}/{Runs} 局" +
            (Formed > 0 ? $"，平均第 {(double)FormRounds / Formed:0.#} 回合" : "") +
            $"。平均最多持有 {(double)Coverage / Runs:0.#} 人，首领通过 {Clears}/{Runs} 局。";
    }

    internal sealed record Comparison(RouteResult Core, RouteResult Small, string Scarce, int Members)
    {
        public string Summary => $"规程 8 · 同种子各 {Core.Runs} 局，五本前平均漏怪：先大 {(double)Core.EarlyLeaks / Core.Runs:0.#}，先小 {(double)Small.EarlyLeaks / Small.Runs:0.#}。";
        public string Detail(AutoPlanStyle style) => (style == AutoPlanStyle.CoreFirst ? Core : Small).Describe(Members)
            + $"\n最少持有的目标干员是{Scarce}。\nSP3 单人模拟，商店与禁用按样本抽取。";
    }

    private static readonly Dataset Data = Load();
    private static readonly Dictionary<string, int> Ids = Data.Names.Select((name, index) => (name, index))
        .ToDictionary(p => p.name, p => p.index, StringComparer.Ordinal);
    private static readonly Dictionary<string, string> CoreIds = new()
    {
        ["谢拉格"]="kjeragShip", ["乌萨斯"]="ursusShip", ["莱塔尼亚"]="lethaniaShip",
        ["阿戈尔"]="egirShip", ["维多利亚"]="victoriaShip", ["萨尔贡"]="sargonShip",
    };

    private static Dataset Load()
    {
        using var resource = typeof(PlannerReplayEvidence).Assembly.GetManifestResourceStream("PlannerReplaySamples")
            ?? throw new InvalidDataException("Missing planner replay samples.");
        using var compressed = new GZipStream(resource, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<Dataset>(compressed) ?? throw new InvalidDataException("Empty planner replay samples.");
    }

    // Fraction of SP3 runs purchasing at least one copy by the given terminal; offers/rewards are excluded.
    public static double? EarlyPurchaseRate(string name, int terminal) =>
        terminal is >= 1 and <= 6 && Ids.TryGetValue(name, out var id) ? (double)Data.Purchases[id][terminal - 1] / Data.PurchaseRuns : null;

    public static Comparison? Compare(string covenant, PlannedLineup team) =>
        team.Strategy == PlannerStrategy.Other && team.People == 8 ? Compare(covenant, team.Operators.Select(o => o.Name)) : null;

    internal static Comparison? Compare(string covenant, IEnumerable<string> members)
    {
        if (!CoreIds.TryGetValue(covenant, out var focus)
            || !Data.Routes.TryGetValue(focus, out var samples)) return null;
        var target = new HashSet<int>();
        foreach (var member in members)
        {
            if (!Ids.TryGetValue(member, out var id) || !target.Add(id)) return null;
        }
        if (target.Count == 0) return null;

        var seen = target.ToDictionary(id => id, _ => 0);
        var results = new RouteResult[2];
        for (int style = 0; style < 2; style++)
        {
            int runs = 0, active = 0, rounds = 0, leaks = 0, formed = 0, formRounds = 0, coverage = 0, clears = 0;
            foreach (var sample in samples.Where(s => s.Style == style))
            {
                runs++;
                if (sample.Activation > 0) { active++; rounds += sample.Activation; }
                leaks += sample.EarlyLeaks;
                if (sample.Clear) clears++;
                var acquired = new HashSet<int>();
                int best = 0;
                foreach (var hand in sample.Hands)
                {
                    int count = 0;
                    for (int i = 1; i < hand.Length; i++)
                        if (target.Contains(hand[i])) { count++; acquired.Add(hand[i]); }
                    if (count == target.Count && best < count) { formed++; formRounds += hand[0]; }
                    best = Math.Max(best, count);
                }
                coverage += best;
                foreach (int id in acquired) seen[id]++;
            }
            results[style] = new(runs, active, rounds, leaks, formed, formRounds, coverage, clears);
        }
        var least = seen.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key).First();
        return new(results[0], results[1], $"{Data.Names[least.Key]}（{least.Value}/{samples.Length} 局）",target.Count);
    }
}
