using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Interop;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;

namespace ASPAssistant.App.Windows;

internal sealed record GuideDraft(string Original, string Text);

public partial class TrackingGuideWindow : Window
{
    private readonly SidePanelWindow _panel;
    private readonly SettingsManager _settings;
    private readonly DispatcherTimer _layoutTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private GuideWindowSettings _layout;
    private TrackedLineup? _target;
    private Lineup? _source;
    private IReadOnlyList<SklandGuideStage> _stages = [];
    private string _stageId = "";
    private sealed record DraftPage(TrackedLineup Target, Lineup? Source, IReadOnlyList<SklandGuideStage> Stages,
        string StageId, Dictionary<string, GuideDraft> Drafts);
    private Dictionary<string, GuideDraft> _drafts = [];
    private readonly Dictionary<string, DraftPage> _parked = [];
    private bool _preview, _rendering, _saving, _restoreWithPanel;
    private readonly Dictionary<string, string> _conflicts = [];
    private bool HasDrafts => _drafts.Count > 0;
    internal bool HasAnyDrafts => HasDrafts || _parked.Count > 0;
    private Lineup? Library(string id) => _panel.LineupVm.Lineups.FirstOrDefault(l => l.Id == id);

    internal TrackingGuideWindow(SidePanelWindow panel, SettingsManager settings)
    {
        _panel = panel;
        _settings = settings;
        InitializeComponent();
        _layout = settings.LoadGuideWindow();
        Topmost = _layout.Pinned;
        ApplyAppearance();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var side = User32.ReadWindowRect(new WindowInteropHelper(panel).Handle);
            var scale = User32.GetWindowDpiScale(new WindowInteropHelper(panel).Handle);
            var width = _layout.Left.HasValue ? _layout.Width : (int)(Width * scale);
            var height = _layout.Top.HasValue ? _layout.Height : (int)(Height * scale);
            int x = _layout.Left ?? side.Left - width - 6, y = _layout.Top ?? side.Top + 55;
            var area = User32.GetMonitorGeometry(_layout.Left ?? side.Left, _layout.Top ?? side.Top).WorkArea;
            User32.SetWindowBounds(hwnd, WindowGeometry.ClampBounds(new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height }, area));
        };
        LocationChanged += (_, _) => LayoutChanged();
        SizeChanged += (_, _) => LayoutChanged();
        _layoutTimer.Tick += async (_, _) => { _layoutTimer.Stop(); await PersistLayoutAsync(); };
        Closed += async (_, _) => { _layoutTimer.Stop(); await PersistLayoutAsync(); };
    }

    private void LayoutChanged()
    {
        if (!IsVisible || WindowState != WindowState.Normal) return;
        var bounds = User32.ReadWindowRect(new WindowInteropHelper(this).Handle);
        _layout = _layout with { Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height };
        _layoutTimer.Stop();
        _layoutTimer.Start();
    }

    private async Task PersistLayoutAsync()
    {
        try { await _settings.SaveGuideWindowAsync(_layout); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Status.Text = $"窗口设置保存失败：{ex.Message}"; }
    }

    private void ApplyAppearance()
    {
        Notes.FontSize = _layout.FontSize;
        FontValue.Text = _layout.FontSize.ToString();
        PinButton.Content = Topmost ? "已置顶" : "置顶";
    }

    private void SetFont(int delta)
    {
        _layout = _layout with { FontSize = Math.Clamp(_layout.FontSize + delta, 12, 32) };
        ApplyAppearance(); _ = PersistLayoutAsync();
    }
    private void ReduceFont(object sender, RoutedEventArgs e) => SetFont(-1);
    private void EnlargeFont(object sender, RoutedEventArgs e) => SetFont(1);
    private void TogglePin(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost; _layout = _layout with { Pinned = Topmost };
        ApplyAppearance(); _ = PersistLayoutAsync();
    }
    private void DragTitle(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) MaximizeGuide(sender, e); else DragMove();
    }
    private void MinimizeGuide(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeGuide(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseGuide(object sender, RoutedEventArgs e) => Hide();
    internal void Present(bool toggle)
    {
        _restoreWithPanel = false;
        if (IsVisible && WindowState != WindowState.Minimized && toggle) { Hide(); return; }
        if (WindowState == WindowState.Minimized) { Hide(); WindowState = WindowState.Normal; }
        SyncTracking();
        Show();
        User32.SetWindowLayer(new WindowInteropHelper(this).Handle, Topmost ? new IntPtr(-1) : IntPtr.Zero);
    }
    internal void TrayChanged(bool hidden)
    {
        if (hidden) { _restoreWithPanel = IsVisible; if (_restoreWithPanel) Hide(); }
        else if (_restoreWithPanel) { _restoreWithPanel = false; Show(); }
    }

    internal void SyncTracking()
    {
        var live = _panel.TrackingVm.CurrentMapLineup;
        if (live is { Stages.Count: 0 }) live = null;
        if (live is null && !HasDrafts)
        {
            if (_parked.Count == 0) { _target = null; Hide(); return; }
            SwitchPage(_parked.Values.Last().Target);
        }
        if (live != null && (_target == null || (!HasDrafts && !_saving && _target.Id != live.Id)))
            SwitchPage(live);
        if (_target is null) return;
        if (!HasDrafts && Library(_target.Id) is { } latest && !ReferenceEquals(latest, _source))
        {
            _source = latest; _stages = TrackingViewModel.LineupStages(latest);
        }
        if (live?.Id == _target.Id && !_preview && !HasDrafts && !Notes.IsKeyboardFocusWithin)
            _stageId = live.SelectedStageId;
        if (_stages.All(s => s.Id != _stageId)) _stageId = _stages.First().Id;
        Render();
    }

    private void Render()
    {
        var live = _panel.TrackingVm.CurrentMapLineup;
        var stage = _stages.Single(s => s.Id == _stageId);
        var same = live?.Id == _target!.Id;
        Team.Text = same ? _target.Name : $"正在编辑：{_target.Name}";
        var trackedStage = live?.Stages.FirstOrDefault(s => s.Id == live.SelectedStageId);
        Following.Text = live == null ? "当前没有追踪阵容" : $"当前追踪：{(same ? "" : live.Name + " · ")}{trackedStage?.Name}";
        OtherDrafts.Visibility = _parked.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Tabs.Children.Clear();
        foreach (var s in _stages.OrderBy(s => s.IsFinal))
        {
            var button = new Button { Content = s.Name + (same && s.Id == live!.SelectedStageId ? " ●" : ""),
                Style = (Style)FindResource(s.Id == _stageId ? "DialogPrimaryButton" : "DialogSecondaryButton"), Margin = new Thickness(0,0,3,0) };
            button.Click += (_, _) => { _stageId = s.Id; _preview = true; Render(); };
            Tabs.Children.Add(button);
        }
        StageTitle.Text = stage.IsFinal ? stage.Name : $"{stage.Name} · {stage.RangeText}";
        FollowButton.Visibility = live != null && (!same || live.SelectedStageId != _stageId || _preview) ? Visibility.Visible : Visibility.Collapsed;
        _rendering = true;
        var text = _drafts.TryGetValue(stage.Id, out var draft) ? draft.Text : stage.OperationIdea;
        if (Notes.Text != text) Notes.Text = text;
        _rendering = false;
        SaveOriginal.Visibility = Library(_target.Id) == null ? Visibility.Collapsed : Visibility.Visible;
        SaveOriginal.IsEnabled = !_saving && HasDrafts && Library(_target.Id) != null;
        SaveCopy.IsEnabled = !_saving && _source != null;
        SaveLabel.Text = !same ? $"保存到「{_target.Name}」" : "保存到原阵容";
        SaveOriginal.ToolTip = $"保存到「{_target.Name}」";
    }

    private void Follow(object sender, RoutedEventArgs e)
    {
        var live = _panel.TrackingVm.CurrentMapLineup;
        if (live is null) return;
        if (live.Id != _target!.Id) SwitchPage(live);
        _preview = false; _stageId = live.SelectedStageId;
        SyncTracking();
    }

    private void SwitchPage(TrackedLineup next)
    {
        if (HasDrafts && _target != null)
            _parked[_target.Id] = new(_target, _source, _stages, _stageId, _drafts);
        _conflicts.Clear(); UseSaved.Visibility = Visibility.Collapsed;
        CopyPanel.Visibility = Visibility.Collapsed; SaveCopy.Content = "另存为新阵容";
        _target = next;
        if (_parked.Remove(next.Id, out var page))
        {
            _source = page.Source; _stageId = page.StageId; _stages = page.Stages; _drafts = page.Drafts;
            _preview = true; Status.Text = "未保存"; return;
        }
        _source = Library(next.Id) ?? next.GuideSource;
        _stages = _source != null ? TrackingViewModel.LineupStages(_source)
            : next.Stages.Select(s => new SklandGuideStage { Id = s.Id, Name = s.Name, StartRound = s.StartRound, EndRound = s.EndRound, IsFinal = s.IsFinal }).ToList();
        _stageId = next.SelectedStageId;
        _drafts = []; _preview = false;
        Status.Text = _source == null ? "旧追踪记录缺少完整阵容，可选中讲解复制。" : "";
    }

    private void ChooseDraft(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = OtherDrafts };
        foreach (var page in _parked.Values)
        {
            var item = new MenuItem { Header = page.Target.Name };
            item.Click += (_, _) => { SwitchPage(page.Target); Render(); };
            menu.Items.Add(item);
        }
        OtherDrafts.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void EditNote(object sender, TextChangedEventArgs e)
    {
        if (_rendering || _target is null) return;
        var original = _drafts.TryGetValue(_stageId, out var draft)
            ? draft.Original : _stages.Single(s => s.Id == _stageId).OperationIdea;
        if (Notes.Text == original) _drafts.Remove(_stageId);
        else _drafts[_stageId] = new(original, Notes.Text);
        Status.Text = HasDrafts ? "未保存" : "";
        SaveOriginal.IsEnabled = !_saving && HasDrafts && Library(_target.Id) != null;
    }

    private async void SaveClick(object sender, RoutedEventArgs e) => await SaveAsync(false);
    private async void CopyClick(object sender, RoutedEventArgs e)
    {
        if (CopyPanel.Visibility == Visibility.Visible) { await SaveAsync(true); return; }
        CopyName.Text = _target!.Name + " 副本";
        CopyPanel.Visibility = Visibility.Visible;
        CopyName.Focus(); CopyName.SelectAll();
        SaveCopy.Content = "确认另存";
    }

    private void UseLatest(object sender, RoutedEventArgs e)
    {
        var latest = Library(_target!.Id);
        if (latest == null) return;
        _source = latest;
        _stages = TrackingViewModel.LineupStages(latest);
        foreach (var id in _conflicts.Keys) _drafts.Remove(id);
        _conflicts.Clear(); UseSaved.Visibility = Visibility.Collapsed;
        Status.Text = HasDrafts ? "其他阶段的草稿仍待保存" : "已采用保存的讲解";
        SyncTracking();
    }

    internal async Task SaveAsync(bool copy)
    {
        if (_saving || _target == null) return;
        if (copy && string.IsNullOrWhiteSpace(CopyName.Text)) { Status.Text = "请输入新阵容名称"; return; }
        _saving = true;
        IsEnabled = false;
        await _panel.GuideSaveGate.WaitAsync();
        try
        {
            var existing = Library(_target.Id);
            var original = existing ?? (copy ? _source : null);
            if (original == null) { Status.Text = "原阵容未入库或已删除，可另存完整阵容。"; return; }
            var result = JsonSerializer.Deserialize<Lineup>(JsonSerializer.Serialize(original))!;
            result.AutoRoute = original.AutoRoute?.ForEditing(original);
            var stages = result.SklandGuide?.Stages ?? result.EditorStages;
            if (stages == null && HasDrafts)
                stages = result.EditorStages = TrackingViewModel.LineupStages(result).ToList();
            foreach (var (id, draft) in _drafts)
            {
                var dest = stages!.FirstOrDefault(s => s.Id == id);
                if (dest == null) { Status.Text = "原阵容已删除正在编辑的阶段，请复制这段草稿。"; return; }
                if (!copy && dest.OperationIdea != draft.Original && dest.OperationIdea != draft.Text
                    && _conflicts.GetValueOrDefault(id) != dest.OperationIdea)
                {
                    _conflicts[id] = dest.OperationIdea;
                    UseSaved.Visibility = Visibility.Visible;
                    Status.Text = $"「{dest.Name}」讲解已被修改。再次点保存采用本窗草稿，或采用已保存讲解。";
                    return;
                }
                dest.OperationIdea = draft.Text;
            }
            if (copy) { result.Id = Guid.NewGuid().ToString("N"); result.Name = CopyName.Text.Trim(); }
            await _panel.LineupVm.AddOrUpdateAsync(result);
            _panel.PlannerVm.NotifySavedAutoRouteSourceChanged(result);
            _drafts.Clear(); _conflicts.Clear(); UseSaved.Visibility = Visibility.Collapsed;
            _source = copy ? original : result;
            _stages = TrackingViewModel.LineupStages(_source);
            CopyPanel.Visibility = Visibility.Collapsed; SaveCopy.Content = "另存为新阵容";
            Status.Text = $"已保存到「{result.Name}」";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Status.Text = $"保存失败，草稿已保留：{ex.Message}"; }
        finally
        {
            _panel.GuideSaveGate.Release(); _saving = false; IsEnabled = true;
            SyncTracking();
        }
    }
}
