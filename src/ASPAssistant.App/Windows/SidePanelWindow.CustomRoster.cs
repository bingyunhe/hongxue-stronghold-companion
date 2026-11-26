using System.IO;
using System.Windows;
using System.Windows.Controls;
using ASPAssistant.App.Controls;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;

namespace ASPAssistant.App.Windows;

public partial class SidePanelWindow
{
    public Func<Task<CustomRosterReading?>>? ReadCustomRoster { get; set; }
    private DeployPoolState _customRun = new();
    private bool _readingCustom;
    private bool _automaticCarryAdvice;

    private void ShowCarryAdvice()
    {
        CarryAdviceBanner.Visibility = Visibility.Visible;
        RefreshVisibleCarryAdvice();
    }

    private void RefreshVisibleCarryAdvice()
    {
        if (CarryAdviceBanner.Visibility != Visibility.Visible) return;
        CarryStrategyButton.Content = "策略 · " + PlannerVm.StrategyChoices.Single(choice => choice.Strategy == PlannerVm.Strategy).Name;
        var tracked = TrackingVm.CurrentMapLineup;
        var lineup = tracked?.GuideSource ?? LineupVm.Lineups.FirstOrDefault(l => l.Id == tracked?.Id);
        if (lineup == null)
        {
            CarryAdviceText.Text = "请先追踪一套阵容";
            return;
        }
        var suggestions = CustomCarryAdvisor.Recommend(lineup, _allOperators, LineupVm.Calculator, BanVm, PlannerVm.Strategy);
        var rows = suggestions.GroupBy(s => s.Tier).OrderBy(g => g.Key).Select(g =>
            (g.Key == 5 ? "V" : "VI") + " 阶　" + string.Join("、", g.Select(s => $"{s.Name}（{s.Reason}）")));
        CarryAdviceText.Text = $"{lineup.Name}\n" + (suggestions.Count == 0 ? "当前自选编队没有匹配这套阵容的人选" : string.Join("\n", rows));
    }

    private void OnCarryStrategy(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = CarryStrategyButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var choice in PlannerVm.StrategyChoices)
            menu.Items.Add(new MenuItem { Header = choice.Name, ToolTip = choice.Hint, IsCheckable = true,
                IsChecked = choice.Strategy == PlannerVm.Strategy, Command = PlannerVm.SelectStrategyCommand, CommandParameter = choice });
        menu.IsOpen = true;
    }

    private void OnHideCarryAdvice(object sender, RoutedEventArgs e) => HideCarryAdvice();

    private void HideCarryAdvice()
    {
        _automaticCarryAdvice = false;
        CarryAdviceBanner.Visibility = Visibility.Collapsed;
    }

    public void InitCustomRun(DeployPoolState state)
    {
        _customRun = state;
        RefreshCustomRun();
    }

    public void StartCustomRun()
    {
        HideCarryAdvice();
        _customRun.CustomCarryEdited = false;
        _customRun.CarriedCustom = [];
        _customRun.CustomOpeningResult = null;
        RefreshCustomRun();
    }

    public void ObserveCustomRunStarted()
    {
        if (_automaticCarryAdvice) HideCarryAdvice();
        if (_customRun.CarriedCustom != null) return;
        StartCustomRun();
        _ = SaveCustomRunAsync();
    }

    private void ClearCustomRun()
    {
        HideCarryAdvice();
        _customRun.CustomCarryEdited = false;
        _customRun.CarriedCustom = null;
        _customRun.CustomOpeningResult = null;
        RefreshCustomRun();
        _ = SaveCustomRunAsync();
    }

    public void ApplyCustomOpening(CustomRosterReading reading)
    {
        var key = reading.Page + ":" + string.Join('|', reading.Entries.Select(e => $"{e.Tier}/{e.Name}/{e.SkillId}"));
        if (reading.ConfirmedEmpty) key += "empty-confirmed";
        if (reading.Unread.Count > 0) key += ":unread";
        if (_customRun.CustomOpeningResult == key) return;
        if (reading.Page == CustomRosterPage.Prepared) HideCarryAdvice();
        else if (reading.Page == CustomRosterPage.Choosing)
        {
            _automaticCarryAdvice = true;
            CarryAdviceBanner.Visibility = Visibility.Visible;
        }
        if (_customRun.CustomCarryEdited && (reading.Page == CustomRosterPage.Prepared
            || _customRun.CustomOpeningResult?.StartsWith("Prepared:", StringComparison.Ordinal) != true))
        {
            RefreshVisibleCarryAdvice();
            return;
        }
        _customRun.CustomCarryEdited = false;
        _customRun.CustomOpeningResult = key;
        _customRun.CarriedCustom = reading.Entries.ToList();
        RefreshCustomRun();
        _ = SaveCustomRunAsync();
    }

    private void RefreshCustomRun()
    {
        OperatorBrowseVm.CarryStatus = _customRun.CarriedCustom == null ? ""
            : _customRun.CustomCarryEdited ? " · 手动确认"
            : _customRun.CustomOpeningResult == "Prepared:empty-confirmed" ? " · 已确认未携带"
            : _customRun.CarriedCustom.Count == 0 ? " · 未识别，按 0 人"
            : _customRun.CustomOpeningResult?.EndsWith(":unread", StringComparison.Ordinal) == true ? " · 部分格位未识别" : " · 已识别";
        foreach (var op in _allOperators.Where(o => o.IsCustom))
        {
            var entry = _customRun.CarriedCustom?.Find(e => e.Name == op.Name);
            op.SetRunEntry(_customRun.CarriedCustom == null ? null : entry != null, entry);
        }
        RefreshCustomViews();
    }

    private void RefreshCustomViews()
    {
        OperatorBrowseVm.LoadOperators(_allOperators.ToList());
        PlannerVm.NotifyOperatorSelectionChanged();
        TrackingView.SetOperators(_allOperators);
        TrackingView.DeployPage.RefreshCustomRoster();
        SklandView.RefreshRunFit();
        RefreshVisibleCarryAdvice();
    }

    private async Task SaveCustomRunAsync()
    {
        try { await _settingsManager.SaveDeployPoolAsync(_customRun); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ShowToast($"本局自选保存失败：{ex.Message}"); }
    }

    private async void ToggleCustomCarry(Operator op, FrameworkElement anchor)
    {
        var entries = _customRun.CarriedCustom?.ToList() ?? [];
        if (entries.RemoveAll(e => e.Name == op.Name) == 0)
        {
            if (BanVm.IsBanned(op.Name) || OperatorView.IsCovenantBannedCheck?.Invoke(op.Name) == true)
            {
                ShowToast("这名干员本局已禁用");
                return;
            }
            if (op.RosterEntry?.Tier == null)
            {
                ShowToast("请先在管理自选中指定这名干员的等阶");
                await EditCustomRosterAsync();
                if (op.RosterEntry?.Tier == null) return;
                entries = _customRun.CarriedCustom?.ToList() ?? [];
                if (entries.Any(e => e.Name == op.Name)) return;
            }
            var sameTier = entries.Where(e => e.Tier == op.RosterEntry.Tier).ToList();
            if (sameTier.Count >= 2)
            {
                var original = _customRun.CarriedCustom;
                var incoming = op.RosterEntry;
                var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
                menu.Items.Add(new MenuItem { Header = $"换入{op.Name}，请选择换下的人", IsEnabled = false });
                foreach (var outgoing in sameTier)
                {
                    var member = _allOperators.FirstOrDefault(o => o.IsCustom && o.Name == outgoing.Name);
                    var label = new StackPanel { Orientation = Orientation.Horizontal };
                    label.Children.Add(new Avatar { Text = outgoing.Name, Source = member?.IconPath, Width = 24, Height = 24 });
                    label.Children.Add(new TextBlock { Text = outgoing.Name, Margin = new Thickness(6,0,0,0), VerticalAlignment = VerticalAlignment.Center });
                    var item = new MenuItem { Header = label, Tag = outgoing.Name };
                    System.Windows.Automation.AutomationProperties.SetName(item, outgoing.Name);
                    item.Click += async (_, _) =>
                    {
                        if (!ReferenceEquals(original, _customRun.CarriedCustom) || incoming != op.RosterEntry
                            || BanVm.IsBanned(op.Name) || OperatorView.IsCovenantBannedCheck?.Invoke(op.Name) == true)
                        { ShowToast("本局状态已变化，请重新选择"); return; }
                        entries[entries.IndexOf(outgoing)] = incoming;
                        await SetManualCarryAsync(entries);
                    };
                    menu.Items.Add(item);
                }
                anchor.ContextMenu = menu;
                menu.Closed += (_, _) => anchor.ClearValue(FrameworkElement.ContextMenuProperty);
                menu.IsOpen = true;
                return;
            }
            entries.Add(op.RosterEntry);
        }
        await SetManualCarryAsync(entries);
    }

    private async Task SetManualCarryAsync(List<CustomRosterEntry> entries)
    {
        _customRun.CustomCarryEdited = true;
        _customRun.CarriedCustom = entries;
        RefreshCustomRun();
        await SaveCustomRunAsync();
    }

    private async Task EditCustomRosterAsync(IReadOnlyList<CustomRosterEntry>? proposed = null)
    {
        var dialog = new CustomOperatorSelectionWindow(_allOperators.Where(o => o.IsCustom), proposed) { Owner = this };
        await MinimizableDialog.ShowAsync(dialog, OperatorView);
        if (dialog.Entries != null) await SaveCustomRosterAsync(dialog.Entries);
    }

    private async Task SaveCustomRosterAsync(IReadOnlyList<CustomRosterEntry> entries)
    {
        try { await _settingsManager.SaveCustomRosterAsync(entries); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ShowToast($"自选干员保存失败：{ex.Message}"); return; }
        var byName = entries.ToDictionary(e => e.Name);
        foreach (var op in _allOperators.Where(o => o.IsCustom)) op.SetRosterEntry(byName.GetValueOrDefault(op.Name));
        RefreshCustomViews();
        ShowToast($"已保存 {entries.Count} 名自选干员");
    }

    private async Task RecognizeCustomRosterAsync()
    {
        if (_readingCustom || ReadCustomRoster == null) return;
        _readingCustom = true;
        try
        {
            var reading = await ReadCustomRoster();
            if (reading == null) { ShowToast("请在游戏中打开「选择自选编队」后重试"); return; }
            if (reading.Unread.Count == 0 && reading.Entries.All(e => e.SkillId != null) && reading.Entries.Count > 0)
            { await SaveCustomRosterAsync(reading.Entries); return; }
            var proposed = reading.Entries.ToList();
            foreach (var op in _allOperators.Where(o => o.RosterEntry != null))
            {
                var old = op.RosterEntry!;
                if (proposed.Any(e => e.Name == old.Name)) continue;
                if (reading.Unread.Any(s => (old.Tier == null || old.Tier == s.Tier) && (old.Slot == null || old.Slot == s.Slot)))
                    proposed.Add(old);
            }
            ShowToast(reading.Unread.Count > 0 ? $"有 {reading.Unread.Count} 个格位需要复核" : "请复核自选编队和携带技能");
            await EditCustomRosterAsync(proposed);
        }
        finally { _readingCustom = false; }
    }
}
