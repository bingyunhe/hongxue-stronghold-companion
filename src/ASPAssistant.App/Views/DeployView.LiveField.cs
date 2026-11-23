using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ASPAssistant.App.Controls;
using ASPAssistant.App.Windows;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;

namespace ASPAssistant.App.Views;

public partial class DeployView
{
    private OcrScannerService? _liveScanner;
    private LiveRoster _roster = new(null, null, []);
    private LiveRoster? _recommendationRoster;
    private LiveRoster DisplayRoster => _recommendationRoster ?? _roster;
    private bool _reviewSlot;
    private bool _readingField;
    private bool ShowingLive => ModeList.SelectedIndex == 0;
    private GarrisonMap? DisplayMap => ShowingLive && _roster.Map is string map ? _board?.Find(map) ?? CurrentMap : CurrentMap;
    private IReadOnlyList<LiveUnit> VisibleUnits => _roster.Map == DisplayMap?.Id ? _roster.Units : [];

    public void ConnectLiveField(OcrScannerService scanner)
    {
        _liveScanner = scanner;
        scanner.SetReviewExclusions(_runExcluded);
        AutomaticFieldToggle.IsChecked = scanner.AutomaticLiveField = _state.AutomaticLiveField;
        long appliedSequence = 0;
        scanner.FieldOwnershipChanged += evidence =>
        {
            void Apply()
            {
                if (evidence.Sequence <= appliedSequence || !scanner.IsCurrentOwnership(evidence.Frame)) return;
                appliedSequence = evidence.Sequence;
                bool frozen = !ShowingLive && IsVisible && _shownPage == Page.Main && _plan != null;
                var previous = frozen ? Candidates() : null;
                if (!_ownership.Observe(evidence)) return;
                SaveState();
                RefreshOwnership(previous);
            }
            if (Dispatcher.CheckAccess()) Apply(); else Dispatcher.InvokeAsync(Apply);
        };
        scanner.LiveFieldChanged += () => Dispatcher.InvokeAsync(() => ShowLiveRoster(scanner.LiveRoster));
    }

    private void OnAutomaticFieldClick(object sender, RoutedEventArgs e)
    {
        _state.AutomaticLiveField = AutomaticFieldToggle.IsChecked == true;
        if (_liveScanner is not null) _liveScanner.AutomaticLiveField = _state.AutomaticLiveField;
        SaveState();
    }

    internal void ShowLiveRoster(LiveRoster roster)
    {
        if (_recommendationRoster is not null)
        {
            _roster = roster;
            return;
        }
        var previousMap = CurrentMap?.Id;
        var previous = _ready && !ShowingLive ? Candidates() : null;
        _roster = roster;
        if (_ready && IsVisible && !ShowingLive && roster.Units.Count > 0)
            _recommendationRoster = roster;
        if (previous is not null && (previousMap != CurrentMap?.Id || !previous.SequenceEqual(Candidates())))
        {
            _planCts?.Cancel();
            ScheduleReplan();
        }
        if (_ready && ShowingLive && _shownPage == Page.Main)
        {
            if (!_readingField && !_reviewSlot) LiveMessage.Text = "";
            RefreshAll();
        }
    }

    private void SetLiveSummary()
    {
        var last = _roster.Observations.LastOrDefault();
        LastFieldRead.Visibility = last is null ? Visibility.Collapsed : Visibility.Visible;
        LastFieldRead.Text = last is null ? "" : $"上次识别：第 {last.Round} 回合 {last.Frame.Time:HH:mm}";
        PoolSummary.Text = $"整备区 · {VisibleUnits.Count(u => u.Mark.Slot.Bench)} 人";
        PoolMeta.Text = $"上场 {VisibleUnits.Count(u => !u.Mark.Slot.Bench)}";
        if (_roster.Map == DisplayMap?.Id && _roster.Bought.Count > 0)
            PoolMeta.Text += " · 本回合购入 " + string.Join("、", _roster.Bought);
        PoolMeta.ToolTip = PoolMeta.Text;
    }

    private void RefreshLiveRows()
    {
        if (_roster.Map != DisplayMap?.Id && !_readingField) LiveMessage.Text = "点击「识别阵容」读取当前棋盘";
        LiveMessage.Visibility = string.IsNullOrEmpty(LiveMessage.Text) ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Visibility = DetailCard.Visibility = Visibility.Collapsed;
        BenchCard.Visibility = BenchRow.Visibility = Visibility.Visible;
        BenchRow.Columns = 10;
        BenchRow.Children.Clear();
        for (int col = 0; col < 10; col++)
        {
            var slot = new FieldSlot(col,-1);
            var tile = LiveTile(VisibleUnits.FirstOrDefault(u => u.Mark.Slot == slot));
            if (tile.ContextMenu == null)
            {
                tile.ContextMenu = new ContextMenu();
                AddFeedbackItem(tile.ContextMenu,slot);
                tile.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenSlot(slot); };
            }
            BenchRow.Children.Add(tile);
        }
        var known = VisibleUnits.Where(u => u.Name != null && _operators.ContainsKey(u.Name)).ToList();
        var positions = new Dictionary<string, (BoardCell Cell, Facing Facing)>();
        var pool = new List<PlacementCandidate>();
        foreach (var unit in known)
        {
            var name = unit.Name!;
            var candidate = new PlacementCandidate(name, _state.IsElite(name), pool.Count(p => p.Name == name));
            pool.Add(candidate);
            if (!unit.Mark.Slot.Bench) positions[candidate.Key] = (new(unit.Mark.Slot.Col,unit.Mark.Slot.Row),Facing.Right);
        }
        ChecksRow.Children.Clear();
        ChecksBorder.Visibility = Visibility.Visible;
        if (VisibleUnits.Any(u => u.Name == null))
            ChecksRow.Children.Add(Text("有待复核干员，阻挡与治疗待确认",10,"TextSecondaryBrush"));
        else if (positions.Count != 0 && DisplayMap != null && _planner != null)
        {
            var assessment = _planner.Evaluate(DisplayMap,pool,positions,new PlacementOptions { FieldCount = positions.Count });
            foreach (var check in assessment.Checks.Where(c => c.Text.Contains("阻挡") || c.Text.Contains("没人拦") || c.Text.Contains("没有奶")))
                ChecksRow.Children.Add(Text((check.Ok ? "✓ " : "× ") + check.Text,10,check.Ok ? "TextSecondaryBrush" : "DangerBrush",margin:new Thickness(2,0,5,0)));
        }
        OperatorGrid.Children.Clear();
        PlanPanel.Visibility = Visibility.Visible;
        foreach (var member in _roster.Unlocated)
        {
            var label=Text($"待定位 · {member.Name}（上次 {member.LastSlot}）",10,"TextSecondaryBrush",margin:new Thickness(2,0,5,0));
            label.ToolTip="本局曾确认，当前位置待核实。右键可移除已出售的干员。";
            label.ContextMenu=new ContextMenu();
            AddForgetItem(label.ContextMenu,member.Id);
            ChecksRow.Children.Add(label);
        }
        foreach (var unit in VisibleUnits.Where(u => !u.Mark.Slot.Bench || u.Suggestion!=null))
        {
            var name = unit.Name ?? "待复核";
            var op = unit.Name == null ? null : _operators.GetValueOrDefault(unit.Name);
            var content = new DockPanel();
            content.Children.Add(new Avatar { Text = unit.Name ?? "?", Source = op?.IconPath, Width = 24, Height = 24, Small = true });
            var location = Text(unit.Mark.Slot.ToString(), 10, "TextSecondaryBrush", margin: new Thickness(3,0,0,0));
            DockPanel.SetDock(location,Dock.Right);
            content.Children.Add(location);
            var body = new StackPanel { Margin = new Thickness(5,0,0,0) };
            if (unit.Suggestion is {} hint && CanChooseOperator(hint.Name))
            {
                var confirm=new Button { Content=$"确认{hint.Name}", HorizontalAlignment=HorizontalAlignment.Left,
                    Padding=new Thickness(4,0,4,0), FontSize=11, ToolTip=$"将本格确认为{hint.Name}，上次位于 {hint.LastSlot}" };
                confirm.SetResourceReference(StyleProperty,"SmallActionButton");
                var observed = _roster.Observations.LastOrDefault();
                confirm.Click+=async (_,e)=>{ e.Handled=true; await ConfirmSuggestion(unit, observed, [hint.Name]); };
                body.Children.Add(confirm);
                body.Children.Add(Text($"上次 {hint.LastSlot}",10,"TextSecondaryBrush"));
            }
            else body.Children.Add(Text(name,11,"TextPrimaryBrush",bold:true));
            if (op != null)
            {
                var covenants = Text("", 9.5, "TextSecondaryBrush");
                Controls.PanelToolTip.AppendNames(covenants, op.CoreCovenants, " · ");
                body.Children.Add(covenants);
            }
            content.Children.Add(body);
            var card = new Border { Child=content, Padding=new Thickness(5,3,5,3), Margin=new Thickness(0,1,4,2),
                CornerRadius=new CornerRadius(4), BorderThickness=new Thickness(1), ContextMenu=LiveMenu(unit), ToolTip=unit.IdentitySource };
            card.SetResourceReference(Border.BackgroundProperty,"CardBackgroundBrush");
            card.SetResourceReference(Border.BorderBrushProperty,"BorderBrush");
            card.MouseLeftButtonUp += async (_,_) => { if (_reviewSlot) { _reviewSlot=false; await ReadField(unit); } else OpenSlot(unit.Mark.Slot); };
            OperatorGrid.Children.Add(card);
        }
    }

    private Border LiveTile(LiveUnit? unit)
    {
        var tile = new Border { Width = S-2, Height = S-2, CornerRadius = new(4), Margin=new Thickness(0,0,1,0) };
        tile.SetResourceReference(Border.BackgroundProperty, unit != null && unit.Name == null ? "WarningTintBrush" : "SectionBackgroundBrush");
        if (unit == null) return tile;
        tile.Child = unit.Name is string name ? new Avatar {Text=name, Source=_operators.GetValueOrDefault(name)?.IconPath, Small=true}
            : Text("?",13,"TextPrimaryBrush",bold:true,center:true);
        tile.ToolTip = unit.Name == null ? $"{unit.Mark.Slot} · 身份待确认 · 右键复核或更换"
            : $"{unit.Mark.Slot} · {unit.Name} · {unit.IdentitySource}\n右键复核或更换";
        tile.ContextMenu = LiveMenu(unit);
        tile.Cursor = Cursors.Hand;
        tile.MouseLeftButtonUp += async (_,e) => { e.Handled=true; if (_reviewSlot) { _reviewSlot=false; await ReadField(unit); }
            else OpenSlot(unit.Mark.Slot); };
        return tile;
    }

    private ContextMenu LiveMenu(LiveUnit unit)
    {
        var menu = new ContextMenu();
        var observed = _liveScanner?.LiveRoster.Observations.LastOrDefault(o =>
            o.Units.Any(u => ReferenceEquals(u.Mark, unit.Mark)));
        var displayed = new List<string>();
        if (unit.Suggestion is {} hint && CanChooseOperator(hint.Name))
        {
            var accept=new MenuItem { Header=$"确认{hint.Name}（上次 {hint.LastSlot}）" };
            displayed.Add(hint.Name);
            accept.Click+=async (_,_)=>await ConfirmSuggestion(unit, observed, displayed.ToArray());
            menu.Items.Add(accept);
        }
        var review = new MenuItem {Header="复核此格"};
        if (unit.Name==null)
        {
            var quick=new List<MenuItem>();
            menu.Opened+=async (_,_) =>
            {
                foreach (var old in quick) menu.Items.Remove(old);
                quick.Clear();
                displayed.RemoveAll(n => n != unit.Suggestion?.Name);
                if (_liveScanner is not {} scanner) return;
                var ranked = await Task.Run(() =>
                {
                    var names = scanner.GetLiveReviewCandidates(unit, out var frame);
                    return (names, frame);
                });
                await menu.Dispatcher.InvokeAsync(() =>
                {
                    if (!menu.IsOpen || quick.Count>0) return;
                    foreach (string name in ranked.names.Where(n=>n!=unit.Suggestion?.Name && CanChooseOperator(n)))
                    {
                        var item=new MenuItem { Header=$"确认{name}" };
                        displayed.Add(name);
                        item.Click+=async (_,_)=>{
                            LiveMessage.Visibility=Visibility.Visible;
                            bool accepted = CanChooseOperator(name) && scanner.ConfirmLiveReviewName(unit,name);
                            LiveMessage.Text = accepted ? $"{unit.Mark.Slot} 已确认 · {name}" : "该格候选已变化，请重新打开菜单";
                            if (accepted && ranked.frame != null)
                                await scanner.SavePlayerCorrectionAsync(ranked.frame, unit.Mark.Slot, "右键·快捷确认", displayed.ToArray(), name);
                        };
                        menu.Items.Insert(menu.Items.IndexOf(review),item);
                        quick.Add(item);
                    }
                });
            };
        }
        review.Click += async (_,_) => await ReadField(unit);
        menu.Items.Add(review);
        var correct = new MenuItem {Header="更换干员并记住"};
        correct.Click += async (_,_) =>
        {
            var scanner = _liveScanner;
            FieldObservation? pickedFrame = null;
            var ranked = scanner?.GetLiveReviewCandidates(unit, out pickedFrame).Where(CanChooseOperator).ToArray();
            if (PickRunOperator(_operators.Values, unit.Name ?? "", ranked)
                is not { Length: > 0 } name) return;
            LiveMessage.Visibility = Visibility.Visible;
            bool accepted = scanner?.SetLiveIdentity(unit, name) == true;
            LiveMessage.Text = accepted ? $"本局已记住 · {unit.Mark.Slot} {name}" : "该干员已移动，请重新选择";
            if (accepted && pickedFrame != null)
                await scanner!.SavePlayerCorrectionAsync(pickedFrame, unit.Mark.Slot, "右键·更换干员", ranked!, name);
        };
        menu.Items.Add(correct);
        if (unit.MemberId is long member) AddForgetItem(menu,member);
        AddFeedbackItem(menu, unit.Mark.Slot);
        return menu;
    }

    private async Task ConfirmSuggestion(LiveUnit unit, FieldObservation? observed, string[] displayed)
    {
        LiveMessage.Visibility=Visibility.Visible;
        var scanner = _liveScanner;
        if (unit.Suggestion is not { } suggestion || !CanChooseOperator(suggestion.Name) || scanner?.ConfirmLiveSuggestion(unit) != true)
        {
            LiveMessage.Text = "该格候选已变化，请查看最新识别结果";
            return;
        }
        LiveMessage.Text = $"本局已记住 · {unit.Mark.Slot} {suggestion.Name}";
        if (observed != null)
            await scanner.SavePlayerCorrectionAsync(observed, unit.Mark.Slot, "同局位置建议", displayed, suggestion.Name);
    }

    private void AddForgetItem(ContextMenu menu,long id)
    {
        var remove=new MenuItem {Header="已出售，移除本局记录"};
        var displayed = _roster;
        remove.Click+=(_,_)=>_liveScanner?.ForgetLiveMember(id, displayed);
        menu.Items.Add(remove);
    }

    private void OnBoardFeedbackMenu(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(BoardCanvas);
        var cell = new BoardCell((int)Math.Floor((p.X - L) / S) + _firstCol, (int)Math.Floor((p.Y - T) / S));
        if (!Visible(cell)) return;
        var slot = new FieldSlot(cell.Col,cell.Row);
        var unit = ShowingLive ? VisibleUnits.FirstOrDefault(u => u.Mark.Slot == slot) : null;
        var menu = unit == null ? new ContextMenu() : LiveMenu(unit);
        if (unit == null) AddFeedbackItem(menu,slot);
        menu.PlacementTarget = BoardCanvas;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void AddFeedbackItem(ContextMenu menu, FieldSlot slot)
    {
        var roster = DisplayRoster;
        var observation = roster.Observations.LastOrDefault();
        var item = new MenuItem { Header = ShowingLive ? "这格识别有误" : "标记推荐站位有误",
            IsEnabled = observation != null && observation.Map == DisplayMap?.Id };
        var mode = ShowingLive ? "实况" : _state.Mode switch { PlacementMode.Combat => "战斗", PlacementMode.Stacking => "叠层", _ => "平衡" };
        var plan = _recommended;
        var planPool = _recommendedPool;
        menu.Opened += (_,_) =>
        {
            var latest = _liveScanner?.LiveRoster;
            if (latest?.Observations.LastOrDefault() is not { } recent || recent.Game != observation?.Game ||
                latest.Map != roster.Map || latest.Round != roster.Round || !latest.Bought.SequenceEqual(roster.Bought)) return;
            if (latest.Units.Select(u => (u.Id,u.Mark.Slot,u.Name,u.IdentitySource))
                .SequenceEqual(roster.Units.Select(u => (u.Id,u.Mark.Slot,u.Name,u.IdentitySource)))) roster = latest;
        };
        item.Click += async (_,_) =>
        {
            var recommendation = $"视图：{mode} · 候选：{string.Join("、",Candidates().Select(c => c.Key))}";
            if (plan != null) recommendation += $" · 方案地图：{plan.Map.Id} · 该方案计算时的候选：{string.Join("、",planPool.Select(c => c.Key))} · 上阵：{string.Join("、",plan.Placed.Select(p => p.Key + " " + p.CellCode))} · 替补：{string.Join("、",plan.Bench.Select(b => b.Name))}";
            else recommendation += " · 暂无已计算方案";
            var purchase = RecognitionDiagnostics.Samples.Freeze().FirstOrDefault(s => s.Game == observation!.Game &&
                s.Round == roster.Round && s.Kind.StartsWith("实况购入", StringComparison.Ordinal));
            var sample = FieldFeedback.Mark(roster, slot, recommendation, purchase);
            string message;
            try
            {
                await Task.Run(() => FieldFeedback.Save(observation!.Game,sample));
                message = $"{slot} 已记录";
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            { message = $"标记保存失败：{ex.Message}"; }
            if (ShowingLive) { LiveMessage.Text = message; LiveMessage.Visibility = Visibility.Visible; }
            else SetStatus(message);
        };
        menu.Items.Add(item);
    }

    private async void OnReadField(object sender,RoutedEventArgs e) => await ReadField(null);
    private void OnReadFieldOptions(object sender, RoutedEventArgs e)
    {
        var menu = ReadFieldButton.ContextMenu;
        menu.PlacementTarget = ReadFieldOptions;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async void OnReadOwnBoard(object sender, RoutedEventArgs e) => await ReadField(null, ownBoard: true);
    private async Task ReadField(LiveUnit? unit, bool ownBoard = false)
    {
        if (_readingField || _liveScanner == null) return;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!ShowingLive) _returnMode = ModeList.SelectedIndex;
        ModeList.SelectedIndex=0;
        HideCorrection();
        _readingField=true;
        LiveMessage.Visibility=Visibility.Visible;
        ReadFieldButton.IsEnabled=false;
        LiveMessage.Text=unit == null ? "正在识别阵容…" : $"正在复核 {unit.Mark.Slot}…";
        var previous = _liveScanner.LiveRoster.Observations.LastOrDefault();
        try
        {
            LiveMessage.Text = await _liveScanner.ReadLiveFieldAsync(unit, ownBoard);
            var roster = _liveScanner.LiveRoster;
            ShowLiveRoster(roster);
            if (IsVisible && unit == null && roster.Observations.LastOrDefault() is { } observed && !ReferenceEquals(previous, observed)
                && LiveMessage.Text.StartsWith("上场", StringComparison.Ordinal))
            {
                var unknown = roster.Units.FirstOrDefault(u => u.Name == null);
                if (unknown != null) OpenSlot(unknown.Mark.Slot);
                else if (observed.UncertainSlots.Length > 0 && observed.PersonalBoard)
                    OpenSlot(observed.UncertainSlots[0]);
                else FinishCorrection($"阵容识别完成 · 已认出 {roster.Units.Count} 人");
            }
            if (unit == null && IsVisible)
                _ = Dispatcher.InvokeAsync(() => ASPAssistant.Core.AppLogger.Info("LiveField",
                    $"manual click-to-render-queue={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}ms"),
                    System.Windows.Threading.DispatcherPriority.Loaded);
        }
        finally { _readingField=false; ReadFieldButton.IsEnabled=true; }
    }
    private void OnChooseFieldSlot(object sender,RoutedEventArgs e)
    {
        ModeList.SelectedIndex=0; _reviewSlot=true; LiveMessage.Visibility=Visibility.Visible;
        LiveMessage.Text="点场上或整备区的一名干员进行复核";
    }
}
