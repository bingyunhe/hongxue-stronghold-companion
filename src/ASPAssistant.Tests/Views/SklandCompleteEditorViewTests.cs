using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ASPAssistant.App;
using ASPAssistant.App.Windows;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

internal static class SklandCompleteEditorViewTests
{
    public static void Verify(GameShopTheme theme, bool night)
    {
        ThemeManager.Apply(theme, night);
        var data = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "data"));
        var operators = data.LoadOperatorsAsync().GetAwaiter().GetResult();
        var strategies = data.LoadStrategiesAsync().GetAwaiter().GetResult();
        var (equipment, _) = data.LoadEquipmentAsync().GetAwaiter().GetResult();
        var (covenants, map) = data.LoadCovenantsAsync().GetAwaiter().GetResult();
        using var http = new HttpClient(new Handler());
        var imported = new SklandGuideImporter(http, operators, strategies).ImportAsync(Services.SklandGuideImporterTests.Url).GetAwaiter().GetResult();
        var before = JsonSerializer.Serialize(imported.Lineup);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var timer = Stopwatch.StartNew();
        var vm = new LineupEditorViewModel(imported.Lineup, operators, equipment, new CovenantCalculator(operators, covenants, map));
        var window = new LineupEditorWindow(vm, strategies) { Width = 760, Height = 680 };
        window.ShowImportNotice(imported.Notice);
        void Click(string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        LineupStageWindow Add()
        {
            Click("AddStageButton"); Pump();
            var dialog = window.OwnedWindows.OfType<LineupStageWindow>().Single();
            WindowUiScale.ScaleOf(dialog).Should().Be(WindowUiScale.ScaleOf(window));
            return dialog;
        }
        void Save(LineupStageWindow dialog) { ((Button)dialog.FindName("StageSaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
        try
        {
            window.Show(); ThemeManager.Apply(theme, night); Pump(); Pump(); timer.Stop();
            ((Panel)window.FindName("PhaseTabs")).Children.Count.Should().Be(4);
            string.Concat(((TextBlock)((Button)((Panel)window.FindName("PhaseTabs")).Children[0]).Content).Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)).Should().Be("成型阵容 8");
            ((Panel)window.FindName("PhaseTabs")).Children.OfType<Button>().Single(b => Equals(b.DataContext, LineupPhase.后期)).ContextMenu.Should().BeNull();
            vm.Guide!.Stages[0].RecommendedEquipments.Should().HaveCount(10);
            Capture(window, $"{theme}-{night}-final");
            Click("SourcePageButton"); Pump();
            ((Grid)window.FindName("LineupPage")).Visibility.Should().Be(Visibility.Collapsed);
            var sourcePage = (ScrollViewer)window.FindName("GuideSourcePage");
            ((Button)window.FindName("SourcePageButton")).Content.Should().Be("来源");
            Descendants<Expander>(sourcePage).Should().BeEmpty();
            var lineupTab = (Button)window.FindName("LineupPageButton");
            var sourceTab = (Button)window.FindName("SourcePageButton");
            lineupTab.ActualWidth.Should().BeApproximately(sourceTab.ActualWidth, 0.1);
            lineupTab.ActualWidth.Should().BeGreaterThan(60);
            ((SolidColorBrush)((Border)window.FindName("EditorPageSwitch")).Background).Color.A.Should().Be(255);
            ((Border)lineupTab.Template.FindName("bg", lineupTab)).BorderThickness.Should().Be(new Thickness(0));
            Descendants<TextBox>(sourcePage).Should().ContainSingle().Which.IsReadOnly.Should().BeTrue();
            ((TextBlock)window.FindName("GuideAuthorText")).Text.Should().Contain("攻略作者");
            ((TextBlock)window.FindName("GuideAuthorText")).FontSize.Should().Be(14);
            ((TextBlock)window.FindName("GuideScopeText")).FontSize.Should().Be(14);
            ((TextBox)window.FindName("SourceUrlBox")).FontSize.Should().Be(14);
            ((TextBlock)window.FindName("ImportNoticeText")).FontSize.Should().Be(12);
            Capture(window, $"{theme}-{night}-source");
            Click("LineupPageButton"); Pump();
            var tabs = (Panel)window.FindName("PhaseTabs");
            tabs.Children.OfType<Button>().ElementAt(1).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            ((ItemsControl)window.FindName("SlotsHost")).Items.Count.Should().Be(15);
            var notes = ((Panel)window.FindName("GuideContentHost")).Children.OfType<Expander>().Single(e => Equals(e.Header, "攻略讲解 · 可编辑"));
            notes.IsExpanded = true;
            var text = (TextBox)notes.Content; text.Text += "\n手动编辑的阶段讲解";
            vm.CurrentStage.OperationIdea.Should().EndWith("手动编辑的阶段讲解");
            Click("SourcePageButton"); Click("LineupPageButton");
            vm.CurrentStage.OperationIdea.Should().EndWith("手动编辑的阶段讲解");
            var viewport = (ScrollViewer)window.FindName("EditorStageScroll");
            viewport.ScrollToVerticalOffset(190); Pump(); Capture(window, $"{theme}-{night}-cards");
            viewport.ScrollToTop(); window.Height = 520; Pump(); Capture(window, $"{theme}-{night}-small"); window.Height = 680;
            var dialog = Add();
            Capture(dialog, $"{theme}-{night}-add");
            var start = (ComboBox)dialog.FindName("StageStartBox"); var end = (ComboBox)dialog.FindName("StageEndBox");
            start.SelectedIndex = 4;
            Save(dialog);
            ((TextBlock)dialog.FindName("StageError")).Text.Should().Contain("同时填写");
            Capture(dialog, $"{theme}-{night}-error");
            end.SelectedIndex = 3; Save(dialog);
            ((TextBlock)dialog.FindName("StageError")).Text.Should().Contain("不能晚于");
            end.SelectedIndex = 6; Pump(); Capture(dialog, $"{theme}-{night}-range");
            ((TextBox)dialog.FindName("StageNameBox")).Text.Should().Be("第 4–6 回合");
            ((TextBox)dialog.FindName("StageNameBox")).IsReadOnly.Should().BeTrue();
            Save(dialog);
            vm.CurrentStage.Name.Should().Be("第 4–6 回合");
            vm.CurrentStage.StartRound.Should().Be(4);
            vm.Guide.Stages.Should().HaveCount(4);
            var stageId = vm.CurrentStage.Id;
            var current = tabs.Children.OfType<Button>().Single(b => Equals(b.DataContext, vm.CurrentPhase));
            current.ContextMenu!.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "重命名")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump(); dialog = window.OwnedWindows.OfType<LineupStageWindow>().Single();
            ((TextBox)dialog.FindName("StageNameBox")).Text = "过渡准备"; Save(dialog);
            vm.CurrentStage.Name.Should().Be("过渡准备"); vm.CurrentStage.StartRound.Should().Be(4);
            current = tabs.Children.OfType<Button>().Single(b => Equals(b.DataContext, vm.CurrentPhase));
            current.ContextMenu!.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "重新选择回合")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump(); dialog = window.OwnedWindows.OfType<LineupStageWindow>().Single();
            ((ComboBox)dialog.FindName("StageStartBox")).SelectedIndex = 8;
            ((ComboBox)dialog.FindName("StageEndBox")).SelectedIndex = 10; Save(dialog);
            vm.CurrentStage.Name.Should().Be("第 8–10 回合"); vm.CurrentStage.Id.Should().Be(stageId);
            dialog = Add(); ((TextBox)dialog.FindName("StageNameBox")).Text = "临时补位"; Save(dialog);
            vm.CurrentStage.StartRound.Should().BeNull(); vm.CurrentStage.EndRound.Should().BeNull(); vm.CurrentStage.Name.Should().Be("临时补位");
            var count = vm.Guide.Stages.Count;
            dialog = Add(); ((Button)dialog.FindName("StageCancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            vm.Guide.Stages.Count.Should().Be(count);
            window.Width = 1100; Pump(); Capture(window, $"{theme}-{night}-wide");
            var saved = vm.ToLineup();
            saved.SklandGuide!.Stages[1].OperationIdea.Should().EndWith("手动编辑的阶段讲解");
            saved.SklandGuide.Stages.Should().HaveCount(5);
            saved.EarlySlots.Should().BeEmpty(); saved.MidSlots.Should().BeEmpty();
            var originalPhase = vm.PhaseLabels.Keys.ElementAt(1);
            var originalId = saved.SklandGuide.Stages[1].Id;
            current = tabs.Children.OfType<Button>().Single(b => Equals(b.DataContext, originalPhase));
            var menu = current.ContextMenu!;
            menu.PlacementTarget = current;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true; Pump();
            Capture(window, $"{theme}-{night}-stage-menu", menu);
            menu.IsOpen = false;
            var duplicateTimer = Stopwatch.StartNew();
            current.ContextMenu!.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "复制此阶段")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump();
            duplicateTimer.Stop();
            vm.PhaseLabels.Keys.ElementAt(2).Should().Be(vm.CurrentPhase);
            vm.CurrentStage.Name.Should().Be(saved.SklandGuide.Stages[1].Name + " 副本");
            vm.CurrentStage.Id.Should().NotBe(originalId);
            vm.Slots.Should().HaveCount(15);
            vm.CurrentStage.OperationIdea.Should().EndWith("手动编辑的阶段讲解");
            window.Width = 760; Pump();
            var stagePanel = (WrapPanel)window.FindName("PhaseTabs");
            var positions = stagePanel.Children.OfType<Button>().Select(b => b.TranslatePoint(new Point(), stagePanel)).ToList();
            positions.Select(p => Math.Round(p.Y)).Distinct().Count().Should().BeGreaterThan(1);
            foreach (var button in stagePanel.Children.OfType<Button>())
                (button.TranslatePoint(new Point(), stagePanel).X + button.ActualWidth).Should().BeLessThanOrEqualTo(stagePanel.ActualWidth + 1);
            Descendants<CheckBox>(window).Should().NotContain(b => b.Content is string && ((string)b.Content).Contains("上阵"));
            Capture(window, $"{theme}-{night}-copied");
            DragEventArgs Drag(Button source, Point point, RoutedEvent routedEvent)
            {
                var args = (DragEventArgs)typeof(DragEventArgs).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Single().Invoke([new DataObject("ASPAssistant.LineupStage", source), DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, stagePanel, point]);
                args.GetPosition(stagePanel).X.Should().BeApproximately(point.X, 0.1);
                args.GetPosition(stagePanel).Y.Should().BeApproximately(point.Y, 0.1);
                args.RoutedEvent = routedEvent; stagePanel.RaiseEvent(args); return args;
            }
            var dragged = stagePanel.Children.OfType<Button>().Last(b => b.DataContext is LineupPhase);
            var draggedPhase = (LineupPhase)dragged.DataContext;
            var activeId = vm.CurrentStage.Id;
            var firstTarget = (Button)stagePanel.Children[1];
            var targetPoint = firstTarget.TranslatePoint(new Point(1, firstTarget.ActualHeight / 2), stagePanel);
            Drag(dragged, targetPoint, DragDrop.PreviewDragOverEvent).Effects.Should().Be(DragDropEffects.Move);
            var marker = (Border)window.FindName("StageDropMarker"); marker.Visibility.Should().Be(Visibility.Visible);
            Pump(); Capture(window, $"{theme}-{night}-drag-marker");
            var hoverTimer = Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++) Drag(dragged, targetPoint, DragDrop.PreviewDragOverEvent);
            hoverTimer.Stop();
            Drag(dragged, targetPoint, DragDrop.PreviewDropEvent);
            Pump();
            vm.PhaseLabels.Keys.ElementAt(1).Should().Be(draggedPhase);
            vm.CurrentStage.Id.Should().Be(activeId);
            marker.Visibility.Should().Be(Visibility.Collapsed);
            dragged = stagePanel.Children.OfType<Button>().Single(b => Equals(b.DataContext, draggedPhase));
            var addButton = (Button)window.FindName("AddStageButton");
            var endPoint = addButton.TranslatePoint(new Point(addButton.ActualWidth / 2, addButton.ActualHeight / 2), stagePanel);
            Drag(dragged, endPoint, DragDrop.PreviewDropEvent); Pump();
            vm.PhaseLabels.Keys.Last().Should().Be(draggedPhase);
            vm.PhaseLabels.Keys.First().Should().Be(LineupPhase.后期);
            stagePanel.Children[^1].Should().BeSameAs(addButton);
            var order = vm.PhaseLabels.Keys.ToArray();
            Drag((Button)stagePanel.Children[0], endPoint, DragDrop.PreviewDragOverEvent).Effects.Should().Be(DragDropEffects.None);
            Drag((Button)stagePanel.Children[0], endPoint, DragDrop.PreviewDropEvent).Effects.Should().Be(DragDropEffects.None);
            vm.PhaseLabels.Keys.Should().Equal(order);
            marker.Visibility.Should().Be(Visibility.Collapsed);
            Capture(window, $"{theme}-{night}-reordered");
            File.WriteAllText(Path.Combine(TestOutput.Directory, $"stage-drag-{theme}-{night}-performance.json"), JsonSerializer.Serialize(new
                { HoverEvents = 1000, HoverEventMilliseconds = hoverTimer.Elapsed.TotalMilliseconds, Note = "Includes test event construction and assertions; hover only, no editor rebuild." }));
            Click("SaveButton"); Pump();
            window.Result.Should().BeNull();
            ((TextBlock)window.FindName("EditorHint")).Text.Should().Contain("冲突");
            var saveError = (TextBlock)window.FindName("SaveErrorText");
            saveError.Visibility.Should().Be(Visibility.Visible);
            saveError.Text.Should().Contain("冲突");
            saveError.TextWrapping.Should().Be(TextWrapping.Wrap);
            Capture(window, $"{theme}-{night}-save-conflict");
            var deleteButton = stagePanel.Children.OfType<Button>().Single(b => Equals(b.DataContext, draggedPhase));
            var deleteItem = deleteButton.ContextMenu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "删除此阶段"));
            foreach (string answer in new[] { "CancelButton", "OkButton" })
            {
                window.Dispatcher.BeginInvoke(() =>
                {
                    var dialog = window.OwnedWindows.OfType<ConfirmDialog>().Single();
                    ((Button)dialog.FindName(answer)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }, System.Windows.Threading.DispatcherPriority.Loaded);
                deleteItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                vm.PhaseLabels.ContainsKey(draggedPhase).Should().Be(answer == "CancelButton");
            }
            vm.ToLineup().SklandGuide!.Stages.Count.Should().Be(order.Length - 1);
            foreach (var phase in vm.PhaseLabels.Keys.Where(p => p != LineupPhase.后期))
                vm.UpdateStage(phase, vm.PhaseLabels[phase], null, null);
            Click("SaveButton"); Pump();
            window.Result.Should().NotBeNull();
            JsonSerializer.Serialize(imported.Lineup).Should().Be(before);
            File.WriteAllText(Path.Combine(TestOutput.Directory, $"skland-editor-{theme}-{night}-performance.json"), JsonSerializer.Serialize(new
                { FirstOpenMs = timer.Elapsed.TotalMilliseconds, SavedBytes = JsonSerializer.SerializeToUtf8Bytes(saved).Length, DuplicateWithRefreshAndFrameMs = duplicateTimer.Elapsed.TotalMilliseconds }));
        }
        finally { window.Close(); }
        var legacyTimes = new List<double>(); var guideTimes = new List<double>();
        for (int i = 0; i < 4; i++)
        foreach (bool full in new[] { false, true })
        {
            var source = full ? imported.Lineup : new Lineup { Name = imported.Lineup.Name, Slots = imported.Lineup.Slots, Strategies = imported.Lineup.Strategies };
            var watch = Stopwatch.StartNew();
            var probe = new LineupEditorWindow(new LineupEditorViewModel(source, operators, equipment,
                new CovenantCalculator(operators, covenants, map)), strategies) { Width = 760, Height = 680 };
            probe.Show(); Pump(); watch.Stop(); probe.Close();
            if (i > 0) (full ? guideTimes : legacyTimes).Add(watch.Elapsed.TotalMilliseconds);
        }
        File.WriteAllText(Path.Combine(TestOutput.Directory, $"skland-editor-{theme}-{night}-comparison.json"), JsonSerializer.Serialize(new
        { LegacyOpenMs = legacyTimes, FullGuideOpenMs = guideTimes, Slots = 8, ImportedStages = 3,
            Note = "Same build, alternating old lineup path and complete guide path after one warmup; includes 35ms render-frame wait." }));
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(35) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    private static void Capture(Window window, string name, ContextMenu? menu = null)
    {
        var output = Path.Combine(TestOutput.Directory, "skland-complete-editor"); Directory.CreateDirectory(output);
        window.UpdateLayout();
        var raw = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        raw.Render(window);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            drawing.DrawImage(raw, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            if (menu is not null)
            {
                var popup = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                popup.Render(menu);
                drawing.DrawImage(popup, new Rect(window.PointFromScreen(menu.PointToScreen(new Point())), menu.RenderSize));
            }
        }
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name + ".png")); png.Save(file);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "skland-guide-import.json"))) });
    }
}
