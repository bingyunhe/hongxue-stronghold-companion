using System.Windows;
using System.Windows.Controls;
using ASPAssistant.App.Controls;
using ASPAssistant.App.Windows;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;

namespace ASPAssistant.App.Views;

public partial class DeployView
{
    private LineupPlannerViewModel? _mapPlanner;
    private LineupViewModel? _mapLibrary;
    private CancellationTokenSource? _pivotSearch;
    private LiveRoster? _editRoster;
    private LiveUnit? _editUnit;
    private FieldSlot? _editSlot;
    private string? _editName;
    private FieldObservation? _choicesFrame;
    private (FieldObservation Frame, string[] Names, string Method)? _pickedChoice;
    private string[] DisplayedCorrectionChoices() => CorrectionChoices.Children.OfType<Button>().Select(b => (string)b.Content).ToArray();
    private int? _returnMode;
    private BanScreenResult? _runCovenants;
    private IReadOnlySet<string>? _runExcluded;
    private OperatorPickerWindow? _openRunPicker;

    internal void SetRunCovenants(BanScreenResult? result, IEnumerable<string> excluded)
    {
        if (result is not null && ReferenceEquals(result, _runCovenants)) return;
        _runCovenants = result;
        _runExcluded = result is null ? null : excluded.ToHashSet(StringComparer.Ordinal);
        _liveScanner?.SetReviewExclusions(_runExcluded);
        _openRunPicker?.Close();
        HideCorrection();
        if (_ready) RefreshAll();
    }

    private bool CanChooseOperator(string name) => _operators.TryGetValue(name, out var op)
        && op.IsAvailableForSelection && _runExcluded?.Contains(name) != true;

    private string? PickRunOperator(IEnumerable<Operator> operators, string current, string[]? ranked = null)
    {
        var choices = operators.Where(op => CanChooseOperator(op.Name)).ToList();
        var strategies = choices.Select(o => o.RequiredStrategyId).Where(s => s.Length > 0).ToHashSet();
        var picker = new OperatorPickerWindow(choices, current, strategies,
            reviewCandidates: ranked?.Where(CanChooseOperator).ToArray(), pickerRun: _runCovenants)
            { Owner = Window.GetWindow(this) };
        picker.ClearSlotButton.Visibility = Visibility.Collapsed;
        _openRunPicker = picker;
        try { picker.ShowDialog(); }
        finally { _openRunPicker = null; }
        return picker.Result;
    }

    internal void ConnectMapPlanner(LineupPlannerViewModel planner, LineupViewModel library)
    {
        _mapPlanner = planner;
        _mapLibrary = library;
        MapSwitchButton.IsEnabled = true;
    }

    private void Notice(string message)
    {
        MapActionNotice.Text = message;
        MapActionNotice.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void HideCorrection()
    {
        _editRoster = null;
        _editSlot = null;
        _editUnit = null;
        _editName = null;
        _choicesFrame = null;
        _pickedChoice = null;
        CorrectionPanel.Visibility = Visibility.Collapsed;
    }

    private void FinishCorrection(string message)
    {
        var back = _returnMode;
        _returnMode = null;
        HideCorrection();
        if (back.HasValue)
        {
            _frozenCandidates = null;
            ModeList.SelectedIndex = back.Value;
            message += $" · 已返回{((ListBoxItem)ModeList.SelectedItem).Content}推荐";
        }
        else if (ShowingLive) DrawBoard();
        Notice(message);
    }

    private async void OpenSlot(FieldSlot slot)
    {
        var observed = _roster.Observations.LastOrDefault();
        if (observed == null) { Notice("先在休整期识别阵容，再点格子补录"); return; }
        if (!observed.PersonalBoard) { Notice("请确认当前是自己的棋盘，点「识别阵容」旁的 ▾，选择「识别我的棋盘并同步」"); return; }
        if (observed.Map != DisplayMap?.Id) { Notice("当前画面与地图不一致，请重新识别自己的棋盘"); return; }
        var unit = _roster.Units.FirstOrDefault(u => u.Mark.Slot == slot);
        _editRoster = _roster;
        _editSlot = slot;
        _editUnit = unit;
        _choicesFrame = observed;
        _pickedChoice = null;
        _editName = unit?.Name is { } current && CanChooseOperator(current) ? current : null;
        var displayed = _editRoster;
        DrawBoard();
        var names = unit == null ? _roster.Unlocated.Select(m => m.Name).Concat(_state.Owned
            .Where(n => _state.CountOf(n) > _roster.Units.Count(u => u.Name == n))).Distinct().ToArray()
            : unit.Suggestion == null ? Array.Empty<string>() : new[] { unit.Suggestion.Name };
        ShowCorrection(names);
        if (unit != null && _liveScanner is { } scanner)
        {
            var result = await Task.Run(() =>
            {
                var ranked = scanner.GetLiveReviewCandidates(unit, out var frame);
                return (ranked, frame);
            });
            if (!ReferenceEquals(_editRoster, displayed) || _editSlot != slot || result.frame == null) return;
            _choicesFrame = result.frame;
            ShowCorrection(names.Concat(result.ranked).Distinct().ToArray());
        }
    }

    private void ShowCorrection(IEnumerable<string> names)
    {
        CorrectionPanel.Visibility = Visibility.Visible;
        CorrectionTitle.Text = _editUnit != null ? $"复核 {_editSlot}" : _editSlot != null ? $"补录 {_editSlot}" : "补记持有";
        CorrectionChoices.Children.Clear();
        var candidates = names.Where(CanChooseOperator).Take(4).ToArray();
        _editName ??= candidates.FirstOrDefault();
        foreach (string n in candidates)
        {
            var button = new Button { Content = n, Margin = new Thickness(0, 3, 4, 2) };
            button.SetResourceReference(StyleProperty, "SmallActionButton");
            button.Click += (_, _) =>
            {
                _pickedChoice = (_choicesFrame!, DisplayedCorrectionChoices(), "快捷确认");
                _editName = n;
                UpdateCorrectionActions();
            };
            CorrectionChoices.Children.Add(button);
        }
        UpdateCorrectionActions();
        CorrectionPanel.BringIntoView();
    }

    private void UpdateCorrectionActions()
    {
        foreach (var candidate in CorrectionChoices.Children.OfType<Button>())
            candidate.Tag = Equals(candidate.Content, _editName) ? "accent" : null;
        int count = _editName == null ? 0 : _state.CountOf(_editName);
        int located = _editName == null ? 0 : _roster.Units.Count(u => u.Name == _editName && u.Mark.Slot != _editSlot);
        int pending = _roster.Units.Count(u => u.Name == null && u.Mark.Slot != _editSlot);
        CorrectionHint.Text = _editName == null ? "请选择游戏中实际看到的干员" : $"{_editName} · 已持有 {count} 名"
            + (_editSlot != null ? $"，其他格已定位 {located} 名" : "，仅更新持有数量");
        ConfirmCorrectionButton.Visibility = _editSlot == null ? Visibility.Collapsed : Visibility.Visible;
        ConfirmCorrectionButton.IsEnabled = _editName != null;
        ConfirmCorrectionButton.Content = _editUnit != null ? $"确认{_editName}" + (pending > 0 ? "并看下一格" : "")
            : count > located ? $"将已持有的{_editName}定位到 {_editSlot}" : $"补上{_editName}到 {_editSlot}（持有至少 {located + 1} 名）";
        CountOnlyButton.Visibility = _editUnit != null ? Visibility.Collapsed : Visibility.Visible;
        CountOnlyButton.Content = $"仅补记 1 名（{count} → {count + 1}）";
        CountOnlyButton.IsEnabled = _editName != null && count < 9;
        LeaveCorrectionButton.Content = _returnMode.HasValue ? $"稍后复核，返回{((ListBoxItem)ModeList.Items[_returnMode.Value]).Content}推荐" : "返回地图";
    }

    private void OnRecordHolding(object sender, RoutedEventArgs e)
    {
        HideCorrection();
        _editRoster = _roster;
        if (!ChooseCorrection()) { HideCorrection(); return; }
        ShowCorrection([]);
    }

    private bool ChooseCorrection()
    {
        if (PickRunOperator(_operators.Values, _editName ?? "") is not { Length: > 0 } n || _editRoster == null) return false;
        _editName = n;
        return true;
    }

    private void OnPickCorrection(object sender, RoutedEventArgs e)
    {
        var frame = _choicesFrame;
        var displayed = DisplayedCorrectionChoices();
        if (ChooseCorrection())
        {
            if (frame != null) _pickedChoice = (frame, displayed, "选其他干员");
            UpdateCorrectionActions();
        }
    }

    private void OnCountOnly(object sender, RoutedEventArgs e)
    {
        if (_editName is not { } name || _state.CountOf(name) >= 9 || _editRoster == null) return;
        SetManualCount(name, _state.CountOf(name) + 1);
        _frozenCandidates = null;
        SaveState();
        RefreshOwnership(null);
        FinishCorrection($"已补记{name} · 持有 {_state.CountOf(name)} 名");
    }

    private async void OnConfirmCorrection(object sender, RoutedEventArgs e)
    {
        if (_editName is not { } name || _editSlot is not { } slot || _editRoster == null || _liveScanner is not { } scanner) return;
        var (observation, candidates, method) = _pickedChoice ?? (_choicesFrame!, DisplayedCorrectionChoices(), "快捷确认");
        var entry = (_editUnit == null ? "空格补记·" : "地图复核·") + method;
        if (!(_editUnit is { } unit ? scanner.ConfirmMapUnit(_editRoster, unit, name) : scanner.PlaceMissedUnit(_editRoster, slot, name)))
        {
            HideCorrection();
            ShowLiveRoster(scanner.LiveRoster);
            Notice("棋盘已更新，请重新点选当前格子");
            return;
        }
        ShowLiveRoster(scanner.LiveRoster);
        if (_roster.Units.FirstOrDefault(u => u.Name == null) is { } next) OpenSlot(next.Mark.Slot);
        else FinishCorrection($"已确认 {slot} · {name}");
        await scanner.SavePlayerCorrectionAsync(observation, slot, entry, candidates, name);

    }

    private void OnLeaveCorrection(object sender, RoutedEventArgs e) => FinishCorrection("已返回地图");

    private void CloseMapActions()
    {
        _returnMode = null;
        HideCorrection();
        _pivotSearch?.Cancel();
        _pivotSearch = null;
        PivotPanel.Visibility = Visibility.Collapsed;
        MapContent.Visibility = Visibility.Visible;
    }

    private void OnClosePivot(object sender, RoutedEventArgs e)
    {
        CloseMapActions();
        RefreshAll();
    }

    public event Action? GuideRequested;
    private void OnMapGuide(object sender, RoutedEventArgs e) => GuideRequested?.Invoke();

    private async void OnMapSwitch(object sender, RoutedEventArgs e)
    {
        if (PivotPanel.Visibility == Visibility.Visible) { OnClosePivot(sender, e); return; }
        HideCorrection();
        PivotPanel.Visibility = Visibility.Visible;
        MapContent.Visibility = MapFooter.Visibility = Visibility.Collapsed;
        Scroller.ScrollToTop();
        await LoadPivots();
    }

    private Task<IReadOnlyList<MapPivotChoice>> GetChoices(CancellationToken token) =>
        _mapPlanner!.FindMapPivotsAsync(_mapLibrary!, _state.Owned.Distinct().ToDictionary(n => n, _state.CountOf),
            _state.Elite.ToHashSet(), _state.FieldCount, _tracking!.TerminalLevel, token);

    private async Task LoadPivots()
    {
        _pivotSearch?.Cancel();
        using var search = new CancellationTokenSource();
        _pivotSearch = search;
        PivotChoices.ItemsSource = null;
        PivotReplaceButton.IsEnabled = PivotAppendButton.IsEnabled = false;
        PivotStatus.Text = "正在比较我的阵容和本局规划…";
        try
        {
            var choices = await GetChoices(search.Token);
            if (search.IsCancellationRequested) return;
            var alternatives = choices.Where(c => !_tracking!.IsLineupTracked(c.Lineup.Id)).ToList();
            PivotChoices.ItemsSource = alternatives;
            PivotChoices.SelectedIndex = alternatives.Count > 0 ? 0 : -1;
            PivotStatus.Text = alternatives.Count == 0 ? "当前条件下没有新的可用阵容" : "按当前可用前排、治疗、手牌和缺卡回放排序";
        }
        catch (OperationCanceledException) when (search.IsCancellationRequested) { }
        finally { if (ReferenceEquals(_pivotSearch, search)) _pivotSearch = null; }
    }

    private void OnPivotSelected(object sender, SelectionChangedEventArgs e)
    {
        PivotDetails.Children.Clear();
        var choice = PivotChoices.SelectedItem as MapPivotChoice;
        PivotReplaceButton.IsEnabled = choice != null;
        PivotAppendButton.IsEnabled = choice != null && _tracking?.CurrentMapLineup is not null;
        if (choice == null) return;
        void Line(string value, bool heading = false)
        {
            var text = Text(value, heading ? 12 : 11, heading ? "TextPrimaryBrush" : "TextSecondaryBrush", bold: heading);
            text.TextWrapping = TextWrapping.Wrap;
            text.Margin = new Thickness(0, 3, 0, 2);
            PivotDetails.Children.Add(text);
        }
        Line($"{choice.Source} · {choice.Lineup.Name}", true);
        if (_tracking!.CurrentMapLineup is { } current)
        {
            var targets = _tracking.TrackedOperators.SelectMany(e => e.Origins.Where(o => o.LineupId == current.Id && o.Role == TrackingRole.Final)
                .Select(_ => e.Name)).ToArray();
            int before = targets.GroupBy(n => n).Sum(g => Math.Min(g.Count(), _state.CountOf(g.Key)));
            int oldMissing = targets.Length - before;
            string comparison = oldMissing > choice.Missing.Length ? $"可少追 {oldMissing - choice.Missing.Length} 张"
                : oldMissing < choice.Missing.Length ? $"还需多追 {choice.Missing.Length - oldMissing} 张" : "待抓张数相同";
            if (targets.Length > 0)
            {
                Line($"原阵容持有 {before}/{targets.Length}，新阵容 {choice.Held.Length}/{choice.Members.Length}；{comparison}");
                var currentHeld = targets.Where(_state.IsOwned).Distinct().ToList();
                bool NewlyFields(Func<OperatorProfile, bool> role) =>
                    !currentHeld.Any(n => _operators.GetValueOrDefault(n)?.Profile is { } op && role(op))
                    && choice.Held.Any(n => _operators.GetValueOrDefault(n)?.Profile is { } op && role(op));
                var gains = new List<string>();
                if (NewlyFields(p => p.IsTank)) gains.Add("前排");
                if (NewlyFields(p => p.IsHealer)) gains.Add("治疗");
                if (gains.Count > 0) Line($"转型可立即补上{string.Join("、", gains)}", true);
                else if (oldMissing <= choice.Missing.Length && choice.RoleGaps > 0) Line("仍缺关键功能位，可先保留原队", true);
            }
        }
        var cards = new WrapPanel();
        foreach (var name in choice.Members)
        {
            var stack = new StackPanel { Width = 52, Margin = new Thickness(1, 3, 1, 3) };
            stack.Children.Add(new Avatar { Text = name, Source = _operators.GetValueOrDefault(name)?.IconPath, Width = 30, Height = 30, Small = true });
            stack.Children.Add(Text(name, 10, "TextPrimaryBrush", center: true));
            cards.Children.Add(stack);
        }
        PivotDetails.Children.Add(cards);
        Line(choice.Reason);
        Line(choice.Missing.Length == 0 ? "已持有全部目标干员" : $"还需抓取：{string.Join("、", choice.Missing)}", true);
        Line(choice.Replay);
        var lost = RetiredTargets(choice);
        Line(lost.Length > 0 ? $"切换后停止提醒：{string.Join("、", lost)}" : "原有待买目标均在新清单中");
    }

    private string[] RetiredTargets(MapPivotChoice choice)
    {
        var preview = new TrackingViewModel();
        preview.TrackLineup(choice.Lineup);
        var keep = preview.ActiveEntries.Select(e => (e.Type, e.Name)).ToHashSet();
        return _tracking!.ActiveEntries.Where(e => !e.IsAcquired && !keep.Contains((e.Type, e.Name))).Select(e => e.Name).Distinct().ToArray();
    }

    private async void OnApplyPivot(object sender, RoutedEventArgs e)
    {
        if (PivotChoices.SelectedItem is not MapPivotChoice selected || _pivotSearch != null) return;
        bool append = sender == PivotAppendButton;
        var retired = RetiredTargets(selected);
        using var verify = new CancellationTokenSource();
        _pivotSearch = verify;
        PivotChoices.IsEnabled = PivotReplaceButton.IsEnabled = PivotAppendButton.IsEnabled = false;
        bool changed;
        try
        {
            var refreshed = (await GetChoices(verify.Token)).FirstOrDefault(c => c.Lineup.Id == selected.Lineup.Id);
            if (verify.IsCancellationRequested) return;
            changed = refreshed == null || refreshed.Reason != selected.Reason || !refreshed.Missing.SequenceEqual(selected.Missing)
                || !retired.SequenceEqual(RetiredTargets(refreshed));
            if (!changed)
            {
                _tracking!.TrackLineup(refreshed!.Lineup, append);
                _state.MapLineupId = refreshed.Lineup.Id;
                SaveState();
                _frozenCandidates = null;
                Notice($"已{(append ? "追加" : "切换")}追踪阵容 · 持有 {refreshed.Held.Length}/{refreshed.Members.Length}"
                    + (refreshed.Missing.Length == 0 ? "" : $"，还需{string.Join("、", refreshed.Missing)}"));
                OnClosePivot(sender, e);
                ScheduleReplan();
            }
        }
        catch (OperationCanceledException) when (verify.IsCancellationRequested) { return; }
        finally { PivotChoices.IsEnabled = true; if (ReferenceEquals(_pivotSearch, verify)) _pivotSearch = null; }
        if (changed) { await LoadPivots(); PivotStatus.Text = "本局条件已变化，请核对更新后的候选"; }
    }
}
