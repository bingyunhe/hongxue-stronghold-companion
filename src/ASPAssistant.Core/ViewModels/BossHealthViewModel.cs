using System.Collections.ObjectModel;
using ASPAssistant.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ASPAssistant.Core.ViewModels;

public partial class BossHealthViewModel : ObservableObject
{
    /// <summary>单人：极速模拟与规程等级 1–20。</summary>
    [ObservableProperty]
    private ObservableCollection<BossHpCell> _bossHpSingle = [];

    /// <summary>多人：标准 / 险境 / 绝境 / 终极模拟。</summary>
    [ObservableProperty]
    private ObservableCollection<BossHpCell> _bossHpMulti = [];

    [ObservableProperty]
    private bool _bossHasHiddenForm;

    public int? RegulationLevel
    {
        get => CurrentMode is { } mode && mode.StartsWith("规程等级") ? int.Parse(mode["规程等级".Length..]) : null;
        set => CurrentMode = value is { } level ? $"规程等级{level}" : null;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentBossHp))]
    private BossHpCell? _currentBossHp;

    public bool HasCurrentBossHp => CurrentBossHp is not null;
    public IEnumerable<BossHpCell> OtherBossHpSingle => BossHpSingle.Where(cell => cell.Name != CurrentBossHp?.Name);

    [ObservableProperty]
    private bool _otherBossHpExpanded = true;

    private void RefreshCurrentBossHp()
    {
        CurrentBossHp = BossHpSingle.FirstOrDefault(cell => cell.Name == CurrentMode);
        OtherBossHpExpanded = CurrentBossHp is null;
        OnPropertyChanged(nameof(OtherBossHpSingle));
    }

    /// <summary>Mode name read from the shop HUD (规程等级 N / …模拟); highlights that HP cell.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RegulationLevel))]
    [NotifyPropertyChangedFor(nameof(IsMultiplayerMode))]
    private string? _currentMode;

    public bool IsMultiplayerMode => CurrentMode is "标准模拟" or "险境模拟" or "绝境模拟" or "终极模拟";

    partial void OnCurrentModeChanged(string? value)
    {
        MarkCurrentMode();
        RefreshCurrentBossHp();
    }

    private void MarkCurrentMode()
    {
        foreach (var list in new[] { BossHpSingle, BossHpMulti })
            for (int i = 0; i < list.Count; i++)
                if (list[i].IsCurrent != (list[i].Name == CurrentMode))
                    list[i] = list[i] with { IsCurrent = list[i].Name == CurrentMode };
    }

    public void SetHealthLevels(IReadOnlyList<BossHpLevel> levels)
    {
        BossHpSingle = new(levels.Where(h => h.Group != "multi").Select(BossHpCell.From));
        BossHpMulti = new(levels.Where(h => h.Group == "multi").Select(BossHpCell.From));
        BossHasHiddenForm = levels.Any(h => h.HiddenHp.HasValue);
        MarkCurrentMode();
        RefreshCurrentBossHp();
    }
}
