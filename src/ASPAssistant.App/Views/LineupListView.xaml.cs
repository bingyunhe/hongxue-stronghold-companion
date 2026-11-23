// Modified for ASP Assistant Arknights on 2026-11-13; see NOTICE.
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;

namespace ASPAssistant.App.Views;

/// <summary>
/// 我的阵容: search and 筛选 by target covenant, 本局推荐 once the opening bans are known, and the saved lineups.
/// </summary>
public partial class LineupListView : UserControl
{
    private const int RecommendShown = 3;

    public event Action? NewLineupRequested;
    public event Action? ImportLineupRequested;
    public event Action<Lineup>? EditLineupRequested;
    public event Action<Lineup>? OpenAutoRouteRequested;
    public event Action<Lineup>? ShareLineupRequested;
    public event Action<Lineup>? PublishLineupRequested;
    public event Action<Lineup>? TrackLineupRequested;
    public event Action<Lineup>? DeleteLineupRequested;
    public event Action<IReadOnlyList<Lineup>>? DeleteSelectedRequested;
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private bool _selecting;
    /// <summary>修补: open the planner with this lineup's surviving operators locked.</summary>
    public event Action<LineupRecommendation>? RepairLineupRequested;

    public event Action<Lineup, int>? MoveLineupRequested;

    public static readonly DependencyProperty TrackedLineupIdsProperty = DependencyProperty.Register(
        nameof(TrackedLineupIds), typeof(IReadOnlySet<string>), typeof(LineupListView), new PropertyMetadata(new HashSet<string>()));
    public IReadOnlySet<string> TrackedLineupIds => (IReadOnlySet<string>)GetValue(TrackedLineupIdsProperty);
    private TrackingViewModel? _tracking;

    public void SetTracking(TrackingViewModel tracking)
    {
        if (_tracking is not null) _tracking.PropertyChanged -= OnTrackingChanged;
        _tracking = tracking;
        _tracking.PropertyChanged += OnTrackingChanged;
        SetValue(TrackedLineupIdsProperty, tracking.TrackedLineupIds);
    }

    private void OnTrackingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackingViewModel.TrackedLineupIds))
            SetValue(TrackedLineupIdsProperty, _tracking!.TrackedLineupIds);
    }

    private LineupViewModel? _vm;
    private bool _rebuildQueued;
    private bool _showAllRecommendations;
    private bool _syncingSegments;

    public LineupListView()
    {
        InitializeComponent();
        DataContextChanged += OnViewDataContextChanged;
        FilterToggle.Checked += (_, _) => UpdateTagsRow();
        FilterToggle.Unchecked += (_, _) => UpdateTagsRow();
    }

    private void OnViewDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.FilteredLineups.CollectionChanged -= OnListChanged;
            _vm.Recommendations.CollectionChanged -= OnListChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _selected.Clear();
        _selecting = false;
        SetSelectionLayout();
        _vm = e.NewValue as LineupViewModel;
        if (_vm is not null)
        {
            _vm.FilteredLineups.CollectionChanged += OnListChanged;
            _vm.Recommendations.CollectionChanged += OnListChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
        SyncSegments();
        Rebuild();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LineupViewModel.SelectedAvailabilityFilter)) _showAllRecommendations = false;
        if (e.PropertyName is nameof(LineupViewModel.FilterTier) or nameof(LineupViewModel.FilterMatchAll)) SyncSegments();
        if (e.PropertyName is nameof(LineupViewModel.HasActiveFilters)) UpdateTagsRow();
    }

    /// <summary>The view model refills its lists item by item; rebuild the cards once afterwards.</summary>
    private void OnListChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _rebuildQueued = false; Rebuild(); });
    }

    public void RefreshAllBadges() => Rebuild();

    /// <summary>Scrolls back to the top so 本局推荐 is in view (after the opening info is recorded).</summary>
    public void ShowRecommendations(LineupAvailabilityFilter? selectedFilter = null)
    {
        if (_vm is not null)
            _vm.SelectedAvailabilityFilter = selectedFilter ?? (!_vm.HasRunInfo ? LineupAvailabilityFilter.All
                : _vm.Recommendations.Any(r => r.Kind is RecommendationKind.BannedButFine or RecommendationKind.NeedsRepair)
                    ? LineupAvailabilityFilter.Pending
                    : _vm.Recommendations.Any(r => r.Kind == RecommendationKind.Usable)
                        ? LineupAvailabilityFilter.Available : LineupAvailabilityFilter.Unavailable);
        _showAllRecommendations = false;
        Rebuild();
        if (FindScroll(this) is { } sv) sv.ScrollToTop();
    }

    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScroll(child) is { } found) return found;
        }
        return null;
    }

    private void Rebuild()
    {
        if (_vm is null) return;
        var recs = _vm.Recommendations.ToDictionary(r => r.Lineup.Id);

        // 本局推荐
        bool hasRun = !_selecting && _vm.HasRunInfo && _vm.Lineups.Count > 0
            && _vm.SelectedAvailabilityFilter == LineupAvailabilityFilter.Pending;
        RecommendSection.Visibility = hasRun ? Visibility.Visible : Visibility.Collapsed;
        var matching = _vm.FilteredLineups.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        _selected.IntersectWith(matching);
        if (hasRun)
        {
            var recommendations = _vm.Recommendations.Where(r => matching.Contains(r.Lineup.Id)).ToList();
            var shown = _showAllRecommendations ? recommendations : recommendations.Take(RecommendShown).ToList();
            RecommendItems.ItemsSource = shown.Select((r, i) => LineupCardItem.From(r.Lineup, r, _vm, isRecommendation: true,
                isBest: i == 0 && r.Kind <= RecommendationKind.BannedButFine)).ToList();
            RecommendMeta.Text = $"{recommendations.Count} 套待确认";
            RecommendMore.Content = _showAllRecommendations ? "收起" : $"全部 {recommendations.Count} 套";
            RecommendMore.Visibility = recommendations.Count > RecommendShown ? Visibility.Visible : Visibility.Collapsed;
            BannedAvatars.ItemsSource = _vm.RunBannedNames.Select(n => new LineupAvatarItem(n, false)).ToList();
            BannedCovenantsList.ItemsSource = _vm.RunBannedCovenants.ToList();
            BannedStrip.Visibility = _vm.RunBannedNames.Count + _vm.RunBannedCovenants.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else RecommendItems.ItemsSource = Array.Empty<LineupCardItem>();

        // 全部
        LineupItems.ItemsSource = hasRun ? Array.Empty<LineupCardItem>() : _vm.FilteredLineups
            .Select(l => LineupCardItem.From(l, recs.GetValueOrDefault(l.Id), _vm, isRecommendation: false, isBest: false,
                selected: _selecting ? _selected.Contains(l.Id) : null)).ToArray();
        int total = _vm.Lineups.Count, matched = _vm.FilteredLineups.Count;
        bool filtered = matched != total;
        CountText.Text = filtered ? $"符合 {matched} 套" : $"{total} 套阵容";
        TotalText.Text = filtered ? $"· 共 {total} 套" : "";
        if (_selecting) UpdateSelectedCount();
        EmptyHint.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoMatchHint.Visibility = total > 0 && matched == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateTagsRow();
    }

    private void SetSelectionLayout()
    {
        BulkDelete.Visibility = BulkCancel.Visibility = BulkAll.Visibility = _selecting ? Visibility.Visible : Visibility.Collapsed;
        BulkMode.Style = (Style)FindResource(_selecting ? "LineupPrimaryButton" : "LineupTopButton");
        Grid.SetColumnSpan(CountGroup, _selecting ? 2 : 1);
        Grid.SetRow(ListActions, _selecting ? 1 : 0);
        Grid.SetColumn(ListActions, _selecting ? 0 : 1);
        Grid.SetColumnSpan(ListActions, _selecting ? 2 : 1);
        ListActions.Margin = new Thickness(0, _selecting ? 5 : 0, 0, 0);
    }

    internal void EndSelection()
    {
        _selecting = false;
        _selected.Clear();
        SetSelectionLayout();
        Rebuild();
    }

    private void OnBulkMode(object sender, RoutedEventArgs e)
    {
        if (_selecting) { EndSelection(); return; }
        _selecting = true;
        SetSelectionLayout();
        Rebuild();
    }

    private void UpdateSelectedCount()
    {
        CountText.Text = $"筛选结果 {_vm!.FilteredLineups.Count} 套 · 已选 {_selected.Count} 套";
        TotalText.Text = "";
        BulkDelete.IsEnabled = _selected.Count > 0;
        BulkAll.IsEnabled = _selected.Count < _vm.FilteredLineups.Count;
    }

    private void OnBulkCheck(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        var id = ((LineupCardItem)box.DataContext).Lineup.Id;
        if (box.IsChecked == true) _selected.Add(id);
        else _selected.Remove(id);
        UpdateSelectedCount();
    }

    private void OnBulkAll(object sender, RoutedEventArgs e)
    {
        _selected.UnionWith(_vm!.FilteredLineups.Select(l => l.Id));
        Rebuild();
    }

    internal Lineup[] SelectedVisible() => _vm!.FilteredLineups.Where(l => _selected.Contains(l.Id)).ToArray();

    private void OnBulkDelete(object sender, RoutedEventArgs e)
    {
        var selected = SelectedVisible();
        if (selected.Length > 0) DeleteSelectedRequested?.Invoke(selected);
    }

    private void UpdateTagsRow()
        => TagsRow.Visibility = FilterToggle.IsChecked != true && _vm?.HasActiveFilters == true ? Visibility.Visible : Visibility.Collapsed;

    private void SyncSegments()
    {
        if (_vm is null) return;
        _syncingSegments = true;
        TierList.SelectedIndex = Math.Clamp(_vm.FilterTier, 0, 2);
        MatchModeList.SelectedIndex = _vm.FilterMatchAll ? 1 : 0;
        _syncingSegments = false;
    }

    private void OnTierChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSegments || _vm is null || TierList.SelectedIndex < 0) return;
        _vm.FilterTier = TierList.SelectedIndex;
    }

    private void OnMatchModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSegments || _vm is null || MatchModeList.SelectedIndex < 0) return;
        _vm.FilterMatchAll = MatchModeList.SelectedIndex == 1;
    }

    private void OnResetFilter(object sender, RoutedEventArgs e) => _vm?.ClearFilters();
    private void OnCloseFilter(object sender, RoutedEventArgs e) => FilterToggle.IsChecked = false;

    private void OnRemoveTag(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: string tag }) _vm?.RemoveTag(tag);
    }

    private void OnToggleAllRecommendations(object sender, RoutedEventArgs e)
    {
        _showAllRecommendations = !_showAllRecommendations;
        Rebuild();
    }

    private void OnMoveButtonLoaded(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = _vm!.CanMove(((LineupCardItem)button.DataContext).Lineup, int.Parse((string)button.CommandParameter));
    }

    private void OnMoveLineup(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        MoveLineupRequested?.Invoke(((LineupCardItem)button.DataContext).Lineup, int.Parse((string)button.CommandParameter));
    }

    private void OnClearHighlights(object sender, RoutedEventArgs e) => _tracking!.ClearLineupHighlights();

    private void OnNewLineup(object sender, RoutedEventArgs e) => NewLineupRequested?.Invoke();
    private void OnImportLineup(object sender, RoutedEventArgs e) => ImportLineupRequested?.Invoke();

    private static LineupCardItem? Card(object sender) => (sender as FrameworkElement)?.DataContext as LineupCardItem;

    private void OnEditLineup(object sender, RoutedEventArgs e) { if (Card(sender) is { } c) EditLineupRequested?.Invoke(c.Lineup); }
    private void OnOpenAutoRoute(object sender, RoutedEventArgs e) { if (Card(sender) is { } c) OpenAutoRouteRequested?.Invoke(c.Lineup); }
    private void OnShareLineup(object sender, RoutedEventArgs e)
    {
        if (Card(sender) is not { } card) return;
        var menu = new ContextMenu { PlacementTarget = (Button)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var link = new MenuItem { Header = "复制分享链接" };
        link.Click += (_, _) => ShareLineupRequested?.Invoke(card.Lineup);
        var publish = new MenuItem { Header = "发布到森空岛…" };
        publish.Click += (_, _) => PublishLineupRequested?.Invoke(card.Lineup);
        menu.Items.Add(link);
        menu.Items.Add(publish);
        menu.IsOpen = true;
    }
    private void OnTrackLineup(object sender, RoutedEventArgs e) { if (Card(sender) is { } c) TrackLineupRequested?.Invoke(c.Lineup); }
    private void OnDeleteLineup(object sender, RoutedEventArgs e) { if (Card(sender) is { } c) DeleteLineupRequested?.Invoke(c.Lineup); }
    private void OnRepairLineup(object sender, RoutedEventArgs e)
    {
        if (Card(sender) is { Recommendation: { } r }) RepairLineupRequested?.Invoke(r);
    }
}

public sealed record LineupAvatarItem(string Name, bool IsBanned)
{
    public string IconPath => $"icons/operators/{Name}.png";
    public string ToolTip => IsBanned ? $"{Name}（本局被禁）" : Name;
}

/// <summary>A target covenant pill. Tone: ok / warn / bad / accent (core, no run) / neutral.</summary>
public sealed record LineupTargetPill(string Name, string Text, string Tone, string? ToolTip);

public sealed class LineupCardItem
{
    public required Lineup Lineup { get; init; }
    public LineupRecommendation? Recommendation { get; init; }
    public string Name => Lineup.Name.Length > 0 ? Lineup.Name : "未命名阵容";
    public string SizeText => $"{Lineup.Slots.Count(s => s.OperatorName.Length > 0)} 人";
    public required IReadOnlyList<LineupTargetPill> Targets { get; init; }
    public required IReadOnlyList<LineupAvatarItem> Avatars { get; init; }
    public bool HasStatus => Recommendation is not null;
    public string StatusText => Recommendation?.StatusText ?? "";
    public string Tone => Recommendation?.Kind switch
    {
        RecommendationKind.Usable => "ok",
        RecommendationKind.BannedButFine => "accent",
        RecommendationKind.NeedsRepair => "warn",
        RecommendationKind.Broken => "bad",
        _ => "neutral",
    };
    public string Note => Recommendation?.Note ?? "";
    public bool HasNote => Note.Length > 0;
    public bool CanRepair => Recommendation is { Kind: RecommendationKind.NeedsRepair };
    public bool IsBest { get; init; }
    /// <summary>Share / delete live on the full list; 本局推荐 keeps to 修补 / 追踪 / 编辑.</summary>
    public bool ShowManage { get; init; }
    public bool ShowSelection { get; init; }
    public bool IsSelected { get; init; }
    public bool HasAutoRoute => Lineup.AutoRoute is not null;

    public static LineupCardItem From(Lineup lineup, LineupRecommendation? rec, LineupViewModel vm, bool isRecommendation, bool isBest, bool? selected = null)
    {
        List<LineupTargetPill> targets;
        if (rec is not null)
            targets = [.. rec.Targets.Select(t => t.CovenantBanned
                ? new LineupTargetPill(t.Name, t.Label, "bad", $"本局{t.Name}不完整")
                : t.Reached
                    ? new LineupTargetPill(t.Name, t.Label, "ok", $"{t.Count}/{t.Needed} 人")
                    : new LineupTargetPill(t.Name, $"{t.Label} 差 {t.Missing}", "warn", $"{t.Count}/{t.Needed} 人"))];
        else
        {
            var core = vm.Calculator.AllCovenants.Where(c => c.BondType == "SEASON").Select(c => c.Name).ToHashSet();
            targets = [.. vm.TargetsOf(lineup).Select(t => new LineupTargetPill(t.Name,
                $"{t.Name} {(t.Tier >= 2 ? "Ⅱ" : "Ⅰ")}", core.Contains(t.Name) ? "accent" : "neutral", null))];
        }
        var banned = rec?.Banned.ToHashSet() ?? [];
        return new LineupCardItem
        {
            Lineup = lineup,
            Recommendation = rec,
            Targets = targets,
            Avatars = [.. lineup.Slots.Where(s => s.OperatorName.Length > 0)
                .Select(s => new LineupAvatarItem(s.OperatorName, banned.Contains(s.OperatorName)))],
            ShowSelection = selected.HasValue,
            IsSelected = selected == true,
            IsBest = isBest,
            ShowManage = !isRecommendation,
        };
    }
}
