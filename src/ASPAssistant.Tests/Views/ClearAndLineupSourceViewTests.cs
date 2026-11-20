// Added for ASP Assistant Arknights on 2026-11-13; see NOTICE.
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ASPAssistant.App;
using ASPAssistant.App.Controls;
using ASPAssistant.App.Views;
using ASPAssistant.App.Windows;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Interop;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

internal static class ClearAndLineupSourceViewTests
{
    public static void VerifyCodex()
    {
        var folder = Path.Combine(Path.GetTempPath(), "codex-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        Operator[] operators = [new() { Name = "甲", Tier = 1 }, new() { Name = "乙", Tier = 2 }];
        Equipment[] equipment = [new() { Name = "装备甲", Tier = 1 }, new() { Name = "装备乙", Tier = 2 }];
        var operatorVm = new OperatorBrowseViewModel();
        operatorVm.LoadOperators(operators.ToList());
        var equipmentVm = new EquipmentBrowseViewModel();
        equipmentVm.LoadEquipment(equipment.ToList(), [], [], new Dictionary<string, string>());
        var calculator = new CovenantCalculator(operators, [], new Dictionary<string, string>());
        var tracking = new TrackingViewModel();
        var bans = new BanViewModel();
        operatorVm.SetBanChecker(bans.IsBanned);
        var panel = new SidePanelWindow(operatorVm, equipmentVm, tracking,
            new GameStateViewModel(), bans, new EnemyViewModel(),
            new LineupViewModel(new LineupStore(Path.Combine(folder, "lineups"), calculator.EquipmentRules), calculator),
            new LineupShareService(calculator.EquipmentRules, Path.Combine(folder, "share")), operators, equipment, [],
            new SettingsManager(folder), GameShopTheme.Terminal, new SidePanelPositionSettings { FreeMovement = true })
        { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, Height = 900 };
        try
        {
            panel.Show();
            Pump();
            var tabs = (TabControl)panel.FindName("MainTabs");
            var codex = (TabItem)panel.FindName("CodexTab");
            var segment = (ListBox)panel.FindName("CodexSegment");
            var operatorView = (OperatorBrowseView)panel.FindName("OperatorView");
            var equipmentView = (EquipmentBrowseView)panel.FindName("EquipmentView");
            tabs.Items.OfType<TabItem>().Select(t => t.Header).Should().Equal("图鉴", "追踪", "阵容", "对局信息");
            segment.SelectedIndex.Should().Be(0);
            operatorView.Visibility.Should().Be(Visibility.Visible);
            equipmentView.Visibility.Should().Be(Visibility.Collapsed);
            ((TextBox)operatorView.FindName("SearchBox")).Text = "甲";
            operatorVm.TierChips.Single(c => c.Value == "1").IsSelected = true;
            tracking.AddTracking("甲", TrackingType.Operator);
            tracking.AddTracking("装备甲", TrackingType.Equipment);
            bans.ToggleBan("乙");
            foreach (var theme in Enum.GetValues<GameShopTheme>())
            foreach (var night in new[] { false, true })
            foreach (var width in new[] { 280, 360 })
            {
                ThemeManager.Apply(theme, night);
                panel.Width = width;
                foreach (var index in new[] { 1, 0 })
                {
                    tabs.SelectedItem = codex;
                    segment.SelectedIndex = index;
                    if (index == 1)
                    {
                        ((TextBox)equipmentView.FindName("SearchBox")).Text = "装备甲";
                        equipmentVm.TierChips.Single(c => c.Value == "1").IsSelected = true;
                    }
                    Await(Task.Delay(240));
                    panel.UpdateLayout();
                    Pump();
                    tabs.SelectedItem.Should().BeSameAs(codex, "segment selection stays within 图鉴");
                    operatorView.Visibility.Should().Be(index == 0 ? Visibility.Visible : Visibility.Collapsed);
                    equipmentView.Visibility.Should().Be(index == 1 ? Visibility.Visible : Visibility.Collapsed);
                    var selected = (ListBoxItem)segment.SelectedItem;
                    selected.ActualWidth.Should().BeGreaterThan(0);
                    selected.ActualWidth.Should().BeApproximately(((ListBoxItem)segment.Items[1 - index]).ActualWidth, 1);
                    Save((FrameworkElement)codex.Content, $"codex-{theme}-{night}-{width}-{index}");
                    foreach (var tab in tabs.Items.OfType<TabItem>().Skip(1))
                    {
                        tabs.SelectedItem = tab;
                        Pump();
                        ((FrameworkElement)tab.Content).IsVisible.Should().BeTrue();
                    }
                    tabs.SelectedItem = codex;
                    Pump();
                    segment.SelectedIndex.Should().Be(index);
                    operatorVm.SearchText.Should().Be("甲");
                    operatorVm.FilteredOperators.Select(o => o.Name).Should().Equal("甲");
                    equipmentVm.SearchText.Should().Be("装备甲");
                    equipmentVm.FilteredEquipment.Select(e => e.Name).Should().Equal("装备甲");
                    tracking.IsTracked("甲").Should().BeTrue();
                    tracking.IsTracked("装备甲").Should().BeTrue();
                    bans.IsBanned("乙").Should().BeTrue();
                }
            }
        }
        finally
        {
            User32.UnregisterHotKey(new System.Windows.Interop.WindowInteropHelper(panel).Handle, 0x4153);
            panel.Close();
            Directory.Delete(folder, true);
        }
    }

    public static void VerifyReferenceCodex(bool night)
    {
        string folder = Directory.CreateTempSubdirectory("codex-reference-").FullName;
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        var store = new JsonDataStore(data);
        var operators = Await(store.LoadOperatorsAsync());
        operators.AddRange(Await(store.LoadCustomOperatorsAsync()));
        var (equipment, _) = Await(store.LoadEquipmentAsync());
        var strategies = Await(store.LoadStrategiesAsync());
        var bosses = new BossDataService();
        Await(bosses.LoadAsync(data));
        var calculator = new CovenantCalculator(operators, [], new Dictionary<string, string>());
        var panel = new SidePanelWindow(new(), new(), new(), new(), new(), new(),
            new LineupViewModel(new LineupStore(Path.Combine(folder, "lineups"), calculator.EquipmentRules), calculator),
            new LineupShareService(calculator.EquipmentRules, Path.Combine(folder, "share")), operators, equipment,
            strategies, new SettingsManager(folder), GameShopTheme.Closure, new() { FreeMovement = true },
            appearance: new SidebarAppearance { Theme = GameShopTheme.Closure, Night = new() { [GameShopTheme.Closure] = night } })
        { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, Width = 280, Height = 640 };
        try
        {
            panel.SetCodexBosses(bosses.Bosses);
            panel.Show();
            Pump();
            var segment = (ListBox)panel.FindName("CodexSegment");
            var tab = (TabItem)panel.FindName("CodexTab");
            ((TabControl)panel.FindName("MainTabs")).SelectedItem = tab;
            ThemeManager.Apply(GameShopTheme.Closure, night);
            var surface = (FrameworkElement)((TabControl)panel.FindName("MainTabs")).Parent;
            panel.Left = SystemParameters.WorkArea.Right - panel.ActualWidth - 20;
            panel.Top = SystemParameters.WorkArea.Top + 15;
            void SavePinned(Border card, double scale, string kind)
            {
                // Popup is a separate HWND. Compose the actual two WPF visuals at their screen positions.
                var dpi = VisualTreeHelper.GetDpi(panel);
                Rect Bounds(FrameworkElement element)
                {
                    var a = element.PointToScreen(new Point());
                    var b = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
                    return new Rect(new Point(a.X / dpi.DpiScaleX, a.Y / dpi.DpiScaleY),
                        new Point(b.X / dpi.DpiScaleX, b.Y / dpi.DpiScaleY));
                }
                var surfaceRect = Bounds(surface);
                var cardRect = Bounds(card);
                cardRect.Right.Should().BeLessThan(surfaceRect.Left);
                cardRect.Width.Should().BeApproximately(card.Width * scale, 2);
                var bounds = Rect.Union(surfaceRect, cardRect);
                var drawing = new DrawingVisual();
                using (var dc = drawing.RenderOpen())
                {
                    dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
                    dc.DrawRectangle((Brush)panel.FindResource("PanelBackgroundBrush"), null, bounds);
                    dc.DrawRectangle(new VisualBrush(surface), null, surfaceRect);
                    dc.DrawRectangle(new VisualBrush(card), null, cardRect);
                }
                var shot = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
                shot.Render(drawing);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(shot));
                using (var file = File.Create(Path.Combine(TestOutput.Directory, $"{kind}-pinned-{night}-{scale}.png"))) png.Save(file);
            }
            var counts = new[] { operators.Count, equipment.Count, strategies.Count, bosses.Bosses.Count, CodexBounty.Load().Length };
            var times = new List<object>();
            foreach (double scale in new[] { 1d, 1.25 })
            {
                var page = (FrameworkElement)tab.Content;
                page.LayoutTransform = new ScaleTransform(scale, scale);
                for (int index = 2; index < 5; index++)
                {
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    segment.SelectedIndex = index;
                    panel.UpdateLayout();
                    var content = (ReferenceCatalogueView)((ContentControl)panel.FindName("ReferenceHost")).Content;
                    var list = (ListBox)content.FindName("Entries");
                    list.Items.Count.Should().Be(counts[index]);
                    times.Add(new { index, scale, ms = timer.Elapsed.TotalMilliseconds });
                    if (index == 2)
                    {
                        var popup = (System.Windows.Controls.Primitives.Popup)content.FindName("DetailsPopup");
                        var card = (Border)content.FindName("DetailsCard");
                        var pinned = (TextBlock)content.FindName("PinnedLabel");
                        Await(Task.Delay(50));
                        while (Descendants<Image>(list).Count(i => i.Source is not null) != strategies.Count &&
                               timer.Elapsed < TimeSpan.FromSeconds(8)) Await(Task.Delay(25));
                        Descendants<Image>(list).Count(i => i.Source is not null).Should().Be(strategies.Count);
                        var portraits = Descendants<Button>(list).ToArray();
                        portraits.Length.Should().Be(strategies.Count);
                        times.Add(new { index, scale, portraitsReadyMs = timer.Elapsed.TotalMilliseconds });
                        Descendants<TextBlock>(list).Select(t => t.Text).Should().Equal("泛用型策略", "盟约型策略");
                        var groups = list.Items.Groups!.Cast<CollectionViewGroup>().ToArray();
                        groups.Select(g => g.ItemCount).Should().Equal(44, 17);
                        portraits[44].TranslatePoint(new Point(), list).X.Should().BeApproximately(portraits[0].TranslatePoint(new Point(), list).X, 1);
                        portraits[60].TranslatePoint(new Point(), list).X.Should().BeApproximately(portraits[1].TranslatePoint(new Point(), list).X, 1);
                        var first = portraits[0].TranslatePoint(new Point(), list);
                        var fifth = portraits[4].TranslatePoint(new Point(), list);
                        first.Y.Should().BeApproximately(fifth.Y, 1);
                        portraits[5].TranslatePoint(new Point(), list).Y.Should().BeGreaterThan(first.Y);
                        portraits[1].ActualHeight.Should().BeApproximately(portraits[1].ActualWidth, 1);
                        void Hover(int i, RoutedEvent ev) => portraits[i].RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = ev });
                        void Click(int i) => portraits[i].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Hover(1, Mouse.MouseEnterEvent);
                        Pump();
                        popup.IsOpen.Should().BeTrue();
                        pinned.Visibility.Should().Be(Visibility.Collapsed);
                        Descendants<TextBlock>(card).Should().Contain(t => t.Text.Contains("歌利亚"));
                        card.PointToScreen(new Point(card.ActualWidth, 0)).X.Should().BeLessThan(panel.PointToScreen(new Point()).X);
                        Hover(1, Mouse.MouseLeaveEvent);
                        popup.IsOpen.Should().BeFalse();
                        Click(1);
                        Hover(1, Mouse.MouseLeaveEvent);
                        Hover(0, Mouse.MouseEnterEvent);
                        Pump();
                        pinned.IsVisible.Should().BeTrue();
                        Descendants<TextBlock>(card).Should().Contain(t => t.Text == "坚不可摧");
                        Await(Task.Delay(250));
                        SavePinned(card, scale, "strategy");
                        portraits[44].BringIntoView();
                        Pump();
                        Click(44);
                        Descendants<TextBlock>(card).Should().Contain(t => t.Text == strategies[44].Name);
                        Save(surface, $"strategy-bond-{night}-{scale}");
                        portraits[0].BringIntoView();
                        Pump();
                        Click(0);
                        Descendants<TextBlock>(card).Should().Contain(t => t.Text == strategies[0].Name);
                        pinned.Visibility.Should().Be(Visibility.Visible);
                        Click(0);
                        pinned.Visibility.Should().Be(Visibility.Collapsed);
                        popup.IsOpen.Should().BeTrue();
                        Hover(0, Mouse.MouseLeaveEvent);
                        popup.IsOpen.Should().BeFalse();
                        Hover(1, Mouse.MouseEnterEvent);
                        Click(1);
                        pinned.Visibility.Should().Be(Visibility.Visible);
                        segment.SelectedIndex = 3;
                        Pump();
                        popup.IsOpen.Should().BeFalse();
                        segment.SelectedIndex = 2;
                        Pump();
                        popup.IsOpen.Should().BeFalse();
                        Click(1);
                        ((TabControl)panel.FindName("MainTabs")).SelectedIndex = 1;
                        Pump();
                        popup.IsOpen.Should().BeFalse();
                        ((TabControl)panel.FindName("MainTabs")).SelectedItem = tab;
                        Pump();
                    }
                    var input = (TextBox)content.FindName("SearchBox");
                    input.Text = index switch { 2 => "阿米娅", 3 => "假想敌：胄", _ => "磨砻" };
                    list.Items.Count.Should().BeGreaterThan(0).And.BeLessThan(counts[index]);
                    if (index == 2)
                    {
                        list.Items.Groups!.Cast<CollectionViewGroup>().Select(g => g.Name).Should().Equal("泛用型策略");
                        input.Text = "娜仁图亚";
                        list.Items.Groups!.Cast<CollectionViewGroup>().Select(g => g.Name).Should().Equal("盟约型策略");
                        input.Clear();
                        var chips = Descendants<ToggleButton>((Grid)content.FindName("StrategyFilters")).ToDictionary(b => (string)b.Content);
                        void Choose(string label)
                        {
                            var button = chips[label];
                            button.IsChecked = button.IsChecked != true;
                            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            panel.UpdateLayout();
                        }
                        string[] Titles() => Descendants<Button>(list).Select(System.Windows.Automation.AutomationProperties.GetName).ToArray();
                        Choose("点击");
                        Choose("回合限制");
                        Titles().Should().Equal("人才锁定", "精巧设计", "广交豪杰");
                        Await(Task.Delay(250));
                        Save(surface, $"strategy-filters-{night}-{scale}");
                        input.Text = "精巧";
                        panel.UpdateLayout();
                        Titles().Should().Equal("精巧设计");
                        Choose("被动");
                        Titles().Should().Equal("精巧设计");
                        chips["点击"].IsChecked.Should().BeFalse();
                        input.Clear();
                        Choose("拖拽");
                        Choose("每场限制");
                        Titles().Should().Equal("幻画为真", "至纯凝结");
                        Choose("回合解锁");
                        Titles().Should().Equal("条文修正", "不稳定要素");
                        Choose("回合解锁");
                        Choose("拖拽");
                        list.Items.Count.Should().Be(counts[index]);
                    }
                    if (index == 4)
                    {
                        list.Items.Count.Should().Be(2);
                        var filters = (FrameworkElement)content.FindName("BountyFilters");
                        var chips = Descendants<ToggleButton>(filters).ToDictionary(b => (string)b.Content);
                        void Choose(string label)
                        {
                            var chip = chips[label];
                            chip.IsChecked = chip.IsChecked != true;
                            chip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            panel.UpdateLayout();
                        }
                        input.Clear();
                        Choose("1");
                        Choose("1场");
                        Choose("击杀");
                        var all = CodexBounty.Load();
                        list.Items.Count.Should().Be(all.Count(b => b.Amount == 1 && b.Rounds == 1 && b.Condition == "击杀"));
                        input.Text = "法术大师A2";
                        panel.UpdateLayout();
                        list.Items.Count.Should().Be(1);
                        var row = Descendants<Button>(list).Single();
                        Descendants<Image>(row).Single().Source.Should().NotBeNull();
                        row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
                        Pump();
                        var card = (Border)content.FindName("DetailsCard");
                        var variants = (ItemsControl)content.FindName("BountyVariants");
                        var names = variants.Items.Cast<CodexBounty>().Select(b => b.Tag);
                        names.Should().Equal("悬赏·飞行I", "普通悬赏", "多轮战术特训");
                        Descendants<TextBlock>(variants).Count(t => t.IsVisible && t.Text == "当前条目").Should().Be(1);
                        Descendants<TextBlock>(card).Should().Contain(t => t.IsVisible && t.Text.Contains("远程法术攻击"));
                        Await(Task.Delay(250));
                        SavePinned(card, scale, "bounty-hover");
                        row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        var popup = (Popup)content.FindName("DetailsPopup");
                        row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
                        popup.IsOpen.Should().BeTrue();
                        segment.SelectedIndex = 3;
                        Pump();
                        popup.IsOpen.Should().BeFalse();
                        segment.SelectedIndex = 4;
                        Pump();
                        Choose("2");
                        Choose("每场");
                        Choose("完美");
                        list.Items.Count.Should().Be(1);
                        input.Clear();
                        Choose("2场");
                        list.Items.Count.Should().Be(0);
                        Choose("击杀");
                        list.Items.Count.Should().Be(all.Count(b => b.Amount == 2 && b.Rounds == 2 && b.Condition == "击杀"));
                        Choose("2");
                        Choose("2场");
                        Choose("击杀");
                        list.Items.Count.Should().Be(counts[index]);
                    }
                    Pump();
                    panel.UpdateLayout();
                    Await(Task.Delay(250));
                    panel.UpdateLayout();
                    if (index == 3)
                    {
                        var popup = (System.Windows.Controls.Primitives.Popup)content.FindName("DetailsPopup");
                        var card = (Border)content.FindName("DetailsCard");
                        var pinned = (TextBlock)content.FindName("PinnedLabel");
                        var scroll = (ScrollViewer)content.FindName("DetailsScroll");
                        var row = Descendants<Button>(list).First(b => b.DataContext == list.Items[0]);
                        void Hover(Button button, RoutedEvent ev) => button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = ev });
                        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Descendants<TextBlock>(row).Select(t => t.Text).Should().Equal("假想敌：胄");
                        panel.EnemyVm.CurrentMode = null;
                        var hoverTimer = System.Diagnostics.Stopwatch.StartNew();
                        Hover(row, Mouse.MouseEnterEvent);
                        Pump();
                        times.Add(new { index, scale, hoverMs = hoverTimer.Elapsed.TotalMilliseconds });
                        popup.IsOpen.Should().BeTrue();
                        var healthHost = (ContentControl)content.FindName("BossHealthHost");
                        var health = (BossHealthViewModel)healthHost.Content;
                        var boss = bosses.Lookup("假想敌：胄")!;
                        var avatar = (Image)content.FindName("BossPortrait");
                        avatar.Source.Should().BeSameAs(Descendants<Image>(row).Single().Source);
                        avatar.Source.Should().NotBeNull();
                        var name = Descendants<TextBlock>(card).Single(t => t.Text == boss.Name);
                        avatar.TranslatePoint(new Point(avatar.ActualWidth, 0), card).X.Should().BeLessThan(name.TranslatePoint(new Point(), card).X);
                        pinned.Text.Should().Be("单击固定");
                        pinned.IsVisible.Should().BeTrue();
                        ((TextBlock)content.FindName("DetailDescription")).Visibility.Should().Be(Visibility.Collapsed);
                        SavePinned(card, scale, "boss-hover");
                        health.HasCurrentBossHp.Should().BeFalse();
                        health.OtherBossHpExpanded.Should().BeTrue();
                        health.BossHpMulti.Should().Equal(boss.Hp.Where(h => h.Group == "multi").Select(BossHpCell.From));
                        health.OtherBossHpSingle.Should().Equal(boss.Hp.Where(h => h.Group == "single").Select(BossHpCell.From));
                        ((TextBlock)content.FindName("BossMetadata")).IsVisible.Should().BeTrue();
                        health.OtherBossHpExpanded = false;
                        Hover(row, Mouse.MouseLeaveEvent);
                        popup.IsOpen.Should().BeFalse();
                        Click(row);
                        Hover(row, Mouse.MouseLeaveEvent);
                        Pump();
                        pinned.IsVisible.Should().BeTrue();
                        pinned.Text.Should().Be("已固定");
                        health.OtherBossHpExpanded.Should().BeTrue();
                        var flow = (StackPanel)scroll.Content;
                        var abilities = Descendants<TextBlock>(flow).Single(t => t.Text.StartsWith("· "));
                        var healthTop = healthHost.TranslatePoint(new Point(), flow).Y;
                        healthTop.Should().BeGreaterThanOrEqualTo(abilities.TranslatePoint(new Point(0, abilities.ActualHeight), flow).Y);
                        scroll.ScrollToVerticalOffset(healthTop - 59);
                        Pump();
                        SavePinned(card, scale, "boss-all-levels");
                        panel.EnemyVm.RegulationLevel = 13;
                        Pump();
                        var expected = BossHpCell.From(boss.Hp.Single(h => h.Name == "规程等级13"));
                        healthHost.Content.Should().BeSameAs(health);
                        health.CurrentBossHp.Should().Be(expected with { IsCurrent = true });
                        var display = Descendants<BossHealthView>(card).Single();
                        var current = (Border)display.FindName("CurrentBossHpCard");
                        var expander = (Expander)display.FindName("OtherBossHpExpander");
                        Descendants<TextBlock>(current).Where(t => t.IsVisible).Select(t => t.Text)
                            .Should().Equal("当前 · 规程等级13", expected.Hp, $"隐藏首领 {expected.HiddenHp}");
                        expander.IsExpanded.Should().BeFalse();
                        current.IsVisible.Should().BeTrue();
                        scroll.ScrollToBottom();
                        Pump();
                        SavePinned(card, scale, "boss-current-level");
                        expander.IsExpanded = true;
                        Pump();
                        var multi = (ItemsControl)display.FindName("MultiBossHpItems");
                        var cells = Descendants<TextBlock>(multi).Where(t => t.IsVisible).ToList();
                        cells.Should().Contain(t => t.Text.StartsWith("隐藏 "));
                        foreach (var cell in cells)
                        {
                            cell.Measure(new Size(cell.ActualWidth, double.PositiveInfinity));
                            cell.DesiredSize.Height.Should().BeLessThanOrEqualTo(cell.ActualHeight + 1);
                        }
                        foreach (var unnumbered in new[] { "标准模拟", "极速模拟" })
                        {
                            panel.EnemyVm.CurrentMode = unnumbered;
                            Pump();
                            current.IsVisible.Should().BeFalse();
                            expander.IsExpanded.Should().BeTrue();
                            expander.Header.Should().Be("全部难度血量");
                            health.OtherBossHpSingle.Should().HaveCount(21);
                        }
                        panel.EnemyVm.RegulationLevel = 13;
                        scroll.ScrollToBottom();
                        Pump();
                        scroll.VerticalOffset.Should().BeGreaterThan(0);
                        var plain = bosses.Bosses.First(b => b.Hp.Count > 0 && b.Hp.All(h => h.HiddenHp is null));
                        input.Text = plain.Name;
                        panel.UpdateLayout();
                        var other = Descendants<Button>(list).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == plain.Name);
                        var previous = card.DataContext;
                        Hover(other, Mouse.MouseEnterEvent);
                        card.DataContext.Should().BeSameAs(previous);
                        Click(other);
                        Pump();
                        card.DataContext.Should().BeSameAs(other.DataContext);
                        var plainHealth = (BossHealthViewModel)healthHost.Content;
                        plainHealth.CurrentBossHp!.Hp.Should().Be(BossHpCell.From(plain.Hp.Single(h => h.Name == "规程等级13")).Hp);
                        Descendants<TextBlock>(card).Where(t => t.IsVisible).Should().NotContain(t => t.Text.Contains("隐藏首领"));
                        scroll.VerticalOffset.Should().Be(0);
                        Click(other);
                        pinned.IsVisible.Should().BeTrue();
                        pinned.Text.Should().Be("单击固定");
                        Hover(other, Mouse.MouseLeaveEvent);
                        popup.IsOpen.Should().BeFalse();
                        Click(other);
                        segment.SelectedIndex = 4;
                        Pump();
                        popup.IsOpen.Should().BeFalse();
                        segment.SelectedIndex = 3;
                        Pump();
                        popup.IsOpen.Should().BeFalse();
                    }
                    foreach (ListBoxItem item in segment.Items)
                    {
                        item.Tag.Should().Be(counts[segment.Items.IndexOf(item)]);
                        foreach (var label in Descendants<TextBlock>(item))
                        {
                            var edges = label.TransformToAncestor(item).TransformBounds(new Rect(label.RenderSize));
                            edges.Left.Should().BeGreaterThanOrEqualTo(-1);
                            edges.Right.Should().BeLessThanOrEqualTo(item.ActualWidth + 1);
                        }
                    }
                    Save(surface, $"reference-{night}-{index}-{scale}");
                    input.Text = "不存在的图鉴条目";
                    list.Items.Count.Should().Be(0);
                    if (index == 4)
                    {
                        panel.UpdateLayout();
                        Save(surface, $"reference-empty-{night}-{scale}");
                    }
                    input.Clear();
                    panel.UpdateLayout();
                    timer.Restart();
                    segment.SelectedIndex = 1;
                    panel.UpdateLayout();
                    times.Add(new { index = 1, scale, ms = timer.Elapsed.TotalMilliseconds });
                    timer.Restart();
                    segment.SelectedIndex = index;
                    panel.UpdateLayout();
                    times.Add(new { index, scale, cached = true, ms = timer.Elapsed.TotalMilliseconds });
                    ((ContentControl)panel.FindName("ReferenceHost")).Content.Should().BeSameAs(content);
                    list.Items.Count.Should().Be(counts[index]);
                }
            }
            double Light(string key)
            {
                var color = ((SolidColorBrush)panel.FindResource(key)).Color;
                color.A.Should().Be(byte.MaxValue);
                static double Linear(byte value) => value <= 10 ? value / 3294.6 : Math.Pow((value / 255d + .055) / 1.055, 2.4);
                return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
            }
            double Contrast(string fg, string bg)
            {
                double a = Light(fg), b = Light(bg);
                return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
            }
            var ratios = new[] { Contrast("TextPrimaryBrush", "CardBackgroundBrush"), Contrast("TextSecondaryBrush", "CardBackgroundBrush"),
                Contrast("OnAccentBrush", "AccentSolidBrush"), Contrast("TextSecondaryBrush", "PanelBackgroundBrush"),
                Contrast("AccentBrush", "CardBackgroundBrush"), Contrast("DangerBrush", "CardBackgroundBrush"),
                Contrast("DangerBrush", "SectionBackgroundBrush") };
            ratios.Min().Should().BeGreaterThanOrEqualTo(4.5);
            File.WriteAllText(Path.Combine(TestOutput.Directory, $"contrast-codex-{night}.json"),
                System.Text.Json.JsonSerializer.Serialize(new { Minimum = ratios.Min() }));
            File.WriteAllText(Path.Combine(TestOutput.Directory, $"reference-timing-{night}.json"), System.Text.Json.JsonSerializer.Serialize(times));
        }
        finally
        {
            User32.UnregisterHotKey(new System.Windows.Interop.WindowInteropHelper(panel).Handle, 0x4153);
            panel.Close();
            Directory.Delete(folder, true);
        }
    }

    public static void Verify()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var folder = Path.Combine(Path.GetTempPath(), "clear-lineup-ui-" + Guid.NewGuid().ToString("N"));
        Operator[] operators = [new() { Name = "甲", Tier = 1 }, new() { Name = "乙", Tier = 1 }, new() { Name = "丙", Tier = 1 }, new() { Name = "丁", Tier = 1 }];
        var calculator = new CovenantCalculator(operators, [], new Dictionary<string, string>());
        var settings = new SettingsManager(folder);
        var store = new LineupStore(Path.Combine(folder, "lineups"), calculator.EquipmentRules);
        var lineups = new LineupViewModel(store, calculator);
        var tracking = new TrackingViewModel { TrackMultipleLineups = true, DetectedTerminalLevel = 6, IsTerminalLevelRestored = true };
        var a = new Lineup { Id = "a", Name = "被追踪的阵容", Slots = [new() { OperatorName = "甲", IsElite = true }] };
        var b = new Lineup { Id = "b", Name = "未追踪的阵容", Slots = [new() { OperatorName = "丙" }] };
        lineups.LoadLineups([a, b]);
        var operatorVm = new OperatorBrowseViewModel();
        operatorVm.LoadOperators(operators.ToList());
        var bans = new BanViewModel();
        operatorVm.SetBanChecker(bans.IsBanned);
        var panel = new SidePanelWindow(operatorVm, new EquipmentBrowseViewModel(), tracking,
            new GameStateViewModel(), bans, new EnemyViewModel(), lineups,
            new LineupShareService(calculator.EquipmentRules, Path.Combine(folder, "share")), operators, [], [], settings,
            GameShopTheme.Terminal, new SidePanelPositionSettings { FreeMovement = true })
        { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, Width = 310, Height = 900 };
        try
        {
            panel.Show();
            Pump();
            panel.PlannerVm.CurrentTerminalLevel.Should().Be(6, "a restored terminal level is available before the next OCR change");
            tracking.IsTerminalLevelRestored = false;
            tracking.DetectedTerminalLevel = 6;
            panel.PlannerVm.CurrentTerminalLevel.Should().Be(6);
            tracking.ResetTerminalLevel();
            tracking.DetectedTerminalLevel = 4;
            panel.PlannerVm.CurrentTerminalLevel.Should().Be(4);
            tracking.ResetTerminalLevel();

            var runView = (RunBanView)panel.FindName("RunBanView");
            bans.SetBans(["甲"]);
            bans.SetCovenantBans(["炎"]);
            bans.ToggleBan("乙");
            operatorVm.SelectedAvailabilityFilter = AvailabilityFilter.Available;
            operatorVm.SetAvailableCovenants(["维多利亚"]);
            ((Button)runView.FindName("ClearHighlightsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            bans.BannedNames.Should().BeEquivalentTo("乙");
            bans.BannedCovenants.Should().BeEmpty();
            operatorVm.HasRunInfo.Should().BeFalse();
            panel.PlannerVm.HasRunInfo.Should().BeFalse();
            operatorVm.FilteredOperators.Should().Contain(o => o.Name == "甲").And.NotContain(o => o.Name == "乙");
            bans.SetCovenantBans(["炎"]);
            ((Button)runView.FindName("ClearBansButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            bans.BannedNames.Should().BeEmpty();
            bans.BannedCovenants.Should().BeEmpty();
            operatorVm.FilteredOperators.Should().HaveCount(4);
            lineups.SelectedAvailabilityFilter.Should().Be(LineupAvailabilityFilter.All);
            lineups.VisibleLineups.Should().HaveCount(2);
            operatorVm.SelectedAvailabilityFilter = AvailabilityFilter.All;
            var inputModel = new InputModel { Text = "测试输入" };
            var input = new TextBox();
            input.SetBinding(TextBox.TextProperty, new Binding(nameof(InputModel.Text)) { Source = inputModel, Mode = BindingMode.TwoWay });
            var inputHost = new Window { Content = input, Width = 280, Height = 50, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
            inputHost.Show();
            try
            {
                input.Style = (Style)panel.FindResource("DialogTextBox");
                foreach (var theme in Enum.GetValues<GameShopTheme>())
                foreach (var night in new[] { false, true })
                {
                    ThemeManager.Apply(theme, night);
                    inputModel.Text = "测试输入";
                    Pump();
                    var button = WaitForClearButtonVisibility(input, Visibility.Visible);
                    button.Visibility.Should().Be(Visibility.Visible);
                    button.CommandTarget.Should().BeSameAs(input);
                    button.IsEnabled.Should().BeTrue();
                    button.Content.Should().BeNull();
                    button.Tag.Should().NotBeNull();
                    ClickClear(input);
                    input.Text.Should().BeEmpty();
                    inputModel.Text.Should().BeEmpty("clearing updates even a LostFocus binding immediately");
                    BindingOperations.IsDataBound(input, TextBox.TextProperty).Should().BeTrue();
                    button = WaitForClearButtonVisibility(input, Visibility.Collapsed);
                    button.Visibility.Should().Be(Visibility.Collapsed);
                    inputModel.Text = "再次输入";
                    Pump();
                    input.Text.Should().Be("再次输入");
                    Save(input, $"{theme}-{night}-text-clear");
                    input.IsReadOnly = true;
                    button = WaitForClearButtonVisibility(input, Visibility.Collapsed);
                    button.Visibility.Should().Be(Visibility.Collapsed);
                    input.IsReadOnly = false;
                }
            }
            finally { inputHost.Close(); }

            var mainTabs = (TabControl)panel.FindName("MainTabs");
            mainTabs.SelectedItem = panel.FindName("LineupsTab");
            ((ListBox)panel.FindName("LineupsSegment")).SelectedIndex = 0;
            Pump();
            var lineupView = (LineupListView)panel.FindName("LineupView");
            tracking.TrackLineup(a);
            Pump();
            lineupView.TrackedLineupIds.Should().BeEquivalentTo("a");
            var card = Descendants<Border>(lineupView).Single(x => Equals(x.Tag, "a"));
            card.Background.Should().Be(panel.FindResource("AccentSoftBrush"));
            Descendants<TextBlock>(card).Should().Contain(x => x.Text == "追踪中" && x.Visibility == Visibility.Visible);
            ((Button)lineupView.FindName("ClearHighlightsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            lineupView.TrackedLineupIds.Should().BeEmpty();
            tracking.IsLineupTracked("a").Should().BeTrue();
            tracking.TrackedOperators.Should().ContainSingle().Which.Name.Should().Be("甲");
            tracking.TrackLineup(a);
            tracking.TrackLineup(b);
            Pump();
            lineupView.TrackedLineupIds.Should().BeEquivalentTo("a", "b");

            var state = new DeployPoolState();
            var trackingView = (TrackingView)panel.FindName("TrackingView");
            trackingView.DeployPage.Initialize(operators.ToDictionary(o => o.Name), [], null, tracking, settings, state, false);
            mainTabs.SelectedItem = mainTabs.Items.OfType<TabItem>().Single(t => ReferenceEquals(t.Content, trackingView));
            var pages = (ListBox)trackingView.FindName("TrackingPageList");
            pages.SelectedIndex = 0;
            Pump();
            var pending = Descendants<Button>((ItemsControl)trackingView.FindName("FinalCells"))
                .Single(x => x.DataContext is TrackedCell c && c.Name == "甲" && Equals(x.Content, "待获取"));
            pending.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            var search = (TextBox)trackingView.DeployPage.FindName("PoolSearch");
            search.Text.Should().Be("甲");
            var poolCard = Descendants<Grid>(trackingView.DeployPage)
                .Single(x => x.ToolTip is string tip && tip.StartsWith("甲 · "));
            poolCard.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Pump();
            search.Text.Should().BeEmpty("acquiring a navigated target restores the full list");
            state.IsOwned("甲").Should().BeTrue();
            tracking.IsTracked("甲").Should().BeTrue("an elite target remains pending after ordinary acquisition");
            trackingView.DeployPage.FocusOperator("丙");
            search.Text = "手动条件";
            ClickClear(search);
            Pump();
            search.Text.Should().BeEmpty();
            search.Text = "丙"; // A manually entered filter is not a navigated lookup.
            Pump();
            poolCard = Descendants<Grid>(trackingView.DeployPage)
                .Single(x => x.ToolTip is string tip && tip.StartsWith("丙 · "));
            poolCard.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Pump();
            search.Text.Should().Be("丙");
            ClickClear(search);

            search.Text = "不匹配新增干员的条件";
            var add = Descendants<Button>(trackingView.DeployPage).Single(x => Equals(x.Content, "＋ 添加"));
            var pickerSeen = false;
            var pickerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            pickerTimer.Tick += (_, _) =>
            {
                var picker = Application.Current.Windows.OfType<OperatorPickerWindow>().FirstOrDefault();
                if (picker is null) return;
                pickerTimer.Stop();
                pickerSeen = true;
                var list = (ListBox)picker.FindName("OperatorsList");
                list.SelectedItem = operators.Single(o => o.Name == "丁");
                list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(picker)!, 0, Key.Enter)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                picker.Result.Should().Be("丁");
            };
            pickerTimer.Start();
            try { add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
            finally { pickerTimer.Stop(); }
            pickerSeen.Should().BeTrue();
            Pump();
            search.Text.Should().BeEmpty();
            state.IsOwned("丁").Should().BeTrue();
            state.Extra.Should().Contain("丁");
            Descendants<TextBlock>((StackPanel)trackingView.DeployPage.FindName("PoolGroups")).Should().Contain(t => t.Text == "丁");
            var candidates = (IEnumerable<PlacementCandidate>)typeof(DeployView).GetMethod("Candidates", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(trackingView.DeployPage, null)!;
            candidates.Should().Contain(c => c.Name == "丁", "the map planner receives the newly added owned operator");
            tracking.TrackedOperators.Should().NotContain(e => e.Name == "丁", "adding to the owned pool does not add a purchase target");
            trackingView.DeployPage.FindName("PoolButton").Should().BeNull("the reserve area no longer has a manage button");

            mainTabs.SelectedIndex = 0;
            Pump();
            var operatorSearch = Descendants<TextBox>(panel).Single(x => x.Name == "SearchBox" && x.DataContext == operatorVm);
            operatorSearch.Text = "甲";
            Pump();
            operatorVm.SearchText.Should().Be("甲");
            ClickClear(operatorSearch);
            operatorVm.SearchText.Should().BeEmpty();

            mainTabs.SelectedItem = panel.FindName("LineupsTab");
            ((ListBox)panel.FindName("LineupsSegment")).SelectedIndex = 0;
            Pump();
            var apply = new Lineup { Id = "a", Name = "已保存的新阵容", Slots = [new() { OperatorName = "乙" }] };
            ChooseConfirmation(panel, () => panel.SaveEditedLineupAsync(apply, true), true);
            tracking.TrackedOperators.Select(e => e.Name).Should().BeEquivalentTo("乙", "丙");
            var savedOnly = new Lineup { Id = "a", Name = "仅保存版本", Slots = [new() { OperatorName = "甲" }] };
            ChooseConfirmation(panel, () => panel.SaveEditedLineupAsync(savedOnly, true), false);
            tracking.TrackedOperators.Select(e => e.Name).Should().BeEquivalentTo("乙", "丙");
            (Await(Task.Run(store.LoadAllAsync))).Single(l => l.Id == "a").Name.Should().Be("仅保存版本");
            Await(panel.SaveEditedLineupAsync(new Lineup { Id = "untracked", Name = "未关联", Slots = [] }, true));
            using (var locked = new FileStream(Path.Combine(folder, "lineups", "a.json"), FileMode.Open, FileAccess.Read, FileShare.None))
                Await(panel.SaveEditedLineupAsync(new Lineup { Id = "a", Name = "无法写入的版本", Slots = [new() { OperatorName = "甲" }] }, true));
            Application.Current.Windows.OfType<ConfirmDialog>().Should().BeEmpty();
            tracking.TrackedOperators.Select(e => e.Name).Should().BeEquivalentTo("乙", "丙");
            state.SetCount("乙", 2);
            state.SetElite("乙", true);
            tracking.SyncDeployPool();
            Await(trackingView.DeployPage.StartNewRunAsync());
            Pump();
            state.Owned.Should().BeEmpty();
            state.Elite.Should().BeEmpty();
            state.Counts.Should().BeEmpty();
            tracking.TrackedOperators.Should().OnlyContain(e => !e.IsAcquired);
            panel.PlannerVm.CurrentTerminalLevel.Should().Be(0);
            trackingView.DeployPage.CurrentPlan.Should().BeNull();
            ((IEnumerable<PlacementCandidate>)typeof(DeployView).GetMethod("Candidates", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(trackingView.DeployPage, null)!).Should().BeEmpty();
            Await(settings.LoadDeployPoolAsync()).Owned.Should().BeEmpty();
            foreach (var theme in Enum.GetValues<GameShopTheme>())
            foreach (var night in new[] { false, true })
            foreach (var width in new[] { 280, 400 })
            {
                ThemeManager.Apply(theme, night);
                panel.Width = width;
                Await(Task.Delay(240));
                Pump();
                foreach (var button in Descendants<Button>(lineupView).Where(x => x.IsVisible))
                {
                    var bounds = button.TransformToAncestor(lineupView).TransformBounds(new Rect(button.RenderSize));
                    bounds.Right.Should().BeLessThanOrEqualTo(lineupView.ActualWidth + 0.1);
                }
                lineupView.TrackedLineupIds.Should().BeEquivalentTo("a", "b");
                foreach (var id in new[] { "a", "b" })
                {
                    var trackedCard = Descendants<Border>(lineupView).Single(x => Equals(x.Tag, id));
                    Descendants<TextBlock>(trackedCard).Should().Contain(x => x.Text == "追踪中" && x.Visibility == Visibility.Visible,
                        $"tracked badges stay visible after saving and theme switches ({theme}, {night})");
                }
                Save(lineupView, $"{theme}-{night}-{width}-lineups");
            }
        }
        finally
        {
            User32.UnregisterHotKey(new System.Windows.Interop.WindowInteropHelper(panel).Handle, 0x4153);
            panel.Close();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            ThemeManager.Apply(GameShopTheme.Terminal, true);
        }
    }

    private static void ChooseConfirmation(SidePanelWindow panel, Func<Task> action, bool apply)
    {
        bool seen = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<ConfirmDialog>().FirstOrDefault();
            if (dialog is null) return;
            timer.Stop();
            seen = true;
            ((Button)dialog.FindName("OkButton")).Content.Should().Be("应用到追踪栏");
            ((Button)dialog.FindName("CancelButton")).Content.Should().Be("仅保存");
            dialog.DialogResult = apply;
        };
        timer.Start();
        try { Await(action()); seen.Should().BeTrue(); }
        finally { timer.Stop(); }
    }

    private static Button ClearButton(TextBox input)
    {
        input.ApplyTemplate();
        return (Button)input.Template.FindName("ClearTextButton", input);
    }
    private static Button WaitForClearButtonVisibility(TextBox input, Visibility expected)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            input.UpdateLayout();
            Pump();
            var button = ClearButton(input);
            if (button.Visibility == expected) return button;
            Thread.Sleep(1);
        }
        throw new TimeoutException($"Current clear-button template did not become {expected}; text length={input.Text.Length}, read-only={input.IsReadOnly}.");
    }

    private static void ClickClear(TextBox input)
    {
        var command = (RoutedCommand)ClearButton(input).Command;
        command.CanExecute(null, input).Should().BeTrue();
        command.Execute(null, input);
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Await(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < until) Pump();
        task.IsCompleted.Should().BeTrue();
        task.GetAwaiter().GetResult();
    }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Save(FrameworkElement view, string name)
    {
        var path = Path.Combine(TestOutput.Directory, "clear-source-previews");
        Directory.CreateDirectory(path);
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(path, name + ".png"));
        encoder.Save(output);
    }
    private sealed class InputModel : INotifyPropertyChanged
    {
        private string _text = "";
        public string Text { get => _text; set { _text = value; PropertyChanged?.Invoke(this, new(nameof(Text))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
