using System.Windows;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.ViewModels;

namespace ASPAssistant.App.Windows;

public partial class SidePanelWindow
{
    internal SemaphoreSlim GuideSaveGate { get; } = new(1, 1);
    private TrackingGuideWindow? _trackingGuide;
    internal event Action<string>? TrayNoticeRequested;

    internal void ShowTrackingGuide(bool toggle)
    {
        if (TrackingVm.CurrentMapLineup is not { Stages.Count: > 0 } && _trackingGuide?.HasAnyDrafts != true)
        {
            const string message = "请先追踪一个阵容";
            ShowToast(message);
            if (IsAssistantHidden || WindowState == WindowState.Minimized) TrayNoticeRequested?.Invoke(message);
            return;
        }
        if (_trackingGuide == null)
        {
            _trackingGuide = new TrackingGuideWindow(this, _settingsManager);
            _trackingGuide.Closed += (_, _) => _trackingGuide = null;
        }
        _trackingGuide.Present(toggle);
    }

    private void RefreshTrackingGuide() => _trackingGuide?.SyncTracking();
    internal void GuideTrayChanged(bool hidden) => _trackingGuide?.TrayChanged(hidden);

    // An editor can remain open while the independent guide saves another draft.
    private void MergeGuideNotes(Lineup original, Lineup latest, Lineup edited)
    {
        var before = TrackingViewModel.LineupStages(original).ToDictionary(s => s.Id);
        var current = TrackingViewModel.LineupStages(latest);
        foreach (var stage in current)
        {
            if (!before.TryGetValue(stage.Id, out var baseline) || stage.OperationIdea == baseline.OperationIdea) continue;
            var sections = edited.SklandGuide?.Stages ?? (edited.EditorStages ??= TrackingViewModel.LineupStages(edited).ToList());
            var changed = sections.FirstOrDefault(s => s.Id == stage.Id);
            if (changed == null || changed.OperationIdea == stage.OperationIdea) continue;
            if (changed.OperationIdea != baseline.OperationIdea
                && ConfirmDialog.Ask(this, "阶段讲解有两份修改", $"「{stage.Name}」已有新讲解。使用编辑器文字，还是保留已保存的文字？", "使用编辑器文字")) continue;
            changed.OperationIdea = stage.OperationIdea;
        }
    }
}
