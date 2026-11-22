using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;

namespace ASPAssistant.Core.ViewModels;

public sealed record MapPivotChoice(Lineup Lineup, string Source, string[] Members, string[] Held, string[] Missing,
    int RoleGaps, int MissingCore, int BeyondTerminal, int ActiveGoals, string Reason, string Replay, double? PurchaseRate,
    int BossPreference, double? ReplayCoverage)
{
    public string Label => $"{Lineup.Name} · {Held.Length}/{Members.Length}";
}

public partial class LineupPlannerViewModel
{
    public Task<IReadOnlyList<MapPivotChoice>> FindMapPivotsAsync(LineupViewModel library,
        IReadOnlyDictionary<string, int> holdings, IReadOnlySet<string> elites, int people, int terminal,
        CancellationToken token)
    {
        var saved = library.Lineups.Where(l => !library.HasRunInfo || library.RecommendationFor(l)?.Kind == RecommendationKind.Usable).ToArray();
        var baseline = BuildAutoRequest();
        var goals = HasGoals && !IsRepairing ? BuildRequest() : null;
        var cachedGoals = !_dirty && PendingSearch is { IsCompletedSuccessfully: true } ? _lastResult : null;
        var allowed = _operators.Concat(_strategyOperators).Where(o => o.IsAvailableForSelection && !IsUnavailable(o))
            .DistinctBy(o => o.Name).ToDictionary(o => o.Name);
        var anchors = holdings.Keys.Where(n => allowed.TryGetValue(n, out var op) && op.RequiredStrategyId.Length == 0)
            .OrderByDescending(elites.Contains).ThenByDescending(n => allowed[n].Tier).ThenBy(n => n, StringComparer.Ordinal).Take(2).ToArray();
        var requiredLocks = baseline.LockedOperators;
        var (boss, regulation) = _bossContext;
        return Task.Run<IReadOnlyList<MapPivotChoice>>(() =>
        {
            var choices = new List<MapPivotChoice>();
            var planned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var lineup in saved)
            {
                token.ThrowIfCancellationRequested();
                Add(lineup, "我的阵容");
            }
            if (goals != null)
            {
                var results = cachedGoals ?? _planner.Plan(goals, token);
                string label = string.Join("、", goals.Goals.Select(g => g.Name + (g.Level == 2 ? "Ⅱ" : "Ⅰ")));
                foreach (var variant in results.Variants.Where(v => v.People == people))
                    for (int i = 0; i < variant.Lineups.Count; i++)
                        AddPlan(variant.Lineups[i], label, $"{label} · {variant.Transforms}转 · 方案{i + 1}", "手动规划");
            }
            var lockSets = new List<IReadOnlySet<string>> { requiredLocks };
            var anchored = requiredLocks.Concat(anchors).ToHashSet();
            if (anchored.Count <= LineupPlanner.MaxLockedOperators && !anchored.SetEquals(requiredLocks)) lockSets.Add(anchored);
            foreach (var locks in lockSets)
            {
                var request = new AutoPlanRequest
                {
                    People = people, Strategy = baseline.Strategy, MechElite = baseline.MechElite, LockedOperators = locks,
                    BannedCovenants = baseline.BannedCovenants, BannedOperators = baseline.BannedOperators,
                    TransformAllowed = baseline.TransformAllowed, ExcludeMixedBannedOperators = baseline.ExcludeMixedBannedOperators
                };
                foreach (var candidate in _autoPlanner.Plan(request, token).Candidates)
                    AddPlan(candidate.Final, candidate.MainCovenant, candidate.MainCovenant + " · 抓牌转型", "规划");
            }
            return choices.OrderBy(c => c.RoleGaps).ThenBy(c => c.MissingCore).ThenBy(c => c.BeyondTerminal)
                .ThenByDescending(c => c.Held.Length).ThenBy(c => c.Missing.Length)
                .ThenByDescending(c => c.ActiveGoals).ThenByDescending(c => c.BossPreference)
                .ThenByDescending(c => c.PurchaseRate ?? -1).ThenByDescending(c => c.ReplayCoverage ?? -1)
                .ThenBy(c => c.Source != "我的阵容").ThenBy(c => c.Lineup.Name, StringComparer.Ordinal).ToArray();

            void AddPlan(PlannedLineup team, string goal, string name, string source)
            {
                var lineup = new PlannerLineupItem { Source = team, LineupName = name }.ToLineup(_equipmentRules);
                lineup.TargetCovenants = team.Goals.Select(g => new TargetCovenant { Name = g.Name, Tier = g.Level }).ToList();
                var key = string.Join("|", lineup.Slots.Select(s => s.OperatorName + ":" + string.Join(",", s.Equipments)).Order(StringComparer.Ordinal));
                lineup.Id = $"pivot/{baseline.Strategy}/{goal}/{key}";
                if (planned.Add(lineup.Id)) Add(lineup, source);
            }

            void Add(Lineup lineup, string source)
            {
                token.ThrowIfCancellationRequested();
                var final = lineup.SklandGuide?.Stages.FirstOrDefault(s => s.IsFinal);
                var slots = (final?.Slots ?? lineup.Slots).Where(s => !s.IsReserve && s.OperatorName.Length > 0).ToList();
                if (slots.Count == 0 || slots.Count > people || slots.Any(s => !allowed.ContainsKey(s.OperatorName))
                    || lineup.Strategies.Any(s => s.StrategyId.Length > 0 && s.StrategyId != PlannerStrategies.StrategyId(baseline.Strategy))) return;
                var remaining = new Dictionary<string, int>(holdings);
                var held = new List<string>();
                var missing = new List<string>();
                var missingCore = new List<string>();
                foreach (var slot in slots.OrderByDescending(s => s.Tags.Contains(LineupTag.阵容核心)))
                {
                    var n = slot.OperatorName;
                    if (remaining.GetValueOrDefault(n) > 0) { remaining[n]--; held.Add(n); }
                    else
                    {
                        missing.Add(n);
                        if (slot.Tags.Contains(LineupTag.阵容核心) || requiredLocks.Contains(n)) missingCore.Add(n);
                    }
                }
                // Planned transformations are equipment requirements, not proof of an active bond on the board.
                var liveBonds = library.Calculator.Compute(new Lineup { Slots = held.Distinct().Select(n => new LineupSlot { OperatorName = n }).ToList() })
                    .Where(c => c.Activated).Select(c => c.Name).ToList();
                var needs = new List<string>();
                void MissingRole(string role, Func<Operator, bool> matches)
                {
                    if (held.Any(n => matches(allowed[n]))) return;
                    var cards = missing.Distinct().Where(n => matches(allowed[n])).ToList();
                    needs.Add(cards.Count > 0 ? role + "（待抓" + string.Join("或", cards) + "）" : role + "（目标中未配置）");
                }
                MissingRole("前排", o => o.Profile?.IsTank == true);
                if (!liveBonds.Contains(LineupPlanner.HealingCovenant)) MissingRole("治疗", o => o.Profile?.IsHealer == true);
                var later = missing.Where(n => terminal > 0 && allowed[n].Tier > terminal).ToArray();
                var rate = missing.Select(n => PlannerReplayEvidence.EarlyPurchaseRate(n, terminal)).ToArray();
                double? scarce = rate.Length > 0 && rate.All(r => r.HasValue) ? rate.Min() : null;
                var reason = (needs.Count == 0 ? "前排、治疗已齐" : $"当前缺{string.Join("、", needs)}")
                    + (missingCore.Count > 0 ? $"；核心或必带缺卡：{string.Join("、", missingCore)}" : "")
                    + (liveBonds.Count > 0 ? $"；持有人选可激活{string.Join("、", liveBonds)}" : "；持有人选暂未激活盟约")
                    + (later.Length > 0 ? $"；超出当前终端：{string.Join("、", later.Distinct())}" : "");
                string replay = scarce.HasValue
                    ? $"SP3 回放 · {missing[Array.IndexOf(rate, scarce)]}在 {terminal} 本及以前的历史购买比例 {scarce:P1}"
                    : missing.Count == 0 ? "目标人员已齐" : "当前缺卡暂无同终端的购买回放";
                var main = library.Calculator.Compute(new Lineup { Slots = slots, Strategies = lineup.Strategies }).Where(c => c.BondType == "SEASON" && c.Activated)
                    .OrderByDescending(c => lineup.TargetCovenants.Any(t => t.Name == c.Name)).ThenByDescending(c => c.Count)
                    .FirstOrDefault()?.Name;
                int bossPreference = 0;
                double? coverage = null;
                if (main != null && baseline.Strategy == PlannerStrategy.Other)
                {
                    var match = BossCovenantRanking.Match(main, boss, regulation);
                    bossPreference = match.Preference ?? 0;
                    if (match.Preference.HasValue) replay += $"\n{main} · {match.Note}";
                    if (regulation == 8 && slots.Count == 8 && PlannerReplayEvidence.Compare(main, slots.Select(s => s.OperatorName)) is { } sp3)
                    {
                        var runs = sp3.Core.Runs + sp3.Small.Runs;
                        coverage = (double)(sp3.Core.Coverage + sp3.Small.Coverage) / runs;
                        replay += $"\nSP3 规程 8 · {runs} 局单人模拟，平均最多同时持有这套 {coverage:0.#}/8 人";
                    }
                }
                choices.Add(new(lineup, source, slots.Select(s => s.OperatorName).ToArray(), held.ToArray(), missing.ToArray(),
                    needs.Count, missingCore.Count, later.Length, liveBonds.Count, reason, replay, scarce, bossPreference, coverage));
            }
        }, token);
    }
}
