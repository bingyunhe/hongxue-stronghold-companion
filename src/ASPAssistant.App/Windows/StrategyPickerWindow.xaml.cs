// Added for ASP Assistant Arknights on 2026-11-13; see NOTICE.
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;

namespace ASPAssistant.App.Windows;

public sealed class StrategyPickRow(Strategy strategy) : INotifyPropertyChanged
{
    public Strategy Strategy { get; } = strategy;
    public string Id => Strategy.Id;
    public string Name => Strategy.Name;
    public string Initiator => Strategy.Initiator;
    public string IconPath => Strategy.IconPath;
    public int InitialHp => Strategy.InitialHp;
    public string Description => Strategy.Description.Replace('\n', ' ');
    public bool IsActive => !Strategy.Description.StartsWith("被动", StringComparison.Ordinal);
    public string KindText => IsActive ? "主动" : "被动";
    public string Summary => $"{Name} · {Initiator}\n初始生命值：{InitialHp}\n{Strategy.Description}";

    private bool _chosen;
    internal LineupStrategySlot Slot { get; } = new() { StrategyId = strategy.Id };
    public List<StrategyTag> Tags => Slot.Tags;
    public bool IsChosen { get => _chosen; set { _chosen = value; if (!value) Tags.Clear(); NotifyAll(); } }
    public StrategyTag? Tag
    {
        get => Tags.Count > 0 ? Tags.Min() : null;
        set { Tags.Clear(); if (value is { } tag) Tags.Add(tag); NotifyAll(); }
    }
    public bool IsCore { get => Tag == StrategyTag.阵容核心; set => Tag = value ? StrategyTag.阵容核心 : null; }
    public bool IsPreferred { get => Tag == StrategyTag.优选; set => Tag = value ? StrategyTag.优选 : null; }
    public bool IsSecond { get => Tag == StrategyTag.次选; set => Tag = value ? StrategyTag.次选 : null; }
    public string TagText => Tag switch { StrategyTag.阵容核心 => "核心", StrategyTag.优选 => "优选", StrategyTag.次选 => "次选", _ => "" };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void NotifyAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>The lineup's strategies at once: click to add / remove, then 核心 / 优选 / 次选 on the chosen ones.</summary>
public partial class StrategyPickerWindow : Window
{
    private readonly List<StrategyPickRow> _rows;
    private readonly ObservableCollection<StrategyPickRow> _shown = [];
    private readonly ObservableCollection<StrategyPickRow> _chosen = [];
    // Keep the lineup's order (including unknown shared strategies) independent of the display order.
    private readonly List<LineupStrategySlot> _slots = [];

    /// <summary>The picked strategies in order (set when 完成 is clicked).</summary>
    public IReadOnlyList<LineupStrategySlot> Picked { get; private set; } = [];

    public StrategyPickerWindow(IEnumerable<Strategy> strategies, IEnumerable<LineupStrategySlot> current)
    {
        InitializeComponent();
        WindowUiScale.PrepareCentered(this);
        var rows = strategies.Select(s => new StrategyPickRow(s)).ToList();
        foreach (var slot in current)
        {
            if (rows.FirstOrDefault(r => r.Id == slot.StrategyId) is not { } row)
            {
                _slots.Add(new LineupStrategySlot { StrategyId = slot.StrategyId, Tags = [.. slot.Tags] });
                continue;
            }
            row.IsChosen = true;
            row.Tags.AddRange(slot.Tags);
            _slots.Add(row.Slot);
        }
        _rows = [.. rows.OrderByDescending(r => r.IsChosen).ThenBy(r => r.Tag is { } tag ? (int)tag : int.MaxValue)];
        foreach (var row in _rows.Where(r => r.IsChosen)) _chosen.Add(row);
        ItemsList.ItemsSource = _shown;
        ChosenList.ItemsSource = _chosen;
        ApplyFilter();
        Loaded += (_, _) => { SearchBox.Focus(); Keyboard.Focus(SearchBox); };
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (_rows is null) return;
        var query = SearchBox.Text;
        var kind = KindList.SelectedIndex;
        _shown.Clear();
        foreach (var row in _rows)
        {
            if (kind == 1 && row.IsActive || kind == 2 && !row.IsActive) continue;
            if (!SearchMatch.Matches(query, row.Name, row.Initiator) && !row.Strategy.Description.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            _shown.Add(row);
        }
        EmptyHint.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnItemClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ItemsList, e.OriginalSource as DependencyObject) is not ListBoxItem { Content: StrategyPickRow row }) return;
        for (var d = e.OriginalSource as DependencyObject; d is not null && d != ItemsList; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is ToggleButton) return;
        if (!row.IsChosen && _slots.Count >= LineupEditorViewModel.MaxStrategySlots)
        {
            HintText.Text = $"最多选择{LineupEditorViewModel.MaxStrategySlots}个策略，请先移出一个";
            return;
        }
        HintText.Text = $"单击加入或移出，可选多个（最多{LineupEditorViewModel.MaxStrategySlots}个）";
        row.IsChosen = !row.IsChosen;
        if (row.IsChosen)
        {
            _chosen.Add(row);
            _slots.Add(row.Slot);
        }
        else
        {
            _chosen.Remove(row);
            _slots.Remove(row.Slot);
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        foreach (var r in _chosen.ToList()) r.IsChosen = false;
        _chosen.Clear();
        _slots.Clear();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        Picked = _slots;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
