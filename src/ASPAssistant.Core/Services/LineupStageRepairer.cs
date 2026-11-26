using System.Text.Json;
using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Services;

public sealed record StageRepairChange(string Name, string BoundsText, string MembersText, string ChangeText, string WarningText, int Missing)
{
    public bool HasUnavailableTransformation { get; init; }
    public string CovenantComparison { get; init; } = "";
    public bool HasWarning => WarningText.Length > 0;
}

public sealed record StageRepairPreview(Lineup Lineup, IReadOnlyList<StageRepairChange> Stages)
{
    public bool CanSave => Stages.All(s => s.Missing == 0 && !s.HasUnavailableTransformation);
    public string Summary => CanSave ? "各阶段修补已就绪，可另存、覆盖或追踪" : "部分阶段仍有缺口，请调整原阶段或本局禁用后重新修补";
}

/// <summary>Repairs saved stages within their original headcount and highest tier; never infers a terminal from rounds.</summary>
public sealed class LineupStageRepairer(IReadOnlyList<Operator> operators, CovenantCalculator calculator)
{
    public StageRepairPreview Repair(Lineup source, Lineup repaired, IReadOnlySet<string> bannedOperators,
        IReadOnlySet<string> bannedCovenants, CancellationToken token = default, IReadOnlySet<string>? transformAllowed = null)
    {
        var copy = JsonSerializer.Deserialize<Lineup>(JsonSerializer.Serialize(repaired))!;
        var byName = operators.GroupBy(o => o.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var definitions = source.SklandGuide?.Stages ?? source.EditorStages ??
            [new() { Id = "early", Name = "前期" }, new() { Id = "mid", Name = "中期" },
             new() { Id = "final", Name = "成型阵容", IsFinal = true }, new() { Id = "support", Name = "挂件" }];
        List<LineupSlot> Slots(SklandGuideStage s) => source.SklandGuide is not null ? s.Slots : s.IsFinal ? source.Slots : s.Id switch
        {
            "early" => source.EarlySlots, "mid" => source.MidSlots, "support" => source.SupportSlots, _ => s.Slots
        };
        bool Available(Operator op) => op.IsAvailableForPlanning && !bannedOperators.Contains(op.Name)
            && !CovenantBanRules.IsExcluded(op, bannedCovenants)
            && (op.RequiredStrategyId.Length == 0 || source.Strategies.Any(s => s.StrategyId == op.RequiredStrategyId));
        List<CovenantStat> Counts(IEnumerable<LineupSlot> slots) => [.. calculator.Compute(new() { Slots = [.. slots], Strategies = source.Strategies })];
        string Compare(IReadOnlyList<CovenantStat> before, IReadOnlyList<CovenantStat> after)
        {
            var original = before.ToDictionary(c => c.Name, c => c.Count);
            var current = after.ToDictionary(c => c.Name, c => c.Count);
            return LineupSubstitutionAdvice.DescribeChanges(calculator.AllCovenants
                .Where(c => original[c.Name] != current[c.Name])
                .Select(c => new CovenantCountChange(c, original[c.Name], current[c.Name])));
        }
        var indices = calculator.AllCovenants.Select((c, i) => (c.Name, Index: i)).ToDictionary(c => c.Name, c => c.Index, StringComparer.Ordinal);
        var cores = calculator.AllCovenants.Select((c, i) => (Covenant: c, Index: i))
            .Where(c => c.Covenant.BondType == "SEASON" && c.Covenant.Name != CovenantCalculator.HarmonyCovenant).Select(c => c.Index).ToList();
        (int[] Values, int Harmony) Contributions(IEnumerable<LineupSlot> slots)
        {
            var counts = Counts(slots);
            var harmony = counts.Where(c => c.Name == CovenantCalculator.HarmonyCovenant).Select(c => c.Count).DefaultIfEmpty(0).Single();
            var values = counts.Take(calculator.AllCovenants.Count).Select(c => c.Count).ToArray();
            foreach (var i in cores) if (values[i] > 0) values[i] -= harmony;
            return (values, harmony);
        }
        var finalNames = copy.Slots.Select(s => s.OperatorName).ToHashSet(StringComparer.Ordinal);
        var finalCounts = Counts(copy.Slots).Where(c => c.Activated).ToList();
        var reservedCustom = finalNames.Where(n => byName.TryGetValue(n,out var op) && op.IsCustom).ToHashSet(StringComparer.Ordinal);
        bool HasRoom(Operator op) => !op.IsCustom || reservedCustom.Contains(op.Name) ||
            reservedCustom.Count(n => byName[n].Tier == op.Tier) < 2;
        var next = finalNames;
        var changes = new Dictionary<string, StageRepairChange>(StringComparer.Ordinal);
        var resolved = new Dictionary<string, List<LineupSlot>>(StringComparer.Ordinal);
        foreach (var stage in definitions.Where(s => !s.IsFinal).Reverse())
        {
            token.ThrowIfCancellationRequested();
            var original = Slots(stage);
            var unknown = original.Where(s => !byName.ContainsKey(s.OperatorName)).Select(s => s.OperatorName).ToList();
            var maximum = original.Where(s => byName.ContainsKey(s.OperatorName)).Select(s => byName[s.OperatorName].Tier).DefaultIfEmpty(0).Max();
            var kept = new List<LineupSlot>();
            foreach (var slot in original)
            {
                if (!byName.TryGetValue(slot.OperatorName, out var op) || !Available(op) || !HasRoom(op)) continue;
                kept.Add(slot);
                if (op.IsCustom) reservedCustom.Add(op.Name);
            }
            var keptNames = kept.Select(s => s.OperatorName).ToHashSet(StringComparer.Ordinal);
            var originalCounts = Counts(original);
            var goals = originalCounts.Where(c => c.Activated && !LineupPlanner.NoAdditionalScore.Contains(c.Name)).ToList();
            var pool = operators.Where(o => o.RequiredStrategyId.Length == 0 && o.Tier <= maximum && Available(o) && HasRoom(o) && !keptNames.Contains(o.Name))
                .GroupBy(o => o.Name).Select(g => g.First()).OrderBy(o => o.Name, StringComparer.Ordinal).ToList();
            var needed = unknown.Count > 0 ? 0 : Math.Min(original.Count - kept.Count, pool.Count);
            List<List<int>> beam = [[]];
            var keptCounts = Contributions(kept);
            var additionsCounts = pool.Select(o => Contributions([new LineupSlot { OperatorName = o.Name }])).ToArray();
            var originalGoals = goals.Select(g => (Index: indices[g.Name], g.Count)).ToList();
            var finalGoals = finalCounts.Select(g => (Index: indices[g.Name], g.Count)).ToList();
            (int Original, int Final, int Next, int FinalNames) Score(List<int> selected)
            {
                Span<int> counts = stackalloc int[keptCounts.Values.Length];
                keptCounts.Values.AsSpan().CopyTo(counts);
                var harmony = keptCounts.Harmony;
                foreach (var i in selected)
                {
                    var addition = additionsCounts[i]; harmony += addition.Harmony;
                    for (int j = 0; j < counts.Length; j++) counts[j] += addition.Values[j];
                }
                foreach (var i in cores) if (counts[i] > 0) counts[i] += harmony;
                int originalScore = 0, finalScore = 0;
                foreach (var g in originalGoals) originalScore += Math.Min(g.Count, counts[g.Index]);
                foreach (var g in finalGoals) finalScore += Math.Min(g.Count, counts[g.Index]);
                return (originalScore, finalScore, selected.Count(i => next.Contains(pool[i].Name)), selected.Count(i => finalNames.Contains(pool[i].Name)));
            }
            // A bounded beam handles several missing holders together without searching every combination.
            for (int depth = 0; depth < needed; depth++)
            {
                var candidates = new List<(List<int> Selection, (int, int, int, int) Score)>();
                foreach (var selected in beam)
                    for (int i = selected.Count == 0 ? 0 : selected[^1] + 1; i <= pool.Count - (needed - depth); i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var op = pool[i];
                        if (op.IsCustom && !reservedCustom.Contains(op.Name) && reservedCustom.Count(n => byName[n].Tier == op.Tier)
                            + selected.Count(j => pool[j].IsCustom && pool[j].Tier == op.Tier && !reservedCustom.Contains(pool[j].Name)) >= 2) continue;
                        List<int> selection = [.. selected, i];
                        candidates.Add((selection, Score(selection)));
                    }
                if (candidates.Count == 0) break;
                beam = candidates.OrderByDescending(c => c.Score).Take(24).Select(c => c.Selection).ToList();
            }
            var additions = beam[0].Select(i => new LineupSlot { OperatorName = pool[i].Name }).ToList();
            foreach (var slot in additions.Where(s => byName[s.OperatorName].IsCustom)) reservedCustom.Add(slot.OperatorName);
            var remaining = new Queue<LineupSlot>(additions);
            var slots = new List<LineupSlot>();
            foreach (var slot in original)
            {
                if (keptNames.Contains(slot.OperatorName)) slots.Add(slot);
                else if (remaining.Count > 0) slots.Add(remaining.Dequeue());
            }
            var missing = original.Count - slots.Count;
            var removed = original.Where(s => !keptNames.Contains(s.OperatorName)).Select(s => s.OperatorName).ToList();
            var after = Counts(slots);
            var lost = goals.Where(g => after.Single(c => c.Name == g.Name).Count < g.ActivateCount).Select(g => g.Name).ToList();
            var warnings = new List<string>();
            if (missing > 0) warnings.Add($"缺 {missing} 人，未突破原人数和等阶限制");
            if (unknown.Count > 0) warnings.Add($"干员资料缺失：{string.Join("、", unknown)}，未猜测替补等阶");
            if (lost.Count > 0) warnings.Add($"原盟约未激活：{string.Join("、", lost)}");
            changes.Add(stage.Id, new(stage.Name, $"原 {original.Count} 人 · 替补 ≤ {maximum} 阶",
                slots.Count == 0 ? "空阶段" : string.Join("、", slots.Select(s => s.OperatorName)),
                removed.Count == 0 ? "保留原名单" : $"换下 {string.Join("、", removed)} → {(additions.Count == 0 ? "暂无合适替补" : string.Join("、", additions.Select(s => s.OperatorName)))}",
                string.Join("；", warnings), missing) { CovenantComparison = removed.Count == 0 ? "" : Compare(originalCounts, after) });
            resolved.Add(stage.Id, slots);
            if (slots.Count > 0) next = slots.Select(s => s.OperatorName).ToHashSet(StringComparer.Ordinal);
        }
        var savedStages = copy.SklandGuide?.Stages ?? copy.EditorStages;
        foreach (var stage in definitions)
        {
            var slots = stage.IsFinal ? copy.Slots : resolved[stage.Id];
            if (savedStages is not null)
                savedStages.Single(s => s.Id == stage.Id).Slots = JsonSerializer.Deserialize<List<LineupSlot>>(JsonSerializer.Serialize(slots))!;
            if (copy.SklandGuide is null)
                switch (stage.Id)
                {
                    case "early": copy.EarlySlots = slots; break;
                    case "mid": copy.MidSlots = slots; break;
                    case "support": copy.SupportSlots = slots; break;
                }
            var invalid = slots.SelectMany(calculator.EquipmentRules.Transformations)
                .Where(t => transformAllowed is not null && !transformAllowed.Contains(t.Covenant)).Select(t => t.Covenant).Distinct().ToList();
            var issue = invalid.Count > 0 ? $"本局不可转入：{string.Join("、", invalid)}" : "";
            if (!stage.IsFinal)
            {
                if (invalid.Count > 0)
                {
                    var change = changes[stage.Id];
                    changes[stage.Id] = change with { HasUnavailableTransformation = true,
                        WarningText = string.Join("；", new[] { change.WarningText, issue }.Where(x => x.Length > 0)) };
                }
                continue;
            }
            var before = Slots(stage).Select(s => s.OperatorName).ToHashSet(StringComparer.Ordinal);
            changes.Add(stage.Id, new(stage.Name, $"成型 {slots.Count} 人", string.Join("、", slots.Select(s => s.OperatorName)),
                $"换下 {string.Join("、", before.Except(finalNames))} → 补入 {string.Join("、", finalNames.Except(before))}", issue, 0) { HasUnavailableTransformation = invalid.Count > 0, CovenantComparison = Compare(Counts(Slots(stage)), Counts(slots)) });
        }
        // Keep retained loadouts independent from both the original lineup and the preview's temporary search values.
        copy = JsonSerializer.Deserialize<Lineup>(JsonSerializer.Serialize(copy))!;
        return new(copy, definitions.Select(s => changes[s.Id]).ToList());
    }
}
