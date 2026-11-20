using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ASPAssistant.App.Converters;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.ViewModels;

namespace ASPAssistant.App.Views;

public sealed record CodexBounty(string Id, string Title, string Subtitle, string Text, string Tag,
    int Amount, int Rounds, string Condition, string Icon, string Mechanism)
{
    public string DurationLabel => Rounds switch { 1 => "下场", 2 => "两场", _ => "每场" };
    public string RewardLabel => Condition switch { "击杀" => "击倒得", "完美" => "完美得", _ => "无奖励" };

    private static readonly Lazy<CodexBounty[]> Entries = new(ReadEntries);
    public static CodexBounty[] Load() => Entries.Value;

    private static CodexBounty[] ReadEntries()
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "data", "bounties.json"));
        using var json = JsonDocument.Parse(file);
        return json.RootElement.GetProperty("entries").Deserialize<CodexBounty[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
}

public partial class ReferenceCatalogueView : UserControl
{
    private sealed class Entry(string title, string subtitle, string text, string icon, string detailLabel, string details) : INotifyPropertyChanged
    {
        public string Title { get; } = title;
        public string Subtitle { get; } = subtitle;
        public string Text { get; } = text;
        public string Icon { get; } = icon;
        public string DetailLabel { get; } = detailLabel;
        public string Details { get; } = details;
        public int InitialHp { get; init; }
        public string Category { get; init; } = "";
        public BossInfo? Boss { get; init; }
        public CodexBounty? Bounty { get; init; }
        public string HealthSearchText { get; init; } = "";
        public BossHealthViewModel? Health { get; set; }
        public BitmapImage? Portrait { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetPortrait(BitmapImage? portrait)
        {
            Portrait = portrait;
            PropertyChanged?.Invoke(this, new(nameof(Portrait)));
        }
    }

    private Entry[] _rows = [];
    public ReferenceCatalogueView()
    {
        InitializeComponent();
        DetailsPopup.CustomPopupPlacementCallback = (size, target, offset) =>
            [new CustomPopupPlacement(new Point(-size.Width - 8 * VisualTreeHelper.GetDpi(this).DpiScaleX, 0), PopupPrimaryAxis.Vertical)];
        IsVisibleChanged += (_, _) => { if (!IsVisible) CloseDetails(); };
        Unloaded += (_, _) => CloseDetails();
    }

    private ILookup<string, CodexBounty>? _bountiesByName;
    private int? _amountFilter, _roundsFilter;
    private string? _conditionFilter;
    private bool _pinned;
    private EnemyViewModel? _runEnemy;
    private Window? _popupOwner;

    internal async Task SetStrategiesAsync(IReadOnlyList<Strategy> strategies)
    {
        StrategyFilters.Visibility = Visibility.Visible;
        Entries.ItemsPanel = (ItemsPanelTemplate)FindResource("PortraitGrid");
        Entries.ItemTemplate = (DataTemplate)FindResource("PortraitTile");
        ScrollViewer.SetCanContentScroll(Entries, false);
        SetEntries("搜索策略、发起人或效果", strategies.Select(s => new Entry(s.Name,
            s.Initiator, s.Description, s.IconPath, "解锁条件", s.UnlockRequirement)
        {
            InitialHp = s.InitialHp,
            Category = s.BandType switch { "GENERAL" => "泛用型策略", "BONDRELEVANT" => "盟约型策略", _ => "未分类策略" }
        }));
        Entries.GroupStyle.Add((GroupStyle)FindResource("StrategyGroups"));
        Entries.Items.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Entry.Category)));
        var portraits = await Task.Run(() => _rows.Select(row => LocalPathToImageConverter.LoadCached(row.Icon)).ToArray()).ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
        {
            for (int i = 0; i < portraits.Length; i++) _rows[i].SetPortrait(portraits[i]);
        });
    }

    private void OnPortraitEnter(object sender, MouseEventArgs e)
    {
        if (!_pinned) ShowDetails((Button)sender);
    }

    private void OnPortraitLeave(object sender, MouseEventArgs e)
    {
        if (!_pinned && ReferenceEquals(((Button)sender).DataContext, Entries.Tag)) CloseDetails();
    }

    private void OnPortraitClick(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned || !ReferenceEquals(((Button)sender).DataContext, Entries.Tag);
        ShowDetails((Button)sender);
    }

    private void ShowDetails(Button portrait)
    {
        if (_popupOwner is null)
        {
            _popupOwner = Window.GetWindow(this);
            DetailsPopup.PlacementTarget = _popupOwner;
            _popupOwner.LocationChanged += OnOwnerMoved;
            _popupOwner.StateChanged += OnOwnerStateChanged;
        }
        if (!ReferenceEquals(Entries.Tag, portrait.DataContext))
        {
            DetailsScroll.ScrollToTop();
            if (portrait.DataContext is Entry { Bounty: { } bounty })
            {
                BountyVariants.Tag = bounty;
                BountyVariants.ItemsSource = _bountiesByName![bounty.Title]
                    .OrderByDescending(b => b.Id == bounty.Id)
                    .ThenByDescending(b => b.Condition == "击杀").ToArray();
            }
            if (portrait.DataContext is Entry { Boss: { } boss } entry)
            {
                if (entry.Health is null)
                {
                    entry.Health = new BossHealthViewModel();
                    entry.Health.SetHealthLevels(boss.Hp);
                }
                entry.Health.RegulationLevel = _runEnemy!.RegulationLevel;
                entry.Health.OtherBossHpExpanded = !entry.Health.HasCurrentBossHp;
                BossHealthHost.Content = entry.Health;
            }
        }
        Entries.Tag = DetailsCard.DataContext = portrait.DataContext;
        PinnedLabel.Text = _pinned ? "已固定" : "单击固定";
        PinnedLabel.Visibility = _pinned || _runEnemy is not null || _bountiesByName is not null ? Visibility.Visible : Visibility.Collapsed;
        var transform = portrait.TransformToAncestor(_popupOwner);

        DetailsPopup.PlacementRectangle = new Rect(0, transform.Transform(new Point()).Y, _popupOwner.ActualWidth, 0);
        DetailsPopup.IsOpen = true;
    }

    private void OnOwnerMoved(object? sender, EventArgs e)
    {
        // WPF does not reposition an open Popup when its owner moves.
        DetailsPopup.HorizontalOffset += 1;
        DetailsPopup.HorizontalOffset -= 1;
    }

    private void OnOwnerStateChanged(object? sender, EventArgs e)
    {
        if (_popupOwner?.WindowState == WindowState.Minimized) CloseDetails();
    }

    internal void CloseDetails()
    {
        DetailsPopup.IsOpen = false;
        _pinned = false;
        DetailsCard.DataContext = Entries.Tag = null;
        BossHealthHost.Content = null;
        PinnedLabel.Visibility = Visibility.Collapsed;
        if (_popupOwner is null) return;
        _popupOwner.LocationChanged -= OnOwnerMoved;
        _popupOwner.StateChanged -= OnOwnerStateChanged;
        _popupOwner = null;
        DetailsPopup.PlacementTarget = null;
    }

    internal void SetBounties(IReadOnlyList<CodexBounty> bounties)
    {
        _bountiesByName = bounties.ToLookup(b => b.Title);
        Entries.ItemTemplate = (DataTemplate)FindResource("BountyTile");
        BountyFilters.Visibility = BountyDetails.Visibility = Visibility.Visible;
        SetEnemyPopupLayout();
        SetEntries("搜索目标、悬赏或奖励", bounties.Select(b =>
            new Entry(b.Title, b.Subtitle, b.Text, b.Icon, "敌人机制", b.Mechanism) { Bounty = b }));
    }

    private void SetEnemyPopupLayout()
    {
        DetailsScroll.MaxHeight = 360;
        DetailsCard.Width = 280;
        StrategyMetadata.Visibility = DetailDescription.Visibility = Visibility.Collapsed;
        BossPortrait.Visibility = Visibility.Visible;
        PinnedLabel.VerticalAlignment = VerticalAlignment.Top;
    }

    internal void SetBosses(IReadOnlyList<BossInfo> bosses, EnemyViewModel runEnemy)
    {
        _runEnemy = runEnemy;
        PropertyChangedEventManager.AddHandler(runEnemy, OnRunModeChanged, nameof(EnemyViewModel.CurrentMode));
        SetEnemyPopupLayout();
        Entries.ItemTemplate = (DataTemplate)FindResource("BossTile");
        BossMetadata.Visibility = Visibility.Visible;
        SetEntries("搜索首领、能力或难度", bosses.Select(b =>
        {
            string abilities = string.Join("\n", b.AbilityList.Select(a => "· " + Regex.Replace(a, @"<[@$][^>]*>|</>", "")));
            string hp = string.Join("\n", b.Hp.Select(h => $"{h.Name}　{h.Hp:N0}" +
                (h.HiddenHp is long hidden ? $"　隐秘 {hidden:N0}" : "")));
            return new Entry(b.Name, string.Join(" · ", new[] { b.EnemyIndex, b.AttackType == "无" ? "攻击方式：无" : b.AttackType,
                EnemyViewModel.FormatDamageTypes(b.DamageTypes) }.Where(s => !string.IsNullOrEmpty(s))),
                b.Description ?? "", b.IconPath, "能力", abilities) { Boss = b, HealthSearchText = hp };
        }));
    }

    private void OnRunModeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (BossHealthHost.Content is BossHealthViewModel health) health.RegulationLevel = _runEnemy!.RegulationLevel;
    }

    private void SetEntries(string hint, IEnumerable<Entry> rows)
    {
        SearchHint.Text = hint;
        _rows = rows.ToArray();
        Entries.ItemsSource = _rows;
    }

    private string _usageFilter = "";
    private string _limitFilter = "";

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        var clicked = (ToggleButton)sender;
        var group = (UniformGrid)clicked.Parent;
        foreach (ToggleButton option in group.Children)
            if (option != clicked) option.IsChecked = false;
        string? filter = clicked.IsChecked == true ? (string)clicked.Tag : null;
        if (group == UsageOptions) _usageFilter = filter ?? "";
        else if (group == LimitOptions) _limitFilter = filter ?? "";
        else if (group == AmountOptions) _amountFilter = filter is null ? null : int.Parse(filter);
        else if (group == RoundsOptions) _roundsFilter = filter is null ? null : int.Parse(filter);
        else _conditionFilter = filter;
        ApplyFilters();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        string query = SearchBox.Text.Trim();
        Entries.ItemsSource = _rows.Where(row =>
            (_amountFilter == null || row.Bounty?.Amount == _amountFilter) &&
            (_roundsFilter == null || row.Bounty?.Rounds == _roundsFilter) &&
            (_conditionFilter == null || row.Bounty?.Condition == _conditionFilter) &&
            row.Text.Contains(_usageFilter) && row.Text.Contains(_limitFilter) &&
            (row.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Text.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Details.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.HealthSearchText.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Bounty?.RewardLabel.Contains(query, StringComparison.OrdinalIgnoreCase) == true
            || row.Bounty?.Tag.Contains(query, StringComparison.OrdinalIgnoreCase) == true)).ToArray();
    }
}
