using ASPAssistant.Core.Models;
using ASPAssistant.Core.ViewModels;

namespace ASPAssistant.Core.Services;

public sealed record CustomCarrySuggestion(string Name, int Tier, string Reason);

public static class CustomCarryAdvisor
{
    public static IReadOnlyList<CustomCarrySuggestion> Recommend(Lineup lineup, IReadOnlyList<Operator> operators,
        CovenantCalculator calculator, BanViewModel bans, PlannerStrategy strategy)
    {
        bool Available(Operator op) => bans.UnavailableReason(op, strategy) == null;
        var options = operators.Where(o => o.IsCustom && o.RosterEntry?.Tier is 5 or 6 && Available(o))
            .OrderBy(o => o.Name, StringComparer.Ordinal).ToList();
        var final = lineup.Slots.Where(s => !s.IsReserve).ToList();
        var stages = lineup.EarlySlots.Concat(lineup.MidSlots).Concat(lineup.SupportSlots).Concat(lineup.Slots).ToList();
        var names = operators.Where(o => !o.IsCustom && Available(o)).Select(o => o.Name).ToHashSet();
        var baseline = final.Where(s => names.Contains(s.OperatorName)).ToList();
        foreach (var piece in operators.Where(o => o.RequiredStrategyId == PlannerStrategies.StrategyId(strategy) && names.Contains(o.Name)))
            if (baseline.All(s => s.OperatorName != piece.Name)) baseline.Add(new() { OperatorName = piece.Name });
        var targets = LineupTargets.States(lineup, calculator, _ => false, bans.BannedCovenants, bans.MissingCovenants)
            .Where(t => !t.CovenantBanned).ToList();

        HashSet<string> Reached(List<Operator> picks)
        {
            if (targets.Count == 0 || baseline.Count + picks.Count > 9) return [];
            var projected = new Lineup { Slots = [.. baseline, .. picks.Select(op =>
                final.FirstOrDefault(s => s.OperatorName == op.Name) ?? new LineupSlot { OperatorName = op.Name })] };
            var counts = calculator.Compute(projected).ToDictionary(stat => stat.Name, stat => stat.Count);
            return targets.Where(t => counts.GetValueOrDefault(t.Name) >= t.Needed).Select(t => t.Label).ToHashSet();
        }

        var picks = new List<Operator>();
        var best = new List<Operator>();
        var bestGoals = Reached(best);
        var bestScore = (0, 0, bestGoals.Count, 0, 0, 0);
        void Visit(int start, int five, int six)
        {
            for (int i = start; i < options.Count; i++)
            {
                var op = options[i];
                bool isFive = op.RosterEntry!.Tier == 5;
                if (isFive ? five == 2 : six == 2) continue;
                picks.Add(op);
                var reached = Reached(picks);
                var score = (picks.Count(o => stages.Any(s => s.OperatorName == o.Name && !s.IsReserve && s.Tags.Contains(LineupTag.阵容核心))),
                    picks.Count(o => final.Any(s => s.OperatorName == o.Name)), reached.Count,
                    picks.Count(o => stages.Any(s => s.OperatorName == o.Name && !s.IsReserve)),
                    picks.Count(o => stages.Any(s => s.OperatorName == o.Name)), -picks.Count);
                if (score.CompareTo(bestScore) > 0) { bestScore = score; best = [.. picks]; bestGoals = reached; }
                Visit(i + 1, five + (isFive ? 1 : 0), six + (isFive ? 0 : 1));
                picks.RemoveAt(picks.Count - 1);
            }
        }
        Visit(0, 0, 0);
        return best.Select(op =>
        {
            var slots = stages.Where(s => s.OperatorName == op.Name).ToList();
            var goal = bestGoals.Except(Reached(best.Where(o => o != op).ToList())).FirstOrDefault();
            string reason = slots.Any(s => !s.IsReserve && s.Tags.Contains(LineupTag.阵容核心)) ? "阵容核心"
                : final.Any(s => s.OperatorName == op.Name) ? "成型阵容需要"
                : goal != null ? $"搭配目标阵容可达{goal}" : slots.Any(s => !s.IsReserve) ? "阶段阵容需要" : "攻略替补";
            return new CustomCarrySuggestion(op.Name, op.RosterEntry!.Tier!.Value, reason);
        }).ToList();
    }
}
