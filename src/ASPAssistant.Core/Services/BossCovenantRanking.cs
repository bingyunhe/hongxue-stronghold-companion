using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Services;

internal static class BossCovenantRanking
{
    internal sealed record Entry(AutoPlanCandidate Candidate, string Note = "", int? Preference = null);

    private static readonly Dictionary<(string, bool), (int Clears, int Runs)> Totals = BossCovenantSamples.Counts
        .GroupBy(kv => (kv.Key.Boss, kv.Key.High))
        .ToDictionary(g => g.Key, g => (g.Sum(kv => kv.Value.Clears), g.Sum(kv => kv.Value.Runs)));

    internal static (string Note, int? Preference) Match(string covenant, string boss, int? level)
    {
        if (level is not (4 or 8 or 11 or 14 or 16 or 18 or 20)) return ("", null);
        bool high = level >= 16;
        if (!Totals.TryGetValue((boss, high), out var total)) return ("", null);
        if (!BossCovenantSamples.Counts.TryGetValue((boss, high, covenant), out var sample))
            return ($"{boss} · 此本家暂无 SP5 模拟样本，保留常规排名", null);
        // Compare with the other sampled main covenants against this boss. Small differences retain the
        // ordinary planner order. The two-proportion z threshold is a ranking heuristic for simulator data.
        int otherRuns = total.Runs - sample.Runs;
        double gap = (double)sample.Clears / sample.Runs - (double)(total.Clears - sample.Clears) / otherRuns;
        double pooled = (double)total.Clears / total.Runs;
        double margin = 1.96 * Math.Sqrt(pooled * (1 - pooled) * (1.0 / sample.Runs + 1.0 / otherRuns));
        int preference = Math.Abs(gap) > margin ? Math.Sign(gap) : 0;
        string levels = high ? "16/18/20" : "4/8/11/14";
        string standing = preference switch { 1 => "同档优先", -1 => "同档靠后", _ => "同档常规" };
        return ($"{boss} · SP5 规程 {levels} 模拟：{sample.Runs} 局中 {sample.Clears} 局通关（{standing}）", preference);
    }

    internal static Entry[] Rank(IReadOnlyList<AutoPlanCandidate> candidates, string boss, int? level)
    {
        var entries = candidates.Select(c => new Entry(c)).ToArray();
        for (int i = 0; i < entries.Length; i++)
        {
            var candidate = candidates[i];
            if (candidate.Final.Strategy != PlannerStrategy.Other) continue;
            var (note, preference) = Match(candidate.MainCovenant, boss, level);
            entries[i] = new(candidate, note, preference);
        }

        // Only comparable, measured candidates exchange slots. Unknown covenants retain their planner rank.
        var groups = entries.Select((entry, index) => (entry, index)).Where(v => v.entry.Preference.HasValue)
            .GroupBy(v => (v.entry.Candidate.MainGoal.Level, v.entry.Candidate.Final.FreeSlots,
                v.entry.Candidate.Final.MissingHealer, v.entry.Candidate.Final.Transforms));
        foreach (var group in groups)
        {
            var ranked = group.OrderByDescending(v => v.entry.Preference).Select(v => v.entry);
            foreach (var (slot, entry) in group.Select(v => v.index).Zip(ranked)) entries[slot] = entry;
        }
        return entries;
    }
}
