// Added for ASP Assistant Arknights on 2026-11-13; see NOTICE.
namespace ASPAssistant.Core.Models;

public sealed class TrackingState
{
    public List<TrackingEntry> Entries { get; set; } = [];
    public List<TrackedLineup> Lineups { get; set; } = [];
    public bool ShowAllStages { get; set; }
    public List<string> PurchasedEquipment { get; set; } = [];
    public string? StageLineupId { get; set; }
    public string? UpgradeReminderLineupId { get; set; }
    public List<UpgradeReminderProgress> UpgradeReminderProgress { get; set; } = [];
}

public sealed record TrackedLineup(string Id, string Name, bool Highlighted = true)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Lineup? GuideSource { get; init; }
    public List<TrackedLineupStage> Stages { get; init; } = [];
    public string SelectedStageId { get; init; } = "";
    public bool AutomaticStageSwitch { get; init; }
    public List<TerminalUpgradeReminder> UpgradeReminders { get; init; } = [];
    public bool UpgradeNotificationsEnabled { get; init; }
}

/// <summary>An empty LineupId records an independent target; older unassociated targets remain independent.</summary>
public sealed record TrackingOrigin
{
    public string LineupId { get; init; } = "";
    public string StageId { get; init; } = "";
    public bool IsRoute { get; init; }
    public bool IsElite { get; init; }
    public TrackingRole Role { get; init; }
    public string Stages { get; init; } = "";
    public int ExpiresAt { get; init; }
    public string? TransformedCovenant { get; init; }
    public List<string> Equipments { get; init; } = [];
    public List<CandidateEquipment> CandidateEquipments { get; init; } = [];

    public static TrackingOrigin From(TrackingEntry entry, string lineupId) => new()
    {
        LineupId = lineupId, IsElite = entry.IsElite, Role = entry.Role, Stages = entry.Stages,
        ExpiresAt = entry.ExpiresAt, TransformedCovenant = entry.TransformedCovenant, Equipments = [.. entry.Equipments],
        CandidateEquipments = [.. entry.CandidateEquipments.Select(c => c.Clone())]
    };
}

public sealed record TrackedLineupStage(string Id, string Name, bool IsFinal, int? StartRound, int? EndRound)
{
    public List<string> RecommendedEquipments { get; init; } = [];
}
public sealed record TrackingStageChoice(string LineupId, string StageId, string Label);
