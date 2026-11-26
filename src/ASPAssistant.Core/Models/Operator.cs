using System.Text.Json.Serialization;

namespace ASPAssistant.Core.Models;

public class TraitEntry
{
    [JsonPropertyName("traitType")]
    public string TraitType { get; set; } = "";

    [JsonPropertyName("traitDescription")]
    public string TraitDescription { get; set; } = "";
}

public class OperatorVariant
{
    [JsonPropertyName("traits")]
    public List<TraitEntry> Traits { get; set; } = [];

    /// <summary>Skill of this variant (from data/operator_profiles.json; null when unknown).</summary>
    [JsonIgnore]
    public OperatorSkill? Skill { get; set; }

    [JsonIgnore]
    public bool HasSkill => Skill is not null;
}

public sealed record CustomRosterEntry(string Name, int? Tier = null, string? SkillId = null, int? Slot = null);
public sealed record CustomSkillOption(string Id, OperatorSkill Normal, OperatorSkill Elite, byte[] Signature);

public class Operator : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    [JsonIgnore] public CustomRosterEntry? RosterEntry { get; private set; }
    [JsonIgnore] public bool? CarriedThisRun { get; private set; }
    [JsonIgnore] public CustomRosterEntry? RunEntry { get; private set; }
    [JsonIgnore] public IReadOnlyList<CustomSkillOption> SkillOptions { get; set; } = [];
    [JsonIgnore] public bool IsTierPending => IsCustom && (RunEntry ?? RosterEntry)?.Tier is null;
    [JsonIgnore] public bool IsAvailableForPlanning => !IsCustom || (CarriedThisRun ?? (IsSelectedCustom && RosterEntry is not { Tier: null }));
    [JsonIgnore] public string CarryLabel => CarriedThisRun == true ? "已携带" : "本局携带";

    public void SetRosterEntry(CustomRosterEntry? entry)
    {
        RosterEntry = entry;
        IsSelectedCustom = entry is not null;
        UpdateCustomDetails();
    }

    public void SetRunEntry(bool? carried, CustomRosterEntry? entry = null)
    {
        CarriedThisRun = carried;
        RunEntry = carried == true ? entry : null;
        UpdateCustomDetails();
    }

    private void UpdateCustomDetails()
    {
        var chosen = RunEntry ?? RosterEntry;
        Tier = chosen?.Tier ?? 5;
        var skill = SkillOptions.FirstOrDefault(x => x.Id == (RunEntry?.SkillId ?? RosterEntry?.SkillId));
        Normal.Skill = skill?.Normal;
        Elite.Skill = skill?.Elite;
        if (Profile is not null)
        {
            Profile.NormalSkill = Normal.Skill;
            Profile.EliteSkill = Elite.Skill;
        }
        PropertyChanged?.Invoke(this, new(string.Empty));
    }
    [JsonPropertyName("isCustom")]
    public bool IsCustom { get; set; }

    [JsonIgnore]
    public bool IsSelectedCustom { get; set; }

    [JsonIgnore]
    public bool IsAvailableForSelection => !IsCustom || IsSelectedCustom || CarriedThisRun == true;

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("tier")]
    public int Tier { get; set; }

    [JsonPropertyName("requiredStrategyId")]
    public string RequiredStrategyId { get; set; } = "";

    [JsonPropertyName("coreCovenants")]
    public List<string> CoreCovenants { get; set; } = [];

    /// <summary>Primary core covenant (first entry), empty string if none.</summary>
    [JsonIgnore]
    public string CoreCovenant => CoreCovenants.FirstOrDefault() ?? "";

    [JsonPropertyName("additionalCovenants")]
    public List<string> AdditionalCovenants { get; set; } = [];

    [JsonPropertyName("normal")]
    public OperatorVariant Normal { get; set; } = new();

    [JsonPropertyName("elite")]
    public OperatorVariant Elite { get; set; } = new();

    [JsonPropertyName("iconPath")]
    public string IconPath { get; set; } = "";

    /// <summary>Class, skill and planner roles (from data/operator_profiles.json; null when unknown).</summary>
    [JsonIgnore]
    public OperatorProfile? Profile { get; set; }

    [JsonIgnore]
    public bool HasProfile => Profile is not null;
}
