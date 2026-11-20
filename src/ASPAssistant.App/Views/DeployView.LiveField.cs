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
    private bool _reviewSlot;
    private bool _readingField;
    private bool ShowingLive => ModeList.SelectedIndex == 0;
    private GarrisonMap? DisplayMap => ShowingLive && _roster.Map is string map ? _board?.Find(map) ?? CurrentMap : CurrentMap;
    private IReadOnlyList<LiveUnit> VisibleUnits => _roster.Map == DisplayMap?.Id ? _roster.Units : [];

    public void ConnectLiveField(OcrScannerService scanner)
    {
        _liveScanner = scanner;
        scanner.LiveFieldChanged += () => Dispatcher.InvokeAsync(() => ShowLiveRoster(scanner.LiveRoster));
    }

    internal void ShowLiveRoster(LiveRoster roster)
    {
        var previousMap = CurrentMap?.Id;
        var previous = _ready && !ShowingLive ? Candidates() : null;
        _roster = roster;
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
        foreach (var unit in VisibleUnits.Where(u => !u.Mark.Slot.Bench))
        {
            var name = unit.Name ?? "待复核";
            var op = unit.Name == null ? null : _operators.GetValueOrDefault(unit.Name);
            var content = new DockPanel();
            content.Children.Add(new Avatar { Text = unit.Name ?? "?", Source = op?.IconPath, Width = 24, Height = 24, Small = true });
            var location = Text(unit.Mark.Slot.ToString(), 10, "TextSecondaryBrush", margin: new Thickness(3,0,0,0));
            DockPanel.SetDock(location,Dock.Right);
            content.Children.Add(location);
            var body = new StackPanel { Margin = new Thickness(5,0,0,0) };
            body.Children.Add(Text(name,11,"TextPrimaryBrush",bold:true));
            if (op != null) body.Children.Add(Text(string.Join(" · ",op.CoreCovenants),9.5,"TextSecondaryBrush"));
            content.Children.Add(body);
            var card = new Border { Child=content, Padding=new Thickness(5,3,5,3), Margin=new Thickness(0,1,4,2),
                CornerRadius=new CornerRadius(4), BorderThickness=new Thickness(1), ContextMenu=LiveMenu(unit), ToolTip=unit.IdentitySource };
            card.SetResourceReference(Border.BackgroundProperty,"CardBackgroundBrush");
            card.SetResourceReference(Border.BorderBrushProperty,"BorderBrush");
            card.MouseLeftButtonUp += async (_,_) => { if (_reviewSlot) { _reviewSlot=false; await ReadField(unit); } };
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
            else { LiveMessage.Visibility=Visibility.Visible; LiveMessage.Text = (string)tile.ToolTip; } };
        return tile;
    }

    private ContextMenu LiveMenu(LiveUnit unit)
    {
        var menu = new ContextMenu();
        var review = new MenuItem {Header="复核此格"};
        review.Click += async (_,_) => await ReadField(unit);
        menu.Items.Add(review);
        var correct = new MenuItem {Header="更换干员并记住"};
        correct.Click += (_,_) =>
        {
            var strategies = _operators.Values.Select(o => o.RequiredStrategyId).Where(s => s.Length > 0).ToHashSet();
            var picker = new OperatorPickerWindow(_operators.Values, unit.Name ?? "", strategies)
                { Owner = Window.GetWindow(this) };
            picker.ClearSlotButton.Visibility = Visibility.Collapsed;
            picker.ShowDialog();
            if (picker.Result is not { Length: > 0 } name) return;
            LiveMessage.Visibility = Visibility.Visible;
            LiveMessage.Text = _liveScanner?.SetLiveIdentity(unit.Id, unit.Mark.Slot, name) == true
                ? $"本局已记住 · {unit.Mark.Slot} {name}" : "该干员已移动，请重新选择";
        };
        menu.Items.Add(correct);
        AddFeedbackItem(menu, unit.Mark.Slot);
        return menu;
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
        var roster = _roster;
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
            var recommendation = $"视图：{mode} · 候选：{string.Join("、",Candidates(roster).Select(c => c.Key))}";
            if (plan != null) recommendation += $" · 方案地图：{plan.Map.Id} · 该方案计算时的候选：{string.Join("、",planPool.Select(c => c.Key))} · 上阵：{string.Join("、",plan.Placed.Select(p => p.Key + " " + p.CellCode))} · 替补：{string.Join("、",plan.Bench.Select(b => b.Name))}";
            else recommendation += " · 暂无已计算方案";
            var purchase = RecognitionDiagnostics.Samples.Freeze().FirstOrDefault(s => s.Game == observation!.Game &&
                s.Round == roster.Round && s.Kind.StartsWith("实况购入", StringComparison.Ordinal));
            var sample = FieldFeedback.Mark(roster, slot, recommendation, purchase);
            string message;
            try
            {
                await Task.Run(() => FieldFeedback.Save(observation!.Game,sample));
                message = $"{slot} 已标记，可在「设置 → 关于 → 识别有误」补充并导出整局。";
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            { message = $"标记保存失败：{ex.Message}"; }
            if (ShowingLive) { LiveMessage.Text = message; LiveMessage.Visibility = Visibility.Visible; }
            else SetStatus(message);
        };
        menu.Items.Add(item);
    }

    private async void OnReadField(object sender,RoutedEventArgs e) => await ReadField(null);
    private async Task ReadField(LiveUnit? unit)
    {
        ModeList.SelectedIndex=0;
        if (_readingField || _liveScanner == null) return;
        _readingField=true;
        LiveMessage.Visibility=Visibility.Visible;
        ReadFieldButton.IsEnabled=false;
        LiveMessage.Text=unit == null ? "正在识别阵容…" : $"正在复核 {unit.Mark.Slot}…";
        try { LiveMessage.Text=await _liveScanner.ReadLiveFieldAsync(unit); }
        finally { _readingField=false; ReadFieldButton.IsEnabled=true; }
    }
    private void OnChooseFieldSlot(object sender,RoutedEventArgs e)
    {
        ModeList.SelectedIndex=0; _reviewSlot=true; LiveMessage.Visibility=Visibility.Visible;
        LiveMessage.Text="点场上或整备区的一名干员进行复核";
    }
}
