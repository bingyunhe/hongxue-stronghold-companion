// Added for Hongxue Stronghold Companion on 2026-11-17; see NOTICE.
using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.ViewModels;

public partial class TrackingViewModel
{
    private string? _stageLineupId;
    public bool StageTargetsUpdating { get; private set; }
    public event Action? StageStateChanged;
    public IReadOnlyList<TrackingStageChoice> StageChoices { get; private set; } = [];
    public TrackingStageChoice? SelectedStageChoice => StageChoices.FirstOrDefault(c => c.LineupId == _stageLineupId
        && _trackedLineups.Any(l => l.Id == c.LineupId && l.SelectedStageId == c.StageId));
    public bool AutomaticStageSwitch => _trackedLineups.FirstOrDefault(l => l.Id == _stageLineupId)?.AutomaticStageSwitch == true;

    public string StageSwitchHint => !AutomaticStageSwitch ? "" : !_trackedLineups.Single(l => l.Id == _stageLineupId).Stages.Any(s => s.StartRound.HasValue)
        ? "阵容未设置回合范围，请手动选择阶段" : Economy?.Round is { } round ? $"当前第 {round} 回合 · 自动跟随" : "等待识别当前回合";

    public static IReadOnlyList<SklandGuideStage> LineupStages(Lineup lineup)
    {
        var definitions = lineup.SklandGuide?.Stages ?? lineup.EditorStages ??
            [new() { Id = "early", Name = "前期" }, new() { Id = "mid", Name = "中期" },
             new() { Id = "final", Name = "成型阵容", IsFinal = true }, new() { Id = "support", Name = "挂件" }];
        return definitions.Select(s => new SklandGuideStage { Id = s.Id,
            Name = s.IsFinal && s.Name == "大成" ? "成型阵容" : s.Name, IsFinal = s.IsFinal, OperationIdea = s.OperationIdea,
            StartRound = s.StartRound, EndRound = s.EndRound, RecommendedEquipments = [.. s.RecommendedEquipments],
            Slots = lineup.SklandGuide is not null ? s.Slots : s.IsFinal ? lineup.Slots : s.Id switch
            {
                "early" => lineup.EarlySlots, "mid" => lineup.MidSlots, "final" => lineup.Slots,
                "support" => lineup.SupportSlots, _ => s.Slots
            } }).ToList();
    }

    private void RefreshStageChoices()
    {
        var sources = _trackedLineups.Where(l => l.Stages.Count > 0).ToList();
        if (!sources.Any(l => l.Id == _stageLineupId)) _stageLineupId = sources.LastOrDefault()?.Id;
        StageChoices = sources.SelectMany(l => l.Stages.Select(s => new TrackingStageChoice(l.Id, s.Id,
            sources.Count > 1 ? $"{l.Name} · {s.Name}" : s.Name))).ToList();
        OnPropertyChanged(nameof(StageChoices));
        OnPropertyChanged(nameof(SelectedStageChoice));
        OnPropertyChanged(nameof(AutomaticStageSwitch));
    }

    public void SelectStage(TrackingStageChoice choice)
    {
        int index = _trackedLineups.FindIndex(l => l.Id == choice.LineupId);
        var source = _trackedLineups[index];
        _stageLineupId = source.Id;
        _trackedLineups[index] = source with { SelectedStageId = choice.StageId, AutomaticStageSwitch = false };
        RefreshStageTargets();
    }

    public void SetAutomaticStageSwitch(bool enabled)
    {
        int index = _trackedLineups.FindIndex(l => l.Id == _stageLineupId);
        if (_trackedLineups[index].AutomaticStageSwitch == enabled) return;
        _trackedLineups[index] = _trackedLineups[index] with { AutomaticStageSwitch = enabled };
        if (FollowStageRound()) return;
        RefreshStageChoices();
        StageStateChanged?.Invoke();
    }

    private bool FollowStageRound()
    {
        if (Economy?.Round is not { } round) return false;
        bool changed = false;
        for (int i = 0; i < _trackedLineups.Count; i++)
        {
            var source = _trackedLineups[i];
            if (!source.AutomaticStageSwitch) continue;
            var ranged = source.Stages.Where(s => !s.IsFinal && s.StartRound.HasValue).ToList();
            if (ranged.Count == 0) continue;
            var next = ranged.Where(s => s.StartRound <= round && s.EndRound >= round).OrderBy(s => s.StartRound).LastOrDefault();
            if (next is null && round > ranged.Max(s => s.EndRound)) next = source.Stages.Single(s => s.IsFinal);
            if (next is null || next.Id == source.SelectedStageId) continue;
            _trackedLineups[i] = source with { SelectedStageId = next.Id };
            changed = true;
        }
        if (changed) RefreshStageTargets();
        return changed;
    }

    private void RefreshStageTargets()
    {
        StageTargetsUpdating = true;
        try
        {
            for (int i = 0; i < TrackedOperators.Count; i++)
            {
                var entry = TrackedOperators[i];
                if (entry.Origins.Count == 0) continue;
                var updated = MergeOrigins(entry, entry.Origins);
                TrackedOperators[i] = updated;
            }
            RefreshExpiry();
            RefreshEquipmentRequirements();
            SyncDeployPool();
        }
        finally { StageTargetsUpdating = false; }
        RefreshStageChoices();
        StageStateChanged?.Invoke();
    }
}
