using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;

namespace ASPAssistant.Core.ViewModels;

public partial class LineupPlannerViewModel
{
    private Lineup RepairFinal(Lineup source, PlannerLineupItem item)
    {
        var copy = JsonSerializer.Deserialize<Lineup>(JsonSerializer.Serialize(source))!;
        copy.Slots = JsonSerializer.Deserialize<List<LineupSlot>>(JsonSerializer.Serialize(RepairSlots(source, item.ToLineup(_equipmentRules).Slots)))!;
        copy.TargetCovenants = [.. _repairTargets.Select(t => t.Clone())];
        if (copy.SklandGuide is { } guide) guide.Stages.Single(s => s.IsFinal).Slots = JsonSerializer.Deserialize<List<LineupSlot>>(JsonSerializer.Serialize(copy.Slots))!;
        if (copy.EditorStages is { } stages) stages.Single(s => s.IsFinal).Slots = JsonSerializer.Deserialize<List<LineupSlot>>(JsonSerializer.Serialize(copy.Slots))!;
        return copy;
    }

    private void InvalidateStageRepairs()
    {
        PreviewStageRepairCommand.Cancel();
        foreach (var item in VisibleLineups) item.StageRepair = null;
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task PreviewStageRepair(PlannerLineupItem? item, CancellationToken token)
    {
        if (RepairSource is not { } source || item is null || !item.RequiresStageRepair || !VisibleLineups.Contains(item)) return;
        item.IsStageRepairBusy = true;
        item.StageRepair = null;
        var original = JsonSerializer.Deserialize<Lineup>(JsonSerializer.Serialize(source))!;
        var repaired = RepairFinal(source, item);
        var request = BuildRequest();
        var repairer = new LineupStageRepairer([.. _operators, .. _strategyOperators], _repairCalculator);
        try
        {
            var preview = await Task.Run(() => repairer.Repair(original, repaired, request.BannedOperators, request.BannedCovenants, token, request.TransformAllowed), token);
            if (!token.IsCancellationRequested && ReferenceEquals(RepairSource, source) && VisibleLineups.Contains(item)) item.StageRepair = preview;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { item.IsStageRepairBusy = false; }
    }
}
