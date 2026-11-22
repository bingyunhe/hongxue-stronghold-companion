// Added for Hongxue Stronghold Companion on 2026-11-16; see NOTICE.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ASPAssistant.Core.Models;

namespace ASPAssistant.App.Windows;

public partial class LineupEditorWindow
{
    private const string StageDragFormat = "ASPAssistant.LineupStage";
    private Point? _stageDragStart;
    private int? _stageInsertionIndex;

    private void RefreshPhaseTabs()
    {
        PhaseTabs.Children.Clear();
        foreach (var (phase, name) in _vm.PhaseLabels)
        {
            var label = new TextBlock { TextWrapping = TextWrapping.Wrap };
            label.Inlines.Add(new Run(name));
            int count = _vm.FilledCount(phase);
            if (count > 0) label.Inlines.Add(new Run($" {count}") { FontFamily = (FontFamily)FindResource("NumberFontFamily"), FontSize = 11 });
            var button = new Button { Content = label, Style = (Style)FindResource("PhaseSegment"), DataContext = phase,
                Tag = phase == _vm.CurrentPhase ? "active" : null };
            button.SetBinding(MaxWidthProperty, new System.Windows.Data.Binding(nameof(ActualWidth)) { Source = PhaseTabs });
            button.Click += (_, _) => { _vm.SelectPhase(phase); RefreshEditor(); };
            if (phase != LineupPhase.后期)
            {
                button.ToolTip = "拖动调整阶段顺序；右键编辑阶段";
                button.PreviewMouseLeftButtonDown += (_, e) => _stageDragStart = e.GetPosition(PhaseTabs);
                button.PreviewMouseLeftButtonUp += (_, _) => _stageDragStart = null;
                button.LostMouseCapture += (_, _) => _stageDragStart = null;
                button.PreviewMouseMove += OnStageMouseMove;
                var menu = new ContextMenu();
                var rename = new MenuItem { Header = "重命名" };
                rename.Click += async (_, _) => await EditStageAsync(phase, StageEditorMode.Rename);
                var rounds = new MenuItem { Header = "重新选择回合" };
                rounds.Click += async (_, _) => await EditStageAsync(phase, StageEditorMode.Rounds);
                var duplicate = new MenuItem { Header = "复制此阶段" };
                duplicate.Click += (_, _) => { _vm.DuplicateStage(phase); RefreshEditor(); };
                var delete = new MenuItem { Header = "删除此阶段" };
                delete.SetResourceReference(ForegroundProperty, "DangerBrush");
                delete.Click += (_, _) =>
                {
                    if (!ConfirmDialog.Ask(this, "删除阶段", $"删除「{name}」及该阶段的干员配置？保存阵容后生效。", "删除阶段", danger: true)) return;
                    _vm.DeleteStage(phase);
                    RefreshEditor();
                };
                menu.Items.Add(rename); menu.Items.Add(rounds); menu.Items.Add(duplicate);
                menu.Items.Add(new Separator()); menu.Items.Add(delete);
                button.ContextMenu = menu;
            }
            PhaseTabs.Children.Add(button);
        }
        PhaseTabs.Children.Add(AddStageButton);
    }

    private void OnStageMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _stageDragStart is not { } start) return;
        var position = e.GetPosition(PhaseTabs);
        if (Math.Abs(position.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _stageDragStart = null;
        var button = (Button)sender;
        button.ReleaseMouseCapture();
        try { DragDrop.DoDragDrop(button, new DataObject(StageDragFormat, button), DragDropEffects.Move); }
        finally { StageDropMarker.Visibility = Visibility.Collapsed; _stageInsertionIndex = null; }
        e.Handled = true;
    }

    private Button? DraggedStage(DragEventArgs e) => e.Data.GetData(StageDragFormat) is Button button
        && button.DataContext is LineupPhase phase && phase != LineupPhase.后期 && PhaseTabs.Children.Contains(button) ? button : null;

    private int StageInsertionIndex(Point position)
    {
        for (int index = 1; index < PhaseTabs.Children.Count; index++)
        {
            var button = (Button)PhaseTabs.Children[index];
            var top = button.TranslatePoint(new Point(), PhaseTabs);
            if (position.Y < top.Y || position.Y <= top.Y + button.ActualHeight && position.X < top.X + button.ActualWidth / 2)
                return index;
        }
        return PhaseTabs.Children.Count - 1;
    }

    private void OnStageDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DraggedStage(e) is null)
        {
            e.Effects = DragDropEffects.None;
            StageDropMarker.Visibility = Visibility.Collapsed;
            _stageInsertionIndex = null;
            return;
        }
        e.Effects = DragDropEffects.Move;
        int index = StageInsertionIndex(e.GetPosition(PhaseTabs));
        if (_stageInsertionIndex == index) return;
        _stageInsertionIndex = index;
        var target = (Button)PhaseTabs.Children[index];
        var point = target.TranslatePoint(new Point(), PhaseTabs);
        Canvas.SetLeft(StageDropMarker, Math.Max(0, point.X - 2));
        Canvas.SetTop(StageDropMarker, point.Y);
        StageDropMarker.Height = target.ActualHeight;
        StageDropMarker.Visibility = Visibility.Visible;
    }

    private void OnStageDragLeave(object sender, DragEventArgs e)
    {
        if (new Rect(PhaseTabs.RenderSize).Contains(e.GetPosition(PhaseTabs))) return;
        StageDropMarker.Visibility = Visibility.Collapsed;
        _stageInsertionIndex = null;
    }

    private void OnStageDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        StageDropMarker.Visibility = Visibility.Collapsed;
        _stageInsertionIndex = null;
        if (DraggedStage(e) is not { } source) { e.Effects = DragDropEffects.None; return; }
        int index = StageInsertionIndex(e.GetPosition(PhaseTabs));
        if (index > PhaseTabs.Children.IndexOf(source)) index--;
        e.Effects = DragDropEffects.Move;
        if (_vm.MoveStage((LineupPhase)source.DataContext, index)) RefreshEditor();
    }

    private async void OnAddStage(object sender, RoutedEventArgs e)
    {
        var dialog = new LineupStageWindow(null, StageEditorMode.Add) { Owner = this };
        await MinimizableDialog.ShowAsync(dialog, EditorContent);
        if (dialog.Result is not { } result) return;
        _vm.AddStage(result.Name, result.StartRound, result.EndRound);
        RefreshEditor();
    }

    private async Task EditStageAsync(LineupPhase phase, StageEditorMode mode)
    {
        _vm.SelectPhase(phase);
        RefreshEditor();
        var dialog = new LineupStageWindow(_vm.CurrentStage, mode) { Owner = this };
        await MinimizableDialog.ShowAsync(dialog, EditorContent);
        if (dialog.Result is not { } result) return;
        _vm.UpdateStage(phase, result.Name, result.StartRound, result.EndRound);
        RefreshEditor();
    }
}
