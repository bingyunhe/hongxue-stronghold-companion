using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ASPAssistant.App.Controls;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.ViewModels;

namespace ASPAssistant.App.Windows;

public partial class CustomOperatorSelectionWindow : Window
{
    private readonly OperatorBrowseViewModel _filters = new() { SelectedOperatorSource = OperatorSourceFilter.CustomCandidates };
    private readonly List<(Border Card, CheckBox Check, Action Sync)> _rows = [];
    private readonly Dictionary<string, CustomRosterEntry> _opening;
    private readonly Dictionary<string, int?> _pinnedTiers;
    public IReadOnlyList<string>? Result { get; private set; }

    public IReadOnlyList<CustomRosterEntry>? Entries { get; private set; }
    private readonly Dictionary<string, CustomRosterEntry> _draft;
    private bool _updating;

    public CustomOperatorSelectionWindow(IEnumerable<Operator> candidates, IReadOnlyList<CustomRosterEntry>? proposed = null)
    {
        var catalogue = candidates.ToList();
        _opening = (proposed ?? catalogue.Where(o => o.IsSelectedCustom)
            .Select(o => o.RosterEntry ?? new CustomRosterEntry(o.Name)).ToList()).ToDictionary(e => e.Name);
        _draft = new(_opening);
        _pinnedTiers = _opening.ToDictionary(pair => pair.Key, pair => pair.Value.Tier);
        _filters.LoadOperators(catalogue);
        InitializeComponent();
        DataContext = _filters;
        WindowUiScale.PrepareCentered(this);
        foreach (var op in catalogue.OrderBy(Group).ThenBy(o => o.Name, StringComparer.CurrentCulture))
        {
            var name = new TextBlock { Text = op.Name, FontSize = 12, FontWeight = FontWeights.Bold };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var covenants = new TextBlock
            {
                FontSize = 10, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
            };
            PanelToolTip.AppendNames(covenants, op.CoreCovenants.Concat(op.AdditionalCovenants), " · ");
            covenants.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            var body = new DockPanel();
            var avatar = new Avatar { Width = 30, Height = 30, Text = op.Name, Source = op.IconPath, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(avatar, Dock.Left);
            var badge = new ContentPresenter { ContentTemplate = (DataTemplate)FindResource("TrackingTierBadge") };
            DockPanel.SetDock(badge, Dock.Left);
            body.Children.Add(badge);
            body.Children.Add(avatar);
            var check = new CheckBox { Tag = op.Name, IsChecked = _draft.ContainsKey(op.Name), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,4,0) };
            var slider = new ListBox { Style = (Style)FindResource("SegmentedListBox"), VerticalAlignment = VerticalAlignment.Center };
            foreach (string label in new[] { "V", "VI" })
            {
                var segment = new ListBoxItem { Content = label };
                segment.PreviewMouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    segment.Focus();
                    slider.SelectedItem = segment;
                };
                slider.Items.Add(segment);
            }
            var skills = new ComboBox { FontSize = 10, MinWidth = 75, MaxWidth = 128,
                HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "本编队携带技能" };
            skills.Items.Add("技能待确认");
            foreach (var option in op.SkillOptions) skills.Items.Add(option.Normal.Name);
            var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, covenants, skills } };
            body.Children.Add(identity);
            var layout = new DockPanel();
            DockPanel.SetDock(check, Dock.Left); layout.Children.Add(check);
            DockPanel.SetDock(slider, Dock.Right); layout.Children.Add(slider);
            layout.Children.Add(body);

            var card = new Border
            {
                Width = 274, Padding = new Thickness(8), Margin = new Thickness(0, 0, 8, 8),
                BorderThickness = new Thickness(1), Child = layout, Tag = op
            };
            card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            card.SetResourceReference(Border.CornerRadiusProperty, "RowCornerRadius");

            void Sync()
            {
                _updating = true;
                var entry = _draft.GetValueOrDefault(op.Name);
                check.IsChecked = entry != null;
                slider.SelectedIndex = entry?.Tier - 5 ?? -1;
                skills.SelectedIndex = entry?.SkillId == null ? 0 : op.SkillOptions.ToList().FindIndex(s => s.Id == entry.SkillId) + 1;
                skills.Visibility = entry != null && op.SkillOptions.Count != 0 ? Visibility.Visible : Visibility.Collapsed;
                covenants.Visibility = skills.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                badge.Content = entry?.Tier is int tier ? new Operator { Tier = tier } : null;
                _updating = false;
            }
            Sync();
            slider.SelectionChanged += (_, _) =>
            {
                if (_updating || slider.SelectedIndex < 0) return;
                int tier = 5 + slider.SelectedIndex;
                var prior = _draft.GetValueOrDefault(op.Name);
                if ((prior == null && _draft.Count == 12) || _draft.Values.Count(e => e.Tier == tier && e.Name != op.Name) >= 6)
                { Sync(); TierCount.Text = $"{tier} 阶或编队名额已满"; return; }
                var original = _opening.GetValueOrDefault(op.Name);
                _draft[op.Name] = new(op.Name, tier, prior?.SkillId, original?.Tier == tier ? original.Slot : null);
                Sync(); UpdateSelection(card);
            };
            check.Checked += (_, _) =>
            {
                if (_updating) return;
                _draft.TryAdd(op.Name, new(op.Name, _draft.Values.Count(e => e.Tier == 5) < 6 ? 5 : 6));
                Sync(); UpdateSelection(card);
            };
            check.Unchecked += (_, _) => { if (!_updating) { _draft.Remove(op.Name); Sync(); UpdateSelection(); } };
            skills.SelectionChanged += (_, _) =>
            {
                if (!_updating && _draft.TryGetValue(op.Name, out var e))
                {
                    _draft[op.Name] = e with { SkillId = skills.SelectedIndex > 0 ? op.SkillOptions[skills.SelectedIndex-1].Id : null };
                    UpdateResetButton();
                }
            };
            System.Windows.Automation.AutomationProperties.SetName(check, op.Name);
            _rows.Add((card, check, Sync));
            ChoicesPanel.Children.Add(card);
        }
        UpdateSelection();
        FilterChoices();
        _filters.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_filters.MatchedCount)) FilterChoices();
        };
        Loaded += (_, _) => SearchBox.Focus();
    }

    // An operator keeps their first pinned position for the lifetime of this draft.
    private int Group(Operator op) => _pinnedTiers.TryGetValue(op.Name, out var tier) ? tier ?? 7 : 8;

    private void UpdateSelection(Border? selected = null)
    {
        if (selected?.Tag is Operator op && _pinnedTiers.TryAdd(op.Name, _draft[op.Name].Tier))
        {
            int group = Group(op);
            int target = ChoicesPanel.Children.Cast<Border>().Select(card => (Operator)card.Tag)
                .Count(other => Group(other) < group || Group(other) == group && StringComparer.CurrentCulture.Compare(other.Name, op.Name) < 0);
            if (ChoicesPanel.Children.IndexOf(selected) != target)
            {
                ChoicesPanel.Children.Remove(selected);
                ChoicesPanel.Children.Insert(target, selected);
            }
        }
        var count = _draft.Count;
        foreach (var row in _rows) row.Check.IsEnabled = row.Check.IsChecked == true || count < 12;
        UpdateResetButton();
        TierCount.Text = $"V 阶 {_draft.Values.Count(e => e.Tier == 5)}/6　 VI 阶 {_draft.Values.Count(e => e.Tier == 6)}/6";
        SelectionCount.Text = $"已选择 {count} / 12 名";
    }

    private void UpdateResetButton() =>
        ResetDraftButton.IsEnabled = _pinnedTiers.Count != _opening.Count || _draft.Count != _opening.Count
            || _draft.Values.Any(entry => _opening.GetValueOrDefault(entry.Name) != entry);

    private void OnResetDraft(object sender, RoutedEventArgs e)
    {
        _draft.Clear();
        _pinnedTiers.Clear();
        foreach (var entry in _opening.Values)
        {
            _draft.Add(entry.Name, entry);
            _pinnedTiers.Add(entry.Name, entry.Tier);
        }
        ChoicesPanel.Children.Clear();
        foreach (var row in _rows)
        {
            row.Sync();
            ChoicesPanel.Children.Add(row.Card);
        }
        UpdateSelection();
    }

    private void FilterChoices()
    {
        var matches = _filters.FilteredOperators.ToHashSet();
        foreach (Border card in ChoicesPanel.Children)
            card.Visibility = matches.Contains((Operator)card.Tag) ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.Check.IsChecked = false;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_draft.Count > 12 || _draft.Values.Where(e => e.Tier.HasValue).GroupBy(e => e.Tier).Any(g => g.Count() > 6))
        {
            TierCount.Text = "请调整编队：V、VI 阶各最多 6 名，共最多 12 名";
            return;
        }
        Entries = _draft.Values.ToList();
        Result = _rows.Where(row => row.Check.IsChecked == true).Select(row => (string)row.Check.Tag).ToList();
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
