// Added for Hongxue Stronghold Companion on 2026-11-17; see NOTICE.
using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.ViewModels;

public partial class TrackingViewModel
{
    private readonly HashSet<string> _purchasedEquipment = new(StringComparer.Ordinal);
    private bool _showAllStages;
    public bool ShowAllStages
    {
        get => _showAllStages;
        set
        {
            if (!SetProperty(ref _showAllStages, value)) return;
            SourcesChanged?.Invoke();
        }
    }

    public IReadOnlyList<TrackingEntry> DisplayOperators => ShowAllStages
        ? TrackedOperators.Select(e =>
        {
            var item = e.Origins.Count > 0 ? MergeOrigins(e, e.Origins, true) : Copy(e, false);
            item.IsAcquired = _deployPool is null ? e.IsAcquired && (!item.IsElite || e.IsElite)
                : item.IsElite ? _deployPool.IsElite(item.Name) : _deployPool.IsOwned(item.Name);
            return item;
        }).ToList() : TrackedOperators.Where(e => e.IsStageActive).ToList();

    public List<TrackingEntry> ShopTrackingEntries => ShowAllStages
        ? [.. DisplayOperators, .. TrackedEquipment.Where(e => e.IsStageActive && !e.IsExpired)]
        : [.. ActiveEntries];

    public IReadOnlyList<TrackingEntry> DisplayEquipment
    {
        get
        {
            if (!ShowAllStages) return TrackedEquipment.Where(e => e.IsStageActive).ToList();
            var result = TrackedEquipment.Where(e => e.IsManual).ToDictionary(e => e.Name, e => Copy(e, false));
            foreach (var (name, kind) in EquipmentRequirements(DisplayOperators, true))
            {
                if (result.ContainsKey(name)) continue;
                var entry = TrackedEquipment.FirstOrDefault(e => e.Name == name);
                var item = entry is null ? new TrackingEntry { Name = name, Type = TrackingType.Equipment, IsManual = false } : Copy(entry, false);
                item.EquipmentKind = kind;
                item.IsAcquired = item.IsAcquired || _purchasedEquipment.Contains(name);
                result.Add(name, item);
            }
            return result.Values.ToList();
        }
    }

    public void RecordEquipmentPurchase(string name)
    {
        if (TrackedEquipment.Any(e => e.Name == name && !e.IsAcquired)) { ToggleAcquired(name); return; }
        if (!_purchasedEquipment.Add(name)) return;
        OnPropertyChanged(nameof(DisplayEquipment));
        SourcesChanged?.Invoke();
    }
}
