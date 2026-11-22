using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ASPAssistant.App;
using ASPAssistant.App.Views;
using ASPAssistant.App.Windows;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

/// <summary>规划 tab, 自动 page: candidate cards and the route with 抓位 / 升本 advice, on the real season data.</summary>
internal static class AutoPlanViewTests
{
    public static void Verify(GameShopTheme theme, bool night)
    {
        var data = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "TestData", "Regulation"));
        var operators = data.LoadOperatorsAsync().GetAwaiter().GetResult();
        var (covenants, _) = data.LoadCovenantsAsync().GetAwaiter().GetResult();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var bans = new BanViewModel();
        var vm = new LineupPlannerViewModel(new LineupPlanner(operators, covenants), operators, bans, EquipmentTestData.Rules)
            { DebounceMilliseconds = 0, CurrentTerminalLevel = 3 };
        vm.IsActive = true;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        vm.ShowAutoPageCommand.Execute(null); Complete(vm.PendingAutoPlan!);
        var candidatesMs = timer.Elapsed.TotalMilliseconds;
        var candidateCount = vm.AutoPlans.Count;
        vm.AutoPlans.Should().NotBeEmpty();
        var pending = vm.PendingAutoPlan;
        foreach (var width in new[] { 280, 400 })
            Render(vm, theme, night, width, $"{theme}-{night}-list-{width}", view =>
            {
                ((Button)view.FindName("AutoTabButton")).Content.Should().Be("自动推荐");
                ((Button)view.FindName("AutoTabButton")).Tag.Should().Be("Active");
                ((Button)view.FindName("GoalTabButton")).Tag.Should().NotBe("Active");
                ((TextBlock)view.FindName("StatusLine")).IsVisible.Should().BeFalse();
                var search = (TextBox)view.FindName("AutoLockSearchBox");
                search.Foreground.Should().BeSameAs(view.FindResource("TextPrimaryBrush"));
                search.Background.Should().BeSameAs(view.FindResource("CardBackgroundBrush"));
                search.Text = "鸿雪";
                vm.LockSearchText.Should().Be("鸿雪");
                var list = (ItemsControl)view.FindName("AutoPlansList");
                list.IsVisible.Should().BeTrue();
                var buttons = Descendants<Button>(list).Where(b => b.DataContext is AutoStyleChoice).ToList();
                buttons.Should().HaveCount(vm.AutoPlans.Count * 2);
                var replayChoices = buttons.Where(b => b.DataContext is AutoStyleChoice choice && choice.Plan.Source.RouteComparison is not null).ToList();
                replayChoices.Should().NotBeEmpty();
                replayChoices.Should().OnlyContain(b => b.ToolTip.ToString()!.Contains("目标 8 人"));
                buttons.Should().OnlyContain(b => b.ActualWidth >= 85);
                foreach (var button in buttons)
                {
                    var label = new TextBlock { Text = (string)button.Content, FontFamily = button.FontFamily,
                        FontSize = button.FontSize, FontWeight = button.FontWeight };
                    label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    label.DesiredSize.Width.Should().BeLessThanOrEqualTo(button.ActualWidth - button.Padding.Left - button.Padding.Right - 2);
                }
                var autoTab = (Button)view.FindName("AutoTabButton");
                var goalTab = (Button)view.FindName("GoalTabButton");
                ((Panel)autoTab.Parent).Children.Cast<Button>().Should().Equal(autoTab, goalTab);
                autoTab.ActualWidth.Should().BeApproximately(goalTab.ActualWidth, 1);
                autoTab.Tag.Should().Be("Active");
                goalTab.Tag.Should().NotBe("Active");
                ((Button)view.FindName("PlannerNavigationButton")).IsVisible.Should().BeFalse();
                foreach (var button in new[] { autoTab, goalTab })
                {
                    var bounds = button.TransformToAncestor(view).TransformBounds(new Rect(button.RenderSize));
                    bounds.Left.Should().BeGreaterThanOrEqualTo(0); bounds.Right.Should().BeLessThanOrEqualTo(view.ActualWidth + 1);
                }
            });
        vm.SetBossContext("假想敌：茧", 20);
        Render(vm, theme, night, 280, $"{theme}-{night}-boss-matchup", view =>
        {
            var note = Descendants<TextBlock>(view).First(t => t.DataContext is AutoPlanItem p
                && p.BossNote.Contains("局通关") && t.Text == p.Note);
            note.BringIntoView(); Pump();
            note.IsVisible.Should().BeTrue();
            note.TextWrapping.Should().Be(TextWrapping.Wrap);
            note.ActualWidth.Should().BeLessThanOrEqualTo(view.ActualWidth);
        });
        vm.SetBossContext("", null);
        var choice = vm.AutoPlans[0];
        var routeTimes = new List<double>();
        foreach (var style in choice.Styles)
        {
            vm.IsActive = true; timer.Restart(); Complete(vm.PlanAutoRouteAsync(style)); routeTimes.Add(timer.Elapsed.TotalMilliseconds);
            vm.RouteStages.Should().HaveCount(5);
            vm.RouteStages.Should().OnlyContain(s => s.HasAdvice);
            foreach (var width in new[] { 280, 400 })
                Render(vm, theme, night, width, $"{theme}-{night}-route-{style.Style}-{width}", view =>
                {
                    ((ItemsControl)view.FindName("AutoPlansList")).IsVisible.Should().BeFalse();
                    var heading = (TextBlock)view.FindName("AutoRouteHeading");
                    heading.IsVisible.Should().BeTrue(); heading.Text.Should().Be(vm.RouteTitle);
                    heading.TextTrimming.Should().Be(TextTrimming.None);
                    var stages = (ItemsControl)view.FindName("RouteStagesList");
                    stages.IsVisible.Should().BeTrue();
                    var current = vm.RouteStages.Single(s => s.IsCurrent);
                    Descendants<TextBlock>(stages).Where(t => t.IsVisible).Select(t => t.Text).Should().Contain(current.FocusText);
                });
        }
        var reserve = vm.RouteStages.FirstOrDefault(s => s.HasReserve);
        Lineup? archived = null; vm.SaveRequested += lineup => archived = lineup; vm.SaveRouteCommand.Execute(null);
        var restored = System.Text.Json.JsonSerializer.Deserialize<Lineup>(System.Text.Json.JsonSerializer.Serialize(archived))!;
        restored.AutoRoute!.Error.Should().BeEmpty();
        var reopened = new LineupPlannerViewModel(new LineupPlanner(operators, covenants), operators, new BanViewModel(), EquipmentTestData.Rules)
            { DebounceMilliseconds = 0, IsActive = true };
        reopened.OpenSavedAutoRoute(restored).Should().BeNull();
        reopened.PendingAutoPlan.Should().BeNull();
        reopened.TrackRouteCommand.CanExecute(null).Should().BeTrue();
        Render(reopened, theme, night, 280, $"{theme}-{night}-archive-reopened", view =>
        {
            Descendants<TextBlock>(view).Select(t => t.Text).Should().Contain(reopened.AutoRouteContext).And.Contain(reopened.AutoRouteNotice);
            reopened.RouteStages.Should().HaveCount(5);
        });
        if (reserve is not null)
        {
            vm.SelectRouteStepCommand.Execute(vm.RouteSteps.Single(s => ReferenceEquals(s.Stage, reserve)));
            Render(vm, theme, night, 280, $"{theme}-{night}-reserve", view =>
                Descendants<TextBlock>(view).Where(t => t.IsVisible).Select(t => t.Text).Should().Contain(reserve.Reserve[0].Name));
        }
        var mid = vm.RouteStages[2];
        vm.CurrentTerminalLevel = 5;
        vm.SelectRouteStepCommand.Execute(vm.RouteSteps.Single(s => ReferenceEquals(s.Stage, mid)));
        foreach (var (funds, cost, label) in new (int?, int?, string)[] { (10, 3, "ready"), (1, 3, "short"), (null, null, "unknown") })
        {
            vm.SetAutoRunState(new(5, new(7, funds, cost, false, null, true), new HashSet<string>()));
            foreach (var scale in new[] { 0.75, 1.5 })
                Render(vm, theme, night, 280, $"{theme}-{night}-five-{label}-{scale}", view =>
                {
                    var five = mid.AdviceLevels.Single(l => l.TerminalLevel == 5);
                    var texts = Descendants<TextBlock>(view).Where(t => t.IsVisible && ReferenceEquals(t.DataContext, five)).ToList();
                    texts.Select(t => t.Text).Should().Contain(five.PurchaseText).And.Contain(five.EconomyText);
                    var title = texts.Single(t => t.Text == five.Title);
                    for (DependencyObject? node = title; node is not null; node = VisualTreeHelper.GetParent(node))
                        if (node is ScrollViewer scroll)
                        {
                            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + title.TranslatePoint(new Point(), scroll).Y);
                            break;
                        }
                    Pump();
                    foreach (var text in texts)
                    {
                        var bounds = text.TransformToAncestor(view).TransformBounds(new Rect(text.RenderSize));
                        bounds.Left.Should().BeGreaterThanOrEqualTo(0);
                        bounds.Right.Should().BeLessThanOrEqualTo(view.ActualWidth + 1);
                    }
                }, scale, 600);
        }
        // Same candidate and route; only advice changes. This timing excludes WPF layout and PNG encoding.
        var routeOperators = mid.Operators;
        vm.IsActive = true;
        var refreshNotifications = 0;
        mid.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(RouteStageItem.Advice)) refreshNotifications++; };
        var allocated = GC.GetAllocatedBytesForCurrentThread(); timer.Restart();
        for (var i = 0; i < 200; i++)
            vm.SetAutoRunState(new(5, new(7, 10 + i % 2, 3, false, null, true), new HashSet<string>()));
        var refreshMs = timer.Elapsed.TotalMilliseconds;
        var refreshBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        refreshNotifications.Should().Be(200, "each changed state must actually refresh the visible advice");
        mid.Operators.Should().BeSameAs(routeOperators);
        vm.PendingAutoPlan.Should().BeSameAs(pending);
        var tracking = new TrackingViewModel { DetectedTerminalLevel = 5, IsTerminalLevelRestored = true };
        var pool = new DeployPoolState(); tracking.SetDeployPool(pool);
        var folder = Path.Combine(Path.GetTempPath(), "auto-run-binding-" + Guid.NewGuid().ToString("N"));
        var calculator = new CovenantCalculator(operators, covenants, new Dictionary<string, string>());
        var editorVm = new LineupEditorViewModel(restored, operators, [], calculator);
        var editor = new LineupEditorWindow(editorVm, []) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        try
        {
            editor.Show(); Pump();
            var guide = Descendants<Expander>(editor).Single(e => Equals(e.Header, "攻略讲解 · 可编辑"));
            guide.IsExpanded = true; guide.BringIntoView(); Pump();
            var notes = (TextBox)guide.Content;
            notes.Text.Should().BeEmpty();
            Save((FrameworkElement)editor.Content, $"{theme}-{night}-saved-stage");
            notes.Text = "我改写的成型攻略";
            ((Button)editor.FindName("SourcePageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            var sourceText = (TextBlock)editor.FindName("AutoRouteContextText");
            sourceText.IsVisible.Should().BeTrue();
            sourceText.Text.Should().Be(reopened.AutoRouteContext);
            Descendants<TextBlock>(editor).Should().Contain(t => t.IsVisible && t.Text == "来源：软件自动推荐");
            Save((FrameworkElement)editor.Content, $"{theme}-{night}-saved-source");
            var edited = System.Text.Json.JsonSerializer.Deserialize<Lineup>(System.Text.Json.JsonSerializer.Serialize(editorVm.ToLineup()))!;
            var editedVm = new LineupEditorViewModel(edited, operators, [], calculator);
            editedVm.CurrentStage.OperationIdea.Should().Be("我改写的成型攻略");
            editedVm.AutoRouteContext.Should().Be(sourceText.Text);
        }
        finally { editor.Close(); }
        var browse = new OperatorBrowseViewModel(); browse.LoadOperators(operators.ToList());
        var side = new SidePanelWindow(browse, new EquipmentBrowseViewModel(), tracking, new GameStateViewModel(), bans, new EnemyViewModel(),
            new LineupViewModel(new LineupStore(Path.Combine(folder, "lineups"), calculator.EquipmentRules), calculator),
            new LineupShareService(calculator.EquipmentRules, Path.Combine(folder, "share")), operators, [], [], new SettingsManager(folder),
            theme, new SidePanelPositionSettings(), covenants: covenants);
        try
        {
            var bound = side.PlannerVm; bound.DebounceMilliseconds = 0; bound.IsActive = true;
            Complete(bound.PendingAutoPlan!); Complete(bound.PlanAutoRouteAsync(bound.AutoPlans[0].Styles[0]));
            var boundMid = bound.RouteStages[2];
            tracking.Economy = new(7, 10, 3, false, null, true);
            boundMid.AdviceLevels[1].EconomyText.Should().Contain("待确认", "a restored terminal is not a current reading");
            tracking.IsTerminalLevelRestored = false;
            boundMid.AdviceLevels[1].EconomyText.Should().Contain("升级后剩 7");
            pool.Owned = operators.Select(o => o.Name).ToList(); tracking.SyncDeployPool();
            boundMid.AdviceLevels[1].KeyCards.Should().BeEmpty();
            tracking.StartNewRun();
            boundMid.AdviceLevels[1].EconomyText.Should().Contain("待确认");
            boundMid.AdviceLevels[1].KeyCards.Should().NotBeEmpty("new runs clear old ownership through the real window binding");
            Complete(side.LineupVm.AddOrUpdateAsync(restored));
            side.ShowActivated = false; side.ShowInTaskbar = false; side.Opacity = 0;
            side.Left = -20000; side.Top = -20000; side.Height = 700;
            side.ShowLineupsSegment(planner: false); side.Show(); Pump(); ThemeManager.Apply(theme, night);
            tracking.TrackLineup(restored);
            var deploy = ((TrackingView)side.FindName("TrackingView")).DeployPage;
            ((Button)deploy.FindName("MapGuideButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            var guideWindow = Application.Current.Windows.OfType<TrackingGuideWindow>().Single();
            var notesBox = (TextBox)guideWindow.FindName("Notes");
            guideWindow.ShowActivated.Should().BeFalse();
            side.IsEnabled.Should().BeTrue();
            var stageBefore = tracking.CurrentMapLineup!.SelectedStageId;
            var tabs = (StackPanel)guideWindow.FindName("Tabs");
            var preview = tabs.Children.OfType<Button>().First(b => !b.Content.ToString()!.Contains("●"));
            preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            tracking.CurrentMapLineup.SelectedStageId.Should().Be(stageBefore);
            notesBox.Text = "打完本回合再升本";
            side.ShowTrackingGuide(toggle: true);
            guideWindow.IsVisible.Should().BeFalse();
            var other = System.Text.Json.JsonSerializer.Deserialize<Lineup>(System.Text.Json.JsonSerializer.Serialize(restored))!;
            other.Id = "other-guide"; other.Name = "另一个追踪阵容";
            tracking.TrackLineup(other);
            side.ShowTrackingGuide(toggle: true); Pump();
            notesBox.Text.Should().Be("打完本回合再升本");
            ((TextBlock)guideWindow.FindName("Following")).Text.Should().Contain(other.Name);
            Save((FrameworkElement)guideWindow.Content, $"{theme}-{night}-tracking-guide-draft");
            ((Button)guideWindow.FindName("FollowButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            notesBox.Text.Should().BeEmpty();
            var otherDrafts = (Button)guideWindow.FindName("OtherDrafts");
            otherDrafts.IsVisible.Should().BeTrue();
            otherDrafts.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            var resume = (MenuItem)otherDrafts.ContextMenu.Items[0];
            resume.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); otherDrafts.ContextMenu.IsOpen = false;
            notesBox.Text.Should().Be("打完本回合再升本");
            var concurrentlyEdited = System.Text.Json.JsonSerializer.Deserialize<Lineup>(System.Text.Json.JsonSerializer.Serialize(restored))!;
            var editedStage = concurrentlyEdited.EditorStages!.Single(s => s.Name == preview.Content.ToString());
            editedStage.OperationIdea = "编辑器里的另一份讲解";
            concurrentlyEdited.EditorStages!.Single(s => s.IsFinal).OperationIdea = "独立阶段的编辑";
            Complete(side.LineupVm.AddOrUpdateAsync(concurrentlyEdited));
            Complete(guideWindow.SaveAsync(false));
            ((Button)guideWindow.FindName("UseSaved")).IsVisible.Should().BeTrue();
            side.LineupVm.Lineups.Single(l => l.Id == restored.Id).Should().BeSameAs(concurrentlyEdited);
            Complete(guideWindow.SaveAsync(false));
            var savedGuide = side.LineupVm.Lineups.Single(l => l.Id == restored.Id);
            savedGuide.EditorStages!.Count(s => s.OperationIdea == "打完本回合再升本").Should().Be(1);
            savedGuide.AutoRoute!.Matches(savedGuide).Should().BeTrue();
            savedGuide.EditorStages!.Single(s => s.IsFinal).OperationIdea.Should().Be("独立阶段的编辑");
            tracking.CurrentMapLineup.Id.Should().Be(other.Id);
            other.EditorStages!.Should().OnlyContain(s => s.OperationIdea == "");
            guideWindow.WindowState = WindowState.Minimized;
            side.ShowTrackingGuide(toggle: true); Pump();
            guideWindow.WindowState.Should().Be(WindowState.Normal);
            notesBox.Text = "临时阵容的成型攻略";
            ((TextBox)guideWindow.FindName("CopyName")).Text = "本局副本";
            Complete(guideWindow.SaveAsync(true));
            side.LineupVm.Lineups.Single(l => l.Name == "本局副本").EditorStages!.Single(s => s.IsFinal).OperationIdea.Should().Be("临时阵容的成型攻略");
            guideWindow.Close();
            var list = (LineupListView)side.FindName("LineupView");
            var open = Descendants<Button>(list).Single(b => Equals(b.Content, "查看自动路线")
                && b.DataContext is LineupCardItem card && card.Lineup.Id == restored.Id);
            var searchTask = bound.PendingAutoPlan;
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            bound.RouteTitle.Should().Contain("已保存路线");
            bound.PendingAutoPlan.Should().BeSameAs(searchTask, "the real list button only restores the saved route");
            bound.TrackRouteCommand.CanExecute(null).Should().BeTrue();
            var brokenRoot = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(restored))!;
            brokenRoot["autoRoute"]!["Version"] = 99; brokenRoot["id"] = "damaged-archive";
            var broken = System.Text.Json.JsonSerializer.Deserialize<Lineup>(brokenRoot.ToJsonString())!;
            Complete(side.LineupVm.AddOrUpdateAsync(broken));
            side.ShowLineupsSegment(planner: false); Pump();
            var brokenButton = Descendants<Button>(list).Single(b => Equals(b.Content, "查看自动路线")
                && b.DataContext is LineupCardItem card && card.Lineup.Id == broken.Id);
            brokenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            ((TextBlock)side.FindName("ToastText")).Text.Should().Contain("阵容仍可编辑和追踪");
            var toast = (Border)side.FindName("ToastBorder"); toast.BeginAnimation(UIElement.OpacityProperty, null); toast.Opacity = 1;
            var content = (FrameworkElement)side.Content;
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(side.Background, null, new Rect(content.RenderSize));
                context.DrawRectangle(new VisualBrush(content), null, new Rect(content.RenderSize));
            }
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var screenshot = File.Create(Path.Combine(TestOutput.Directory, "auto-plan", $"{theme}-{night}-archive-error.png"));
            encoder.Save(screenshot);
        }
        finally { side.Close(); }
        vm.CloseRouteCommand.Execute(null);
        Render(vm, theme, night, 400, $"{theme}-{night}-back", view => ((ItemsControl)view.FindName("AutoPlansList")).IsVisible.Should().BeTrue());
        vm.PendingAutoPlan.Should().BeSameAs(pending, "returning to the cached list does not recalculate candidates");
        foreach (var scale in new[] { 0.75, 1.5 })
            Render(vm, theme, night, 280, $"{theme}-{night}-scale-{scale}", view =>
            {
                var search = (TextBox)view.FindName("AutoLockSearchBox");
                search.Text = "鸿雪";
                search.IsVisible.Should().BeTrue();
            }, scale);
        vm.IsActive = true;
        bans.SetCovenantBans(covenants.Where(c => c.BondType == "SEASON").Select(c => c.Name));
        Complete(vm.PendingAutoPlan!);
        vm.AutoPlans.Should().BeEmpty();
        Render(vm, theme, night, 280, $"{theme}-{night}-empty", view =>
            ((TextBlock)view.FindName("AutoStatusTextBlock")).Text.Should().Contain("没有找到"));
        File.WriteAllText(Path.Combine(TestOutput.Directory, $"auto-{theme}-{night}-performance.json"),
            System.Text.Json.JsonSerializer.Serialize(new { Candidates = candidateCount, CandidatesMs = candidatesMs, RouteMs = routeTimes,
                AdviceRefreshCount = refreshNotifications, AdviceRefreshMs = refreshMs, AdviceRefreshBytes = refreshBytes, CandidateSearchesDuringRefresh = 0 }));
    }

    private static void Render(LineupPlannerViewModel vm, GameShopTheme theme, bool night, int width, string name, Action<LineupPlannerView> verify, double scale = 1, int height = 900)
    {
        ThemeManager.Apply(theme, night);
        var view = new LineupPlannerView { DataContext = vm };
        var surface = new Grid();
        surface.SetResourceReference(Panel.BackgroundProperty, "PanelBackgroundBrush");
        view.LayoutTransform = new ScaleTransform(scale, scale);
        surface.Children.Add(view);
        var host = new Window
        {
            Content = surface, ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, Width = width * scale, Height = height * scale
        };
        try
        {
            host.Show();
            Pump();
            verify(view);
            if (name.Contains("-five-") || name.Contains("-archive") || name.Contains("-boss-matchup"))
            {
                var contrasts = new List<double>();
                foreach (var text in Descendants<TextBlock>(view).Where(t => t.IsVisible && t.Text.Length > 0
                    && (t.DataContext is AutoPlanLevelAdvice || t.Text == vm.AutoRouteContext || t.Text == vm.AutoRouteNotice ||
                        t.DataContext is AutoPlanItem p && p.BossNote.Length > 0 && t.Text == p.Note)))
                {
                    var backgrounds = new Stack<Color>();
                    for (DependencyObject? node = text; node is not null && node != host; node = VisualTreeHelper.GetParent(node))
                    {
                        var brush = node switch { Border border => border.Background, Panel panel => panel.Background,
                            Control control => control.Background, _ => null };
                        if (brush is SolidColorBrush solid) backgrounds.Push(solid.Color);
                    }
                    var background = Colors.White;
                    foreach (var color in backgrounds)
                    {
                        var alpha = color.A / 255.0;
                        background = Color.FromRgb((byte)(color.R * alpha + background.R * (1 - alpha)),
                            (byte)(color.G * alpha + background.G * (1 - alpha)), (byte)(color.B * alpha + background.B * (1 - alpha)));
                    }
                    var foreground = ((SolidColorBrush)text.Foreground).Color;
                    var a = Luminance(foreground); var b = Luminance(background);
                    var contrast = (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
                    contrast.Should().BeGreaterThanOrEqualTo(4.5, $"{name}: {text.Text}");
                    contrasts.Add(contrast);
                }
                contrasts.Should().NotBeEmpty();
                File.WriteAllText(Path.Combine(TestOutput.Directory, $"contrast-advice-{name}.json"),
                    System.Text.Json.JsonSerializer.Serialize(new { Minimum = contrasts.Min() }));
            }
            Save(surface, name);
            if (name.Contains("-five-"))
            {
                var funds = Descendants<TextBlock>(view).Single(t => t.DataContext is AutoPlanLevelAdvice { TerminalLevel: 5 } five
                    && t.Text == five.EconomyText);
                funds.BringIntoView(); Pump();
                Save(surface, name + "-funds");
            }
        }
        finally
        {
            host.Close();
            view.DataContext = null;
        }
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte value) => value / 255.0 <= 0.04045 ? value / 255.0 / 12.92 : Math.Pow((value / 255.0 + 0.055) / 1.055, 2.4);
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    /// <summary>Pumps the dispatcher until the planning task has finished.</summary>
    private static void Complete(Task task)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
        task.IsCompleted.Should().BeTrue("planning should finish");
        task.GetAwaiter().GetResult();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) yield return result;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Save(FrameworkElement content, string name)
    {
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.Combine(TestOutput.Directory, "auto-plan");
        Directory.CreateDirectory(directory);
        using var file = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(file);
    }
}
