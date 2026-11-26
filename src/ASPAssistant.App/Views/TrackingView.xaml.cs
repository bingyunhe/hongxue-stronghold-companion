// Modified for ASP Assistant Arknights on 2026-11-13; see NOTICE.
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ASPAssistant.App.Controls;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;

namespace ASPAssistant.App.Views;

public partial class TrackingView : UserControl
{
    public event Action<string>? RemoveTrackingRequested;
    public event Action<string>? ToggleAcquiredRequested;
    public event Action? ClearAllTrackingRequested;
    public static readonly DependencyProperty OperatorDatabaseProperty = DependencyProperty.Register(
        nameof(OperatorDatabase), typeof(IReadOnlyDictionary<string, Operator>), typeof(TrackingView),
        new PropertyMetadata(new Dictionary<string, Operator>()));

    public IReadOnlyDictionary<string, Operator> OperatorDatabase
        => (IReadOnlyDictionary<string, Operator>)GetValue(OperatorDatabaseProperty);

    public static readonly DependencyProperty EquipmentDatabaseProperty = DependencyProperty.Register(
        nameof(EquipmentDatabase), typeof(IReadOnlyDictionary<string, Equipment>), typeof(TrackingView),
        new PropertyMetadata(new Dictionary<string, Equipment>()));

    public IReadOnlyDictionary<string, Equipment> EquipmentDatabase
        => (IReadOnlyDictionary<string, Equipment>)GetValue(EquipmentDatabaseProperty);
    private TrackingViewModel? _vm;
    private INotifyCollectionChanged? _operators;
    private INotifyCollectionChanged? _equipment;
    private CovenantCalculator? _calculator;
    private bool _updatingUpgradeSource;
    private bool _updatingStageControls;
    public static readonly DependencyProperty EquipmentRulesProperty = DependencyProperty.Register(
        nameof(EquipmentRules), typeof(LineupEquipmentService), typeof(TrackingView));
    public LineupEquipmentService EquipmentRules => (LineupEquipmentService)GetValue(EquipmentRulesProperty);

    public TrackingView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as TrackingViewModel);
    }

    public void SetOperators(IReadOnlyList<Operator> operators)
    {
        SetValue(OperatorDatabaseProperty, operators.GroupBy(o => o.Name).ToDictionary(g => g.Key, g => g.First()));
        RefreshSummary();
    }

    public void SetCovenantCalculator(CovenantCalculator calculator)
    {
        _calculator = calculator;
        SetValue(EquipmentRulesProperty, calculator.EquipmentRules);
        RefreshSummary();
    }

    private void Attach(TrackingViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = vm;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        HookCollections();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm?.StageTargetsUpdating == true) return;
        if (e.PropertyName is nameof(TrackingViewModel.TrackedOperators) or nameof(TrackingViewModel.TrackedEquipment))
            HookCollections();
        else if (e.PropertyName is nameof(TrackingViewModel.TerminalLevel) or nameof(TrackingViewModel.TerminalLevelText)
                 or nameof(TrackingViewModel.OperatorProgress) or nameof(TrackingViewModel.Economy) or nameof(TrackingViewModel.SelectedStageChoice) or nameof(TrackingViewModel.ShowAllStages) or nameof(TrackingViewModel.DisplayEquipment))
            RefreshSummary();
        else if (e.PropertyName is nameof(TrackingViewModel.UpgradeReminder) or nameof(TrackingViewModel.UpgradeReminderSources)
                 or nameof(TrackingViewModel.UpgradeReminderLineupId))
            RefreshUpgradeReminder();
    }

    private void HookCollections()
    {
        if (_operators is not null) _operators.CollectionChanged -= OnCollectionChanged;
        if (_equipment is not null) _equipment.CollectionChanged -= OnCollectionChanged;
        _operators = _vm?.TrackedOperators;
        _equipment = _vm?.TrackedEquipment;
        if (_operators is not null) _operators.CollectionChanged += OnCollectionChanged;
        if (_equipment is not null) _equipment.CollectionChanged += OnCollectionChanged;
        RefreshSummary();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm?.StageTargetsUpdating == true) return;
        RefreshSummary();
    }

    /// <summary>Updates the empty state and the covenant tally of tracked operators.</summary>
    private void RefreshSummary()
    {
        var db = OperatorDatabase;
        bool AvailableThisRun(TrackingEntry entry) => db.GetValueOrDefault(entry.Name) is not { IsCustom: true, CarriedThisRun: false };
        var currentOperators = _vm?.TrackedOperators.Where(e => e.IsStageActive && AvailableThisRun(e)).ToList() ?? [];
        var operators = _vm?.DisplayOperators.Where(AvailableThisRun).ToList() ?? [];
        bool overview = _vm?.ShowAllStages == true;
        bool stages = _vm?.StageChoices.Count > 0;
        RefreshStageControls();
        var equipment = _vm?.DisplayEquipment ?? [];
        var all = operators.Concat(equipment).Where(e => !e.IsExpired).ToList();
        var filter = CompletionFilterList.SelectedIndex;
        bool Match(TrackingEntry entry) => filter == 2 || entry.IsAcquired == (filter == 1);
        var equipmentShown = equipment.Where(Match).ToList();
        EquipmentCells.ItemsSource = equipmentShown;
        EquipmentCount.Text = $"{equipmentShown.Count} 件";
        ProgressSummary.Text = $"待完成 {all.Count(e => !e.IsAcquired)} · 已完成 {all.Count(e => e.IsAcquired)}";
        ClearAllButton.Visibility = _vm?.TrackedOperators.Count > 0 || _vm?.TrackedEquipment.Any(e => e.IsManual || e.IsStageActive) == true
            ? Visibility.Visible : Visibility.Hidden;

        var lineup = new Lineup
        {
            Slots = currentOperators.Where(e => stages || e.Role == TrackingRole.Final).Select(e => new LineupSlot
            {
                OperatorName = e.Name, Equipments = e.Equipments, IsElite = e.IsElite
            }).ToList()
        };
        var tally = (_calculator?.Compute(lineup) ?? []).Where(s => s.Count > 0)
            .OrderByDescending(s => s.BondType == "SEASON").ThenByDescending(s => s.Count)
            .Select(s => new TrackedCovenantChip(s.Name, s.Count, s.BondType == "SEASON")).ToList();
        TrackedCovenantChips.ItemsSource = tally;
        TrackedCovenantChips.Visibility = tally.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var rules = EquipmentRules;
        var cells = operators.Where(e => !e.IsExpired && Match(e)).Select(e =>
        {
            var cell = TrackedCell.From(e, db.GetValueOrDefault(e.Name), overview ? null : rules);
            if (overview)
            {
                var labels = e.Origins.Count > 0 ? e.Origins.Select(o => o.Stages.Length > 0 ? o.Stages
                    : o.LineupId.Length > 0 || o.IsRoute ? "成型阵容" : "手动").Distinct() : [e.Stages.Length > 0 ? e.Stages : "手动"];
                cell = cell with { Sub = "阶段：" + string.Join("、", labels), Transformations = [], LegacyCovenant = "" };
            }
            return cell with { Progress = _vm!.OperatorProgress(e), Sub = $"目标：{(e.IsElite ? "精锐" : "获取")} · {cell.Sub}" };
        }).ToList();
        Fill(FinalSection, FinalCells, FinalCount, cells.Where(c => stages || overview || c.Kind == TrackedCellKind.Final).ToList());
        if (stages) FinalSection.Visibility = Visibility.Visible;
        TrackedCovenantChips.ToolTip = stages ? "当前阶段干员带来的盟约人数，包含已完成干员与计划装备" : "目标成型阵容干员带来的盟约人数，包含已完成干员与计划转职，不计过渡干员";
        TrackedCovenantTitle.Text = stages ? "目标盟约 · 当前阶段干员合计" : "目标盟约 · 成型阵容干员合计";
        Fill(TransitionSection, TransitionCells, TransitionCount, cells.Where(c => !stages && !overview && c.Kind == TrackedCellKind.Transition).ToList());
        var past = operators.Where(e => e.IsExpired).Select(e => TrackedCell.From(e, db.GetValueOrDefault(e.Name), rules) with { Progress = "已过期" }).ToList();
        Fill(PastSection, PastCells, PastCount, past);
        var empty = cells.Count + equipmentShown.Count == 0 && (filter != 2 || past.Count == 0);
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Title = operators.Count + equipment.Count == 0 ? "还没有追踪目标" : filter == 0 ? "当前目标已完成" : "没有符合筛选的目标";
        if (stages && !overview && operators.Count == 0) EmptyState.Title = "当前阶段没有上阵干员";
        EmptyState.Hint = operators.Count + equipment.Count == 0 ? "在干员、装备页点 ☆，或在阵容、规划页点“追踪”。"
            : "可切换筛选查看其他目标，或到干员状态更新获取与精锐。";
        if (stages && !overview && operators.Count == 0) EmptyState.Hint = "切换其他阶段，或在阵容编辑器中设置本阶段的上阵干员。";
        EquipmentSection.Visibility = equipmentShown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Terminal level card
        var level = _vm?.TerminalLevel ?? 0;
        LevelTitle.Text = level > 0 ? $"终端等级 {level}" : "终端等级未识别";
        var source = _vm is null || level == 0 ? "打开商店后按升级卡自动识别"
            : _vm.ManualTerminalLevel is not null ? "手动指定 · 自动识别追上后恢复自动"
            : _vm.IsTerminalLevelRestored ? "上次记录 · 打开商店后确认"
            : "自动识别";
        var pastStages = currentOperators.Where(e => e.IsExpired).SelectMany(e => e.Stages.Split(['、', '→', '·', ' '],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct().ToList();
        LevelDetail.Text = pastStages.Count > 0 && level > 0 ? $"{source} · {string.Join("、", pastStages)}已过" : source;
        LevelDetail.ToolTip = _vm?.TerminalLevelText;
        PastHint.Text = level > 0 ? $"终端 {level} 级起不再标出" : "不再标出";

        // Shop HUD
        var economy = _vm?.Economy;
        EconomyRow.Visibility = economy is { HasAny: true } || economy?.Mode is not null ? Visibility.Visible : Visibility.Collapsed;
        EconomyRound.Text = economy?.Round?.ToString() ?? "–";
        EconomyMoney.Text = economy?.Money?.ToString() ?? "–";
        EconomyCost.Text = economy is { UpgradeMaxed: true } ? "满级" : economy?.UpgradeCost?.ToString() ?? "–";
        EconomyMode.Text = economy?.Mode ?? "";
        RefreshUpgradeReminder();
    }

    private void RefreshUpgradeReminder()
    {
        var display = _vm?.UpgradeReminder;
        UpgradeReminderArea.Visibility = display is null ? Visibility.Collapsed : Visibility.Visible;
        if (display is null) return;
        _updatingUpgradeSource = true;
        UpgradeReminderSourceRow.Visibility = _vm!.UpgradeReminderSources.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(UpgradeReminderSourceBox.ItemsSource, _vm.UpgradeReminderSources))
            UpgradeReminderSourceBox.ItemsSource = _vm.UpgradeReminderSources;
        UpgradeReminderSourceBox.SelectedValue = _vm.UpgradeReminderLineupId;
        _updatingUpgradeSource = false;
        var status = display.Status;
        bool warning = status is UpgradeReminderStatus.LastRound or UpgradeReminderStatus.Overdue;
        bool neutral = status is UpgradeReminderStatus.Waiting or UpgradeReminderStatus.Upcoming or UpgradeReminderStatus.Ignored;
        bool complete = status == UpgradeReminderStatus.Completed;
        UpgradeReminderCard.SetResourceReference(Border.BackgroundProperty, warning ? "WarningTintBrush"
            : complete ? "SuccessTintBrush" : neutral ? "SectionBackgroundBrush" : "AccentSoftBrush");
        UpgradeReminderCard.SetResourceReference(Border.BorderBrushProperty, warning ? "WarningMutedBrush"
            : complete ? "SuccessBorderBrush" : neutral ? "LineBrush" : "AccentBorderBrush");
        string foreground = warning ? "WarningTextBrush" : complete ? "SuccessTextBrush" : neutral ? "TextSecondaryBrush" : "AccentBrush";
        UpgradeReminderTitle.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        UpgradeReminderRange.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        UpgradeReminderTitle.Text = (complete ? "✓ " : neutral ? "" : "↑ ") + display.Title;
        UpgradeReminderRange.Text = display.RangeText;
        UpgradeReminderDetail.Text = display.Detail;
        UpgradeReminderCard.ToolTip = _vm.UpgradeReminderSources.Single(l => l.Id == _vm.UpgradeReminderLineupId).Name
            + "\n" + display.Title + " · " + display.Reminder?.RoundText + "\n" + display.Detail
            + (display.Reminder is { Note.Length: > 0 } r ? "\n" + r.Note : "");
        IgnoreUpgradeReminderButton.Visibility = display.Reminder is not null || status == UpgradeReminderStatus.Ignored
            ? Visibility.Visible : Visibility.Collapsed;
        IgnoreUpgradeReminderButton.Content = status == UpgradeReminderStatus.Ignored ? "恢复提醒" : "本局忽略";
    }

    private void OnUpgradeReminderSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingUpgradeSource && _vm is not null && UpgradeReminderSourceBox.SelectedValue is string id)
            _vm.UpgradeReminderLineupId = id;
    }

    private void OnIgnoreUpgradeReminder(object sender, RoutedEventArgs e)
    {
        if (_vm?.UpgradeReminder?.Status == UpgradeReminderStatus.Ignored) _vm.RestoreUpgradeReminders();
        else _vm?.IgnoreUpgradeReminder();
    }

    private static void Fill(FrameworkElement section, ItemsControl list, TextBlock count, IReadOnlyList<TrackedCell> cells)
    {
        list.ItemsSource = cells;
        count.Text = $"{cells.Count} 名";
        section.Visibility = cells.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCellRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TrackedCell cell) RemoveTrackingRequested?.Invoke(cell.Name);
    }

    /// <summary>Removes the planned transformation(s): 变形同构体 + job-change gear, or a v1.5 covenant without gear.</summary>
    private void OnCellClearTransform(object sender, RoutedEventArgs e)
    {
        if (_vm is null || (sender as FrameworkElement)?.DataContext is not TrackedCell cell) return;
        foreach (var t in cell.Transformations) _vm.ClearPlannedTransformation(cell.Name, t.EquipmentName, EquipmentRules);
        if (cell.LegacyCovenant.Length > 0) _vm.ClearPlannedTransformation(cell.Name, "", EquipmentRules);
    }

    /// <summary>购买清单 / 干员状态 / 地图模拟。</summary>
    private void OnTrackingPageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeployPage is null || ShoppingPage is null) return;
        bool deploy = TrackingPageList.SelectedIndex > 0;
        DeployPage.Visibility = deploy ? Visibility.Visible : Visibility.Collapsed;
        ShoppingPage.Visibility = deploy ? Visibility.Collapsed : Visibility.Visible;
        if (deploy) DeployPage.ShowPage(showPool: TrackingPageList.SelectedIndex == 1);
        if (IsLoaded) ASPAssistant.App.Windows.PageTransition.SlideIn(deploy ? DeployPage : ShoppingPage, fromX: deploy ? 14 : -14);
    }

    public void SetEquipment(IReadOnlyList<Equipment> equipment)
        => SetValue(EquipmentDatabaseProperty, equipment.ToDictionary(e => e.Name));

    private void OnTrackedEquipmentToggled(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is EquipmentCell { DataContext: Equipment equipment })
            RemoveTrackingRequested?.Invoke(equipment.Name);
    }

    private void OnTrackedEquipmentAcquired(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is EquipmentCell { DataContext: Equipment equipment })
            ToggleAcquiredRequested?.Invoke(equipment.Name);
    }

    private void OnTogglePast(object sender, RoutedEventArgs e)
    {
        var open = PastBody.Visibility != Visibility.Visible;
        PastBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        PastToggle.Content = (open ? "▾ " : "▸ ") + "已过期的过渡干员";
    }

    private void RefreshStageControls()
    {
        _updatingStageControls = true;
        bool stages = _vm?.StageChoices.Count > 0;
        StageSelector.Visibility = stages ? Visibility.Visible : Visibility.Collapsed;
        FinalTitle.Text = _vm?.ShowAllStages == true ? "全程干员" : "成型阵容干员";
        FinalTitle.Visibility = stages ? Visibility.Collapsed : Visibility.Visible;
        AutoStagePanel.Visibility = stages ? Visibility.Visible : Visibility.Collapsed;
        StageSelector.ItemsSource = _vm?.StageChoices;
        StageSelector.SelectedItem = _vm?.SelectedStageChoice;
        AutoStageToggle.IsChecked = _vm?.AutomaticStageSwitch == true;
        StageSwitchHint.Text = _vm?.StageSwitchHint ?? "";
        StageSwitchHint.Visibility = StageSwitchHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _updatingStageControls = false;
    }

    private void OnStageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingStageControls && StageSelector.SelectedItem is TrackingStageChoice choice) _vm!.SelectStage(choice);
    }

    private void OnAutomaticStageChanged(object sender, RoutedEventArgs e)
    {
        if (!_updatingStageControls) _vm!.SetAutomaticStageSwitch(AutoStageToggle.IsChecked == true);
    }

    private void OnToggleGroup(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var body = (FrameworkElement)button.Tag;
        var open = body.Visibility != Visibility.Visible;
        body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (button.Content is System.Windows.Shapes.Path icon)
        {
            icon.Data = System.Windows.Media.Geometry.Parse(open ? "M3,6 L8,11 L13,6" : "M6,3 L11,8 L6,13");
            icon.Width = open ? 11 : 7; icon.Height = open ? 7 : 11;
            button.ToolTip = open ? "收起列表" : "展开列表";
        }
        else button.Content = open ? "收起" : "展开";
    }

    private void OnCompletionFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is not null) RefreshSummary();
    }

    private void OnCellOpenStatus(object sender, RoutedEventArgs e)
    {
        var data = MenuContext(sender);
        if (data is not TrackedCell cell) return;
        TrackingPageList.SelectedIndex = 1;
        DeployPage.FocusOperator(cell.Name);
    }

    /// <summary>Acquisition for equipment names missing from the data.</summary>
    private void OnToggleAcquired(object sender, RoutedEventArgs e)
    {
        if (MenuContext(sender) is TrackingEntry entry) ToggleAcquiredRequested?.Invoke(entry.Name);
    }

    /// <summary>A menu item's data, falling back to the element the menu belongs to.</summary>
    private static object? MenuContext(object sender) => sender is FrameworkElement { DataContext: { } data } ? data
        : (sender as FrameworkElement)?.Parent is ContextMenu { PlacementTarget: FrameworkElement target } ? target.DataContext : null;

    private void OnRemoveTracking(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TrackingEntry entry) RemoveTrackingRequested?.Invoke(entry.Name);
    }

    private void OnClearAllTracking(object sender, RoutedEventArgs e)
    {
        ClearAllTrackingRequested?.Invoke();
    }
}

public sealed record TrackedCovenantChip(string Name, int Count, bool IsCore);

public enum TrackedCellKind { Final, Transition, Past }

/// <summary>One tracked operator on the tracking page: grouped by role, ringed in the shop marker color.</summary>
public sealed record TrackedCell(string Name, TrackedCellKind Kind, string Sub, Operator? Operator,
    IReadOnlyList<EquipmentTransformation> Transformations, string LegacyCovenant)
{
    /// <summary>Marked 已获取: kept on the list, no longer starred in the shop.</summary>
    public bool IsAcquired { get; init; }
    public string Progress { get; init; } = "";
    public string Initial => Name.Length > 0 ? Name[..1] : "";
    public string IconPath => Operator?.IconPath ?? "";
    public bool HasDetails => Operator is not null;
    public bool HasTransform => Transformations.Count > 0 || LegacyCovenant.Length > 0;
    /// <summary>Transformation target(s), e.g. “不屈” or “不屈、精准” (暴躁铁皮 can hold several).</summary>
    public string TransformedCovenant => string.Join("、",
        Transformations.Select(t => t.Covenant).Append(LegacyCovenant).Where(c => c.Length > 0).Distinct());

    public static TrackedCell From(TrackingEntry entry, Operator? op, LineupEquipmentService? rules)
    {
        var kind = entry.IsExpired ? TrackedCellKind.Past
            : entry.IsTransition ? TrackedCellKind.Transition : TrackedCellKind.Final;
        IReadOnlyList<EquipmentTransformation> transformations = rules?.Transformations(new LineupSlot
        {
            OperatorName = entry.Name, Equipments = entry.Equipments, IsElite = entry.IsElite
        }) ?? [];
        var cell = new TrackedCell(entry.Name, kind, "", op, transformations, entry.TransformedCovenant ?? "");
        string sub;
        if (kind != TrackedCellKind.Final && !string.IsNullOrEmpty(entry.Stages)) sub = entry.Stages;
        else
        {
            var covenants = op is null ? new List<string>() : [.. op.CoreCovenants, .. op.AdditionalCovenants];
            var text = string.Join(" · ", covenants.Distinct());
            sub = cell.HasTransform ? $"转职 {cell.TransformedCovenant}" + (text.Length > 0 ? $" · {text}" : "")
                : text.Length > 0 ? text : op is null ? "无数据" : "";
        }
        if (entry.IsAcquired) sub = sub.Length > 0 ? $"已完成 · {sub}" : "已完成";
        return cell with { Sub = sub, IsAcquired = entry.IsAcquired };
    }
}
