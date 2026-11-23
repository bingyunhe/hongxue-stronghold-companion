using System.Diagnostics;
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
using ASPAssistant.Tests.Services;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

internal static class SklandImportViewTests
{
    internal static void VerifyPublishPreview(bool night)
    {
        ThemeManager.Apply(GameShopTheme.Closure, night);
        var owner = new Window { Width = 400, Height = 730, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
        owner.Show();
        var session = new SklandSessionWindow(owner, Path.Combine(Path.GetTempPath(), "skland-preview", "Browser"));
        var lineup = new Lineup { Name = "精准开局转叙拉古", Strategies = [new() { StrategyId = "primary" }, new() { StrategyId = "alternate" }],
            Slots = [new() { OperatorName = "迷迭香", IsElite = true }], EarlySlots = [new() { OperatorName = "角峰" }],
            EditorStages = [new() { Id = "early", Name = "前期", StartRound = 1, EndRound = 4, OperationIdea = "先保血量，预留转职装备。" },
                new() { Id = "final", Name = "成型阵容", IsFinal = true, RecommendedEquipments = ["人事部文档"] }] };
        var window = new SklandPublishWindow(lineup,
            [new() { Id = "primary", Name = "众志合一" }, new() { Id = "alternate", Name = "变形者集群" }], session)
            { Owner = owner, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show();
            window.Submit.IsEnabled.Should().BeFalse();
            window.Status.Text.Should().Contain("登录");
            window.RenderStages([["谢拉格"], ["精准", "叙拉古"]]);
            window.UpdateLayout();
            window.StageList.Items.Count.Should().Be(2);
            Descendants<TextBlock>(window.StageList).Should().Contain(t => t.Text.Contains("精准、叙拉古"));
            window.StrategyText.Text.Should().Contain("备选策略 · 变形者集群");
            Save(window, "publish-preview-" + night);
        }
        finally { window.Close(); owner.Close(); }
    }

    public static void Verify()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var directory = Path.Combine(Path.GetTempPath(), "skland-import-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Operator[] operators = [new() { Name = "甲", Tier = 2 }, new() { Name = "乙", Tier = 3 },
            .. Enumerable.Range(1, 20).Select(i => new Operator { Name = "候选" + i, Tier = (i - 1) % 6 + 1 })];
        var calculator = new CovenantCalculator(operators, [], new Dictionary<string, string>());
        var lineups = new LineupViewModel(new LineupStore(Path.Combine(directory, "lineups"), calculator.EquipmentRules), calculator);
        var tracking = new TrackingViewModel();
        var operatorVm = new OperatorBrowseViewModel();
        operatorVm.LoadOperators(operators.ToList());
        var panel = new SidePanelWindow(operatorVm, new EquipmentBrowseViewModel(), tracking, new GameStateViewModel(),
            new BanViewModel(), new EnemyViewModel(), lineups,
            new LineupShareService(calculator.EquipmentRules, Path.Combine(directory, "share")), operators, [], [],
            new SettingsManager(directory), GameShopTheme.Terminal, new SidePanelPositionSettings { FreeMovement = true })
        { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            panel.Show();
            var count = 0;
            foreach (var theme in Enum.GetValues<GameShopTheme>())
            foreach (var night in new[] { false, true })
            {
                ThemeManager.Apply(theme, night);
                var draft = new Lineup
                {
                    Name = "官方攻略草稿", SourceUrl = Services.SklandGuideImporterTests.Url,
                    Slots = [new() { OperatorName = "甲", IsElite = true }, new() { OperatorName = "乙" }]
                };
                const string notice = "已读取成型阵容 2 人、策略 0 项。推荐装备 20 件未绑定到干员，回合讲解 2 段未导入；请查看来源攻略。技能与模组沿用本地模式数据。检查并保存后才加入我的阵容，不自动追踪。";
                var cancel = panel.OpenEditorAsync(draft, notice);
                var editor = panel.OwnedWindows.OfType<LineupEditorWindow>().Single();
                editor.Width = 760;
                editor.Height = 680;
                editor.UpdateLayout();
                ((Button)editor.FindName("SourcePageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                editor.UpdateLayout();
                var url = (TextBox)editor.FindName("SourceUrlBox");
                ((FrameworkElement)editor.FindName("SourcePanel")).Visibility.Should().Be(Visibility.Visible);
                url.Text.Should().Be(draft.SourceUrl);
                url.IsReadOnly.Should().BeTrue();
                ((TextBlock)editor.FindName("ImportNoticeText")).Text.Should().Be(notice);
                url.ActualWidth.Should().BeGreaterThan(100);
                var bounds = url.TransformToAncestor((Visual)editor.Content).TransformBounds(new Rect(url.RenderSize));
                bounds.Right.Should().BeLessThanOrEqualTo(editor.ActualWidth);
                lineups.Lineups.Should().HaveCount(count, "preview must not write the draft");
                Save(editor, $"{theme}-{night}");
                editor.Close();
                Complete(cancel);
                lineups.Lineups.Should().HaveCount(count, "cancel must not save the imported lineup");

                var save = panel.OpenEditorAsync(draft, notice);
                editor = panel.OwnedWindows.OfType<LineupEditorWindow>().Single();
                ((Button)editor.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Complete(save);
                count++;
                lineups.Lineups.Should().HaveCount(count);
                var saved = lineups.Lineups.Single(x => x.Id == draft.Id);
                saved.SourceUrl.Should().Be(draft.SourceUrl);
                saved.SourceContentHash.Should().Be(LineupShareService.ContentFingerprint(saved));
                saved.Slots[0].IsElite.Should().BeTrue();
                tracking.TrackedOperators.Should().BeEmpty();
                tracking.TrackedEquipment.Should().BeEmpty();
            }
            Directory.GetFiles(Path.Combine(directory, "lineups"), "*.json")
                .Count(path => !Path.GetFileName(path).StartsWith('.')).Should().BeGreaterThanOrEqualTo(6);
            var candidates = new Lineup
            {
                Name = "20 人候选", SourceUrl = Services.SklandGuideImporterTests.Url,
                Slots = operators.Skip(2).Select(op => new LineupSlot { OperatorName = op.Name }).ToList()
            };
            var select = panel.OpenEditorAsync(candidates, "官方列出20名候选，已全部保留；请移除至15人以内再保存。");
            var chooser = panel.OwnedWindows.OfType<LineupEditorWindow>().Single();
            var host = (ItemsControl)chooser.FindName("SlotsHost");
            host.Items.Count.Should().Be(20);
            ((Button)chooser.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            select.IsCompleted.Should().BeFalse();
            lineups.Lineups.Should().HaveCount(6, "too many candidates must not be saved");
            ((TextBlock)chooser.FindName("EditorHint")).Text.Should().Contain("15");
            for (var i = 0; i < 5; i++)
            {
                var card = (FrameworkElement)host.Items[0];
                card.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "移出阵容"))
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            host.Items.Count.Should().Be(15);
            ((Button)chooser.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Complete(select);
            lineups.Lineups.Should().HaveCount(7);
            lineups.Lineups.Single(x => x.Id == candidates.Id).Slots.Should().HaveCount(15);
            candidates.Slots.Should().HaveCount(20);
            tracking.TrackedOperators.Should().BeEmpty();

            var grouped = new Lineup
            {
                Name = "20 人分阶段攻略", SourceUrl = Services.SklandGuideImporterTests.Url,
                EarlySlots = operators.Skip(2).Where(op => op.Tier <= 2).Select(op => new LineupSlot { OperatorName = op.Name }).ToList(),
                MidSlots = operators.Skip(2).Where(op => op.Tier is 3 or 4).Select(op => new LineupSlot { OperatorName = op.Name }).ToList(),
                Slots = operators.Skip(2).Where(op => op.Tier >= 5).Select(op => new LineupSlot { OperatorName = op.Name, IsElite = true }).ToList()
            };
            const string phaseNotice = "官方列出 20 名候选，已按本数分组并全部保留：1–2本前期 8 人、3–4本中期 6 人、5–6本后期 6 人。检查并保存后才加入我的阵容，不自动追踪。";
            foreach (var theme in Enum.GetValues<GameShopTheme>())
            foreach (var night in new[] { false, true })
            {
                ThemeManager.Apply(theme, night);
                var preview = panel.OpenEditorAsync(grouped, phaseNotice);
                var phaseEditor = panel.OwnedWindows.OfType<LineupEditorWindow>().Single();
                phaseEditor.Width = 760;
                phaseEditor.Height = 680;
                var tabs = (Panel)phaseEditor.FindName("PhaseTabs");
                foreach (var (phase, people) in new[] { (LineupPhase.前期, 8), (LineupPhase.中期, 6), (LineupPhase.后期, 6) })
                {
                    var tab = tabs.Children.OfType<Button>().Single(button => Equals(button.DataContext, phase));
                    string.Concat(((TextBlock)tab.Content).Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text))
                        .Should().Be($"{(phase == LineupPhase.后期 ? "成型阵容" : phase.ToString())} {people}");
                    tab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    ((TextBlock)phaseEditor.FindName("EditorHint")).Text.Should().Contain(phase == LineupPhase.后期 ? "成型阵容" : phase.ToString());
                    Save(phaseEditor, $"phases-{theme}-{night}-{phase}");
                }
                phaseEditor.Close();
                Complete(preview);
                lineups.Lineups.Should().HaveCount(7);
            }
            var saveGrouped = panel.OpenEditorAsync(grouped, phaseNotice);
            var groupedEditor = panel.OwnedWindows.OfType<LineupEditorWindow>().Single();
            ((Button)groupedEditor.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Complete(saveGrouped);
            var savedGrouped = lineups.Lineups.Single(lineup => lineup.Id == grouped.Id);
            savedGrouped.EarlySlots.Should().HaveCount(8);
            savedGrouped.MidSlots.Should().HaveCount(6);
            savedGrouped.Slots.Should().HaveCount(6).And.OnlyContain(slot => slot.IsElite);
            tracking.TrackedOperators.Should().BeEmpty();
            tracking.TrackLineup(savedGrouped);
            tracking.TrackedOperators.Should().HaveCount(20);
            tracking.TrackedOperators.Count(entry => entry.Role == TrackingRole.Final).Should().Be(6);
        }
        finally
        {
            foreach (Window child in panel.OwnedWindows.Cast<Window>().ToArray()) child.Close();
            panel.Close();
            Directory.Delete(directory, true);
        }
    }

    private sealed class CatalogueHandler(string index, string resource) : System.Net.Http.HttpMessageHandler
    {
        public int Calls;
        public bool Offline;
        public Uri? LastIndex;
        public TaskCompletionSource? HoldSearch;
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (Offline) throw new System.Net.Http.HttpRequestException("离线测试");
            var uri = request.RequestUri!;
            uri.Host.Should().Be("zonai.skland.com");
            var json = resource;
            if (!uri.AbsolutePath.EndsWith("/resource"))
            {
                uri.AbsolutePath.Should().EndWith("/index");
                uri.Query.Should().Contain("source=COMMUNITY").And.Contain("sortType=1").And.Contain("seasonId=act3autochess");
                LastIndex = uri;
                var title = System.Web.HttpUtility.ParseQueryString(uri.Query)["titleKeyword"];
                if (title == "旧查询" && HoldSearch != null) await HoldSearch.Task;
                if (title == null) json = index;
                else
                {
                    var reply = System.Text.Json.Nodes.JsonNode.Parse(index)!;
                    reply["data"]!["list"]![0]!["guide"]!["title"] = title;
                    json = reply.ToJsonString();
                }
            }
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new System.Net.Http.StringContent(json) };
        }
    }

    internal static void VerifyBrowse()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var data = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "data"));
        var getOps = data.LoadOperatorsAsync();
        var getStrategies = data.LoadStrategiesAsync();
        var getCovenants = data.LoadCovenantsAsync();
        var getEquipment = data.LoadEquipmentAsync();
        Complete(Task.WhenAll(getOps, getStrategies, getCovenants, getEquipment));
        var operators = getOps.Result.ToList();
        var strategies = getStrategies.Result.ToList();
        var calculator = new CovenantCalculator(operators, getCovenants.Result.Covenants, getCovenants.Result.JobChangeEquipmentToCovenant);
        var dir = Path.Combine(Path.GetTempPath(), "skland-browse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var vm = new LineupViewModel(new LineupStore(Path.Combine(dir, "lineups"), calculator.EquipmentRules), calculator);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "skland-guide-import.json")))!;
        var payload = json["data"]!;
        var post = payload["guide"]!;
        post["rts"] = new System.Text.Json.Nodes.JsonObject { ["collected"] = "0", ["liked"] = "215" };
        post["guide"]!["applicableScope"] = string.Join("\n", Enumerable.Repeat("奥术与精准可用时，按当前回合选择备选干员。", 12));
        var recommended = post["guide"]!["strategyIds"]!.AsArray();
        recommended.Add("band_amiya");
        var resource = payload["seasonResourceMap"]!["act3autochess"]!["versionResourceMap"]!["V3_1"]!;
        resource["strategies"]!["band_amiya"] = new System.Text.Json.Nodes.JsonObject { ["name"] = "众志合一" };
        payload["list"] = new System.Text.Json.Nodes.JsonArray(post.DeepClone());
        payload["hasMore"] = false;
        var filterResource = System.Text.Json.JsonSerializer.Serialize(new { code = 0, data = new {
            versionInfo = new { seasonId = "act3autochess" },
            strategies = strategies.Select(s => new { strategyId = s.Id, name = s.Name, initiatorName = s.Initiator }),
            bonds = new[] { ("iberiaShip", "伊比利亚", 1), ("docShip", "聚合", 2), ("indomShip", "不屈", 2),
                ("preciShip", "精准", 2), ("suntShip", "绝技", 2) }.Select(v => new {bondId = v.Item1, name = v.Item2, bondType = v.Item3})
        }});
        using var handler = new CatalogueHandler(json.ToJsonString(), filterResource);
        using var http = new System.Net.Http.HttpClient(handler);
        var ops = new OperatorBrowseViewModel();
        ops.LoadOperators(operators);
        var panel = new SidePanelWindow(ops, new EquipmentBrowseViewModel(), new TrackingViewModel(), new GameStateViewModel { CurrentRound = 9 },
            new BanViewModel(), new EnemyViewModel(), vm, new LineupShareService(calculator.EquipmentRules, dir, http), operators, getEquipment.Result.Equipment, strategies,
            new SettingsManager(dir), GameShopTheme.Closure, new SidePanelPositionSettings { FreeMovement = true }, covenants: getCovenants.Result.Covenants)
        { Width = 450, Height = 900, Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            panel.Show();
            panel.ShowLineupsSegment(false);
            handler.Calls.Should().Be(0);
            var view = (App.Views.SklandBrowseView)panel.FindName("SklandView");
            var segment = (ListBox)panel.FindName("LineupsSegment");
            segment.SelectedIndex = 2;
            var cards = (ItemsControl)view.FindName("Cards");
            var watch = Stopwatch.StartNew();
            while (cards.Items.Count == 0 && watch.Elapsed < TimeSpan.FromSeconds(5))
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            cards.Items.Count.Should().Be(1, ((TextBlock)view.FindName("Status")).Text);
            var publicCalls = handler.Calls;
            view.SavedTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            view.Status.Text.Should().Contain("请点击「登录」查看我的收藏");
            view.OwnTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            view.Status.Text.Should().Contain("请点击「登录」查看我发布的攻略");
            handler.Calls.Should().Be(publicCalls);
            panel.OwnedWindows.OfType<SklandSessionWindow>().Should().BeEmpty();
            view.PublicTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Complete(view.ShowAsync());
            var login = new SklandSessionWindow(panel, Path.Combine(dir, "Browser")) { ShowActivated = false };
            foreach (var (scale, night) in new[] { (1.0, false), (1.25, true) })
            {
                ThemeManager.Apply(GameShopTheme.Closure, night);
                panel.Appearance.UiScale = scale;
                login.Show();
                login.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                WindowUiScale.Refresh(panel.Appearance);
                login.UpdateLayout();
                WindowUiScale.ScaleOf(login).Should().Be(scale);
                var done = Descendants<Button>(login).Single(b => Equals(b.Content, "完成登录"));
                done.IsVisible.Should().BeTrue();
                var root = (FrameworkElement)login.Content;
                var bounds = done.TransformToAncestor(root).TransformBounds(new Rect(done.RenderSize));
                bounds.IsEmpty.Should().BeFalse();
                bounds.Width.Should().BeGreaterThan(0);
                bounds.Height.Should().BeGreaterThan(0);
                new Rect(root.RenderSize).Contains(bounds).Should().BeTrue();
                SaveVisual((FrameworkElement)done.Parent, "login-toolbar-" + night);
                login.Hide();
                login.IsVisible.Should().BeFalse();
            }
            panel.Appearance.UiScale = 1;
            WindowUiScale.Refresh(panel.Appearance);
            void WaitFor(Func<bool> condition)
            {
                var deadline = Stopwatch.StartNew();
                while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(6))
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                condition().Should().BeTrue(view.Status.Text);
            }
            view.Search.Text = "旧查询";
            handler.HoldSearch = new();
            WaitFor(() => System.Web.HttpUtility.ParseQueryString(handler.LastIndex!.Query)["titleKeyword"] == "旧查询");
            view.Search.Text = "首页之外的标题";
            handler.HoldSearch.SetResult();
            WaitFor(() => cards.Items.OfType<SklandBrowseGuide>().Any(g => g.Title == "首页之外的标题"));
            view.Search.Clear();
            WaitFor(() => cards.Items.OfType<SklandBrowseGuide>().Any(g => g.Title != "首页之外的标题"));
            var search = view.LocalSearch;
            search.Text = "不存在的名字";
            cards.Items.Count.Should().Be(0);
            search.Text = "折桠";
            cards.Items.Count.Should().Be(1);
            var guide = (SklandBrowseGuide)cards.Items[0];
            guide.Matches("", "伊比利亚", ["聚合", "不屈"], "众志合一").Should().BeTrue();
            guide.Matches("", "伊比利亚", ["不屈", "协防干员"], null).Should().BeFalse();
            guide.Primary!.Id.Should().Be("band_bonus");
            guide.Alternatives.Single().Name.Should().Be("众志合一");
            guide.Collected.Should().Be("0");
            guide.Published.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1794375752).ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss"));
            view.FilterButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            void SelectChip(WrapPanel group, string text)
            {
                var chip = group.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>()
                    .Single(c => Equals(c.Content, text) || (c.DataContext as SklandFilterOption)?.Name == text);
                chip.IsChecked = chip.IsChecked != true;
                chip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            SelectChip(view.Core, "伊比利亚");
            SelectChip(view.Extra, "聚合");
            SelectChip(view.Extra, "不屈");
            view.StrategyBody.Visibility.Should().Be(Visibility.Collapsed);
            view.StrategyToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            int calls = handler.Calls;
            view.Strategy.Children.Count.Should().Be(strategies.Count + 1);
            view.StrategySearch.Text = "阿米娅";
            view.Strategy.Children.OfType<FrameworkElement>().Where(c => c.Visibility == Visibility.Visible).Should().ContainSingle();
            SelectChip(view.Strategy, "众志合一");
            view.StrategySearch.Text = "不存在的发起人";
            view.NoStrategy.Visibility.Should().Be(Visibility.Visible);
            view.ChosenStrategy.Content.Should().Be("已选：众志合一 ×");
            view.CollapseStrategies.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            view.StrategyBody.Visibility.Should().Be(Visibility.Collapsed);
            handler.Calls.Should().Be(calls);
            var combined = new SklandGuideQuery("", "iberiaShip", ["docShip","indomShip"], "band_amiya");
            WaitFor(() => combined.MatchesRequest(handler.LastIndex!) && view.Refresh.IsEnabled);
            view.StrategySearch.Clear();
            cards.Items.Count.Should().Be(1);
            view.Open(guide);
            ((SklandBrowseStage)view.Stages.SelectedItem).Start.Should().Be(1, "round nine does not match 不屈");
            view.Back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SelectChip(view.Extra, "全部");
            view.FilterButton.Content.Should().Be("筛选 · 2");
            search.Text.Should().Be("折桠");
            view.ClearFilters.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            view.FilterButton.Content.Should().Be("筛选");
            search.Text.Should().Be("折桠");
            view.FilterButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            handler.Offline = true;
            ((Button)view.FindName("Refresh")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((TextBlock)view.FindName("Status")).Text.Should().Contain("加载失败");
            cards.Items.Count.Should().Be(1);
            handler.Offline = false;
            view.Refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            watch.Restart();
            while (!view.Refresh.IsEnabled && watch.Elapsed < TimeSpan.FromSeconds(5))
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            view.Status.Text.Should().Contain("已加载");
            guide = (SklandBrowseGuide)cards.Items[0];
            foreach (var night in new[] { false, true })
            {
                ThemeManager.Apply(GameShopTheme.Closure, night);
                var sidebarScale = (ScaleTransform)panel.FindName("SidebarScaleTransform");
                panel.Appearance.UiScale = sidebarScale.ScaleX = sidebarScale.ScaleY = night ? 1.25 : 1;
                panel.Left = night ? SystemParameters.WorkArea.Left : SystemParameters.WorkArea.Right - panel.ActualWidth;
                panel.Top = SystemParameters.WorkArea.Top;
                panel.UpdateLayout();
                ToolTip Hover(FrameworkElement owner, string sample)
                {
                    var opening = (ToolTipEventArgs)Activator.CreateInstance(typeof(ToolTipEventArgs),
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new object[] { true }, null)!;
                    owner.RaiseEvent(opening);
                    opening.Handled.Should().BeFalse();
                    var tip = (ToolTip)owner.ToolTip;
                    tip.PlacementTarget = owner;
                    tip.IsOpen = true;
                    panel.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    tip.UpdateLayout();
                    tip.Content.Should().NotBeNull();
                    tip.Placement.Should().Be(System.Windows.Controls.Primitives.PlacementMode.Custom);
                    var left = tip.PointToScreen(new Point()).X;
                    var right = tip.PointToScreen(new Point(tip.ActualWidth, 0)).X;
                    var gap = night ? left - panel.PointToScreen(new Point(panel.ActualWidth, 0)).X
                        : panel.PointToScreen(new Point()).X - right;
                    gap.Should().BeApproximately(8 * VisualTreeHelper.GetDpi(panel).DpiScaleX, 2,
                        $"the {owner.Tag} tooltip should sit next to the sidebar at scale {sidebarScale.ScaleX}");
                    if (sample.Length > 0) SaveVisual(tip, $"hover-{sample}-{night}");
                    return tip;
                }
                var primary = Descendants<Image>(cards).First(i => Equals(i.Tag, "Strategy"));
                panel.Appearance.UiScale = sidebarScale.ScaleX = sidebarScale.ScaleY = night ? 1 : 1.25;
                panel.UpdateLayout();
                Hover(primary, "").IsOpen = false;
                panel.Appearance.UiScale = sidebarScale.ScaleX = sidebarScale.ScaleY = night ? 1.25 : 1;
                panel.UpdateLayout();
                var hovering = Hover(primary, "strategy");
                hovering.DataContext.Should().BeSameAs(strategies.Single(s => s.Id == guide.Primary!.Id));
                hovering.IsOpen = false;
                var portrait = Descendants<Grid>(cards).First(g => Equals(g.Tag, "Operator"));
                Hover(portrait, "operator").DataContext.Should().BeSameAs(operators.Single(o => o.Name == ((SklandBrowsePortrait)portrait.DataContext).Name));
                SavePlacement(panel, hovering, $"operator-docked-{night}");
                hovering.IsOpen = false;
                Save(panel, "catalogue-" + night);
                view.FilterButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var precision = view.Extra.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().Single(b => Equals(b.Content, "精准"));
                ToolTipService.GetInitialShowDelay(precision).Should().Be(250);
                ToolTipService.GetBetweenShowDelay(precision).Should().Be(800);
                ToolTipService.GetBetweenShowDelay(view.Strategy.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().Skip(1).First()).Should().Be(0);
                view.Extra.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().First().ToolTip.Should().BeNull();
                view.Extra.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().Should().NotContain(b => Equals(b.Content, "绝技"));
                var special = Descendants<Border>(cards).First(b => Equals(b.Tag, "Covenant") && Equals(b.DataContext, "绝技"));
                Hover(special, "elite-count").DataContext.Should().BeSameAs(panel.PlannerVm.CovenantDetails.Single(c => c.Name == "绝技"));
                Descendants<TextBlock>(hovering).Should().Contain(t => t.IsVisible && t.Text.Contains("在场5名精锐"));
                panel.PlannerVm.AllCovenants.Should().NotContain(c => c.Name == "绝技");
                hovering.IsOpen = false;
                Hover(precision, "").DataContext.Should().BeSameAs(panel.PlannerVm.CovenantDetails.Single(c => c.Name == "精准"));
                var covenant = (PlannerCovenantItem)hovering.DataContext;
                covenant.IsRunStatusUnknown.Should().BeTrue();
                panel.BanVm.SetCovenantBans([], new HashSet<string> { "精准" });
                covenant.BanNote.Should().Be("本局未出现");
                covenant.ShowBannedBadge.Should().BeFalse();
                panel.BanVm.SetCovenantBans(["精准"]);
                covenant.ShowBannedBadge.Should().BeTrue();
                panel.BanVm.SetCovenantBans([]);
                hovering.IsOpen = false;
                SelectChip(view.Extra, "精准");
                SelectChip(view.Extra, "聚合");
                view.StrategyToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                view.StrategySearch.Text = "众志合一";
                var strategyFilter = view.Strategy.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().Single(b => (b.DataContext as SklandFilterOption)?.Name == "众志合一");
                strategyFilter.BringIntoView();
                WaitFor(() => new SklandGuideQuery("", null, ["preciShip","docShip"], null).MatchesRequest(handler.LastIndex!) && view.Refresh.IsEnabled);
                panel.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                var filterModel = strategies.Single(s => s.Id == "band_amiya");
                string description = filterModel.Description;
                filterModel.Description = string.Join("\n", Enumerable.Repeat(description, 20));
                Hover(strategyFilter, "").DataContext.Should().BeSameAs(filterModel);
                var longScroll = Descendants<ScrollViewer>(hovering).Single();
                longScroll.ScrollToBottom();
                hovering.UpdateLayout();
                longScroll.VerticalOffset.Should().BeGreaterThan(0);
                filterModel.Description = description;
                Hover(strategyFilter, "");
                Descendants<ScrollViewer>(hovering).Single().VerticalOffset.Should().Be(0);
                hovering.IsOpen = false;
                SelectChip(view.Strategy, "众志合一");
                view.CollapseStrategies.BringIntoView();
                panel.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                SaveVisual((FrameworkElement)VisualTreeHelper.GetParent((DependencyObject)panel.Content), "catalogue-filters-" + night);
                view.CollapseStrategies.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                view.StrategySearch.Clear();
                view.ClearFilters.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                view.FilterButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitFor(() => SklandGuideQuery.Empty.MatchesRequest(handler.LastIndex!) && view.Refresh.IsEnabled);
                panel.UpdateLayout();
                guide = (SklandBrowseGuide)cards.Items[0];
                var cardButtons = Descendants<Button>(cards).Where(b => ReferenceEquals(b.DataContext, guide)).ToList();
                cardButtons.Should().Contain(b => Equals(b.Tag, "collected"));
                cardButtons.Should().Contain(b => Equals(b.Tag, "liked"));
                cardButtons.Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == guide.Title)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                panel.UpdateLayout();
                view.AlternatePanel.Visibility.Should().Be(Visibility.Visible);
                view.ScopeToggle.Visibility.Should().Be(Visibility.Visible);
                view.ScopeToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                double.IsPositiveInfinity(view.ScopeText.MaxHeight).Should().BeTrue();
                view.ScopeToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var stages = (ListBox)view.FindName("Stages");
                ((SklandBrowseStage)stages.SelectedItem).Start.Should().Be(8);
                stages.SelectedIndex = 0;
                ((SklandBrowseStage)stages.SelectedItem).Equipment.Should().Contain("盟约之币");
                stages.SelectedIndex = 2;
                ((SklandBrowseStage)stages.SelectedItem).Equipment.Length.Should().Be(20);
                ((SklandBrowseStage)stages.SelectedItem).Bonds.Should().Contain("精准");
                panel.UpdateLayout();
                var alternate = Descendants<StackPanel>(view.AlternatePanel).First(p => Equals(p.Tag, "Strategy"));
                Hover(alternate, "").DataContext.Should().BeSameAs(strategies.Single(s => s.Id == "band_amiya"));
                hovering.IsOpen = false;
                var bond = Descendants<Border>(view.StagePanel).First(b => Equals(b.Tag, "Covenant") && Equals(b.DataContext, "精准"));
                Hover(bond, "covenant").DataContext.Should().BeSameAs(covenant);
                hovering.IsOpen = false;
                var gear = Descendants<StackPanel>(view.StagePanel).First(p => Equals(p.Tag, "Equipment") && Equals(p.DataContext, "盟约之币"));
                Hover(gear, "equipment").Tag.Should().Be("此装备会销毁，导入时跳过。");
                hovering.DataContext.Should().BeOfType<Equipment>();
                hovering.IsOpen = false;
                gear.DataContext = "新赛季装备";
                Hover(gear, "").DataContext.Should().Be("新赛季装备");
                Descendants<TextBlock>(hovering).Where(t => t.IsVisible).Should().ContainSingle(t => t.Text == "新赛季装备");
                gear.ClearValue(FrameworkElement.DataContextProperty);
                stages.SelectedIndex = 0;
                hovering.IsOpen.Should().BeFalse();
                stages.SelectedIndex = 2;
                Save(panel, "catalogue-stage-" + night);
                view.Back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            view.Open(guide);
            ((Button)view.FindName("Import")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var editor = panel.OwnedWindows.OfType<LineupEditorWindow>().Single();
            ((Button)editor.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            watch.Restart();
            while (vm.Lineups.Count == 0 && watch.Elapsed < TimeSpan.FromSeconds(5))
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            vm.Lineups.Single().SklandGuide!.Stages.Should().HaveCount(3);
            var savedStrategies = vm.Lineups.Single().Strategies;
            savedStrategies.Select(s => s.StrategyId).Should().Equal("band_bonus", "band_amiya");
            savedStrategies[0].Tags.Should().Equal(StrategyTag.阵容核心);
            savedStrategies[1].Tags.Should().BeEmpty();
            guide.Id.Should().Be(vm.Lineups.Single().SklandGuide!.GuideId);
        }
        finally
        {
            foreach (Window child in panel.OwnedWindows.Cast<Window>().ToArray()) child.Close();
            panel.Close();
            Directory.Delete(dir, true);
        }
    }

    internal static void VerifyLocalNameSearch()
    {
        SearchMatch.LoadFile(Path.Combine(AppContext.BaseDirectory, "data", "search_keys.json"));
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "skland-guide-import.json")))!;
        var payload = json["data"]!;
        payload["guide"]!["rts"] = new System.Text.Json.Nodes.JsonObject { ["collected"] = "0", ["liked"] = "0" };
        payload["list"] = new System.Text.Json.Nodes.JsonArray(payload["guide"]!.DeepClone());
        payload["hasMore"] = false;
        const string options = """{"code":0,"data":{"versionInfo":{"seasonId":"act3autochess"},"bonds":[],"strategies":[{"strategyId":"band_amiya","name":"众志合一","initiatorName":"阿米娅"},{"strategyId":"band_bonus","name":"量体裁衣","initiatorName":"但书"}]}}""";
        using var handler = new CatalogueHandler(json.ToJsonString(), options);
        using var client = new System.Net.Http.HttpClient(handler);
        var view = new App.Views.SklandBrowseView();
        view.Configure(new SklandGuideCatalogue(client), () => 1, _ => "", _ => Task.CompletedTask, (_, _) => null);
        var surface = new Border { Child = view };
        surface.SetResourceReference(Border.BackgroundProperty, "PanelBackgroundBrush");
        var host = new Window { Content = surface, Width = 430, Height = 760, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            host.Show();
            Complete(view.ShowAsync());
            view.FilterButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            view.StrategyToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            view.StrategySearch.Text = "AMY";
            view.LocalSearch.Text = "ZY";
            view.Cards.Items.Count.Should().Be(1);
            view.Strategy.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>()
                .Where(b => b.Visibility == Visibility.Visible).Should().ContainSingle().Which.Content.Should().Be("众志合一 · 阿米娅");
            handler.Calls.Should().Be(2);
            foreach (var (scale, night) in new[] { (1.0, false), (1.25, true) })
            {
                ThemeManager.Apply(GameShopTheme.Closure, night);
                view.LayoutTransform = new ScaleTransform(scale, scale);
                host.UpdateLayout();
                foreach (var box in new[] {view.Search, view.LocalSearch, view.StrategySearch})
                {
                    box.ActualHeight.Should().Be(32);
                    box.FontSize.Should().Be(12);
                    box.Padding.Should().Be(new Thickness(9, 0, 9, 0));
                }
                view.Filters.ScrollToTop();
                host.UpdateLayout();
                SaveVisual(surface, "local-search-author-operator-" + night);
                view.Filters.ScrollToBottom();
                host.UpdateLayout();
                SaveVisual(surface, "local-search-inputs-" + night);
            }
            var clear = (Button)view.StrategySearch.Template.FindName("ClearTextButton", view.StrategySearch);
            clear.IsVisible.Should().BeTrue();
            ((System.Windows.Input.RoutedCommand)clear.Command).Execute(clear.CommandParameter, clear.CommandTarget);
            view.StrategySearch.Text.Should().BeEmpty();
        }
        finally { host.Close(); }

        Operator[] names = [new() { Name = "铃兰", Tier = 1 }, new() { Name = "老鲤", Tier = 1 }];
        var picker = new OperatorPickerWindow(names, "铃兰", new HashSet<string>(), reviewCandidates: ["铃兰", "老鲤"])
            { Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            picker.Show();
            picker.SearchBox.Text = "ll";
            picker.UpdateLayout();
            picker.OperatorsList.Items.Count.Should().Be(2);
            picker.SearchBox.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(picker), 0, System.Windows.Input.Key.Enter) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            picker.Result.Should().BeNull();
            picker.IsVisible.Should().BeTrue();
            picker.SearchBox.Text = "老鲤";
            picker.SearchBox.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(picker), 0, System.Windows.Input.Key.Enter) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            picker.Result.Should().Be("老鲤");
        }
        finally { picker.Close(); }
    }

    private static void Complete(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
        task.IsCompleted.Should().BeTrue("the editor should finish saving or cancelling");
        task.GetAwaiter().GetResult();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Save(Window window, string name)
        => SaveVisual((FrameworkElement)window.Content, name);

    private static void SaveVisual(FrameworkElement content, string name)
    {
        content.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(content);
        SaveImage(image, name);
    }

    private static void SavePlacement(Window window, ToolTip tip, string name)
    {
        static Rect Bounds(FrameworkElement element) => new(element.PointToScreen(new Point()),
            element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
        var sidebar = Bounds(window);
        var popup = Bounds(tip);
        var bounds = Rect.Union(sidebar, popup);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-bounds.Left, -bounds.Top));
            var surface = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
            dc.DrawRectangle(new VisualBrush(surface)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(surface.RenderSize),
                Stretch = Stretch.Fill
            }, null, sidebar);
            dc.DrawRectangle(new VisualBrush(tip), null, popup);
        }
        var image = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
        image.Render(drawing);
        SaveImage(image, name);
    }

    private static void SaveImage(BitmapSource image, string name)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        var folder = Path.Combine(TestOutput.Directory, "skland-import-previews");
        Directory.CreateDirectory(folder);
        using var stream = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(stream);
    }
}
