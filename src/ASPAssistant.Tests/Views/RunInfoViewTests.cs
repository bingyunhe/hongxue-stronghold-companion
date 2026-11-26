using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

internal static class RunInfoViewTests
{
    public static void Verify(GameShopTheme theme, bool night)
    {
        var data = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "TestData", "Regulation"));
        var operators = data.LoadOperatorsAsync().GetAwaiter().GetResult().Where(o => o.RequiredStrategyId.Length == 0).Take(42).ToList();
        var (covenants, map) = data.LoadCovenantsAsync().GetAwaiter().GetResult();
        var calculator = new CovenantCalculator(operators, covenants, map);
        var operatorsVm = new OperatorBrowseViewModel();
        operatorsVm.LoadOperators(operators);
        var bans = new BanViewModel();
        operatorsVm.SetBanChecker(bans.IsBanned);
        var enemy = new EnemyViewModel();
        var bosses = new BossDataService();
        bosses.LoadAsync(Path.Combine(AppContext.BaseDirectory, "TestData", "Regulation")).GetAwaiter().GetResult();
        var boss = bosses.Lookup("假想敌：胄")!;
        enemy.Update(new(boss.Name, ["特训敌人·元素"], []), boss, [new() { TypeName = "元素" }]);
        enemy.RegulationLevel = 7;
        var folder = Path.Combine(Path.GetTempPath(), "run-info-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var lineups = new LineupViewModel(new LineupStore(Path.Combine(folder, "lineups"), calculator.EquipmentRules), calculator);
        var panel = new SidePanelWindow(operatorsVm, new EquipmentBrowseViewModel(), new TrackingViewModel(),
            new GameStateViewModel(), bans, enemy, lineups, new LineupShareService(calculator.EquipmentRules, Path.Combine(folder, "share")),
            operators, [], [], new SettingsManager(folder), theme, new SidePanelPositionSettings { FreeMovement = true }, covenants: covenants)
        { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, Height = 1000 };
        var run = (RunBanView)panel.FindName("RunBanView");
        try
        {
            panel.Show();
            ThemeManager.Apply(theme, night);
            var tab = (TabItem)panel.FindName("RunInfoTab");
            ((TabControl)panel.FindName("MainTabs")).SelectedItem = tab;
            var enemyView = (EnemyView)panel.FindName("EnemyView");
            var health = (BossHealthView)enemyView.FindName("HealthView");
            var scan = (Button)run.FindName("ManualScanButton");
            var pills = (ItemsControl)run.FindName("BanCovenantPills");
            var portraits = (ItemsControl)run.FindName("BannedOperators");
            var meta = (TextBlock)run.FindName("BanBannerMeta");
            var detailsToggle = (ToggleButton)run.FindName("BanDetailsToggle");
            var details = (StackPanel)run.FindName("BanDetailsPanel");
            ((OperatorBrowseView)panel.FindName("OperatorView")).FindName("ManualScanButton").Should().BeNull();
            panel.Width = 360;
            enemy.Clear();
            Pump();
            Descendants<TextBlock>(run).Should().Contain(t => t.Text == "对局信息");
            enemyView.FindName("RecognizeEnemyButton").Should().BeNull();
            enemyView.FindName("RecognitionStatusText").Should().BeNull();
            Descendants<TextBlock>(enemyView).Should().Contain(t => t.Text.Contains("开局时没有自动获取对局信息"));
            Save((FrameworkElement)tab.Content, $"{theme}-{night}-empty");
            enemy.Update(new(boss.Name, ["特训敌人·元素"], []), boss, [new() { TypeName = "元素" }]);
            enemy.RegulationLevel = 7;
            var names = new[] { "卡西米尔", "维多利亚", "乌萨斯", "阿戈尔" };
            foreach (var width in new[] { 280, 360, 400 })
            foreach (var count in new[] { 0, 1, 40 })
            {
                panel.Width = width;
                detailsToggle.IsChecked = true;
                bans.SetBans(operators.Take(count).Select(o => o.Name));
                bans.SetCovenantBans(count == 0 ? [] : names);
                Pump();
                run.IsVisible.Should().BeTrue();
                enemyView.IsVisible.Should().BeTrue();
                scan.IsEnabled.Should().BeTrue();
                scan.Content.Should().Be(count == 0 ? "截屏录入" : "重新录入");
                portraits.Items.Cast<Operator>().Select(op => op.Name).Should().BeEquivalentTo(panel.LineupVm.RunBannedNames);
                pills.Items.Count.Should().Be(count == 0 ? 0 : 4);
                Bounds(scan, run).Right.Should().BeLessThanOrEqualTo(run.ActualWidth + 1);
                Bounds(meta, run).Right.Should().BeLessThanOrEqualTo(run.ActualWidth + 1, "summary text must fit the narrow sidebar");
                if (count > 0)
                {
                    portraits.ActualWidth.Should().BeGreaterThanOrEqualTo(run.ActualWidth - 24, "portraits must use the full summary width");
                    foreach (var avatar in Descendants<Avatar>(portraits))
                        Bounds(avatar, run).Right.Should().BeLessThanOrEqualTo(run.ActualWidth + 1);
                }
                run.ActualHeight.Should().BeLessThan(400, "many bans must leave space for the boss information");
                ((TextBlock)health.FindName("CurrentBossHpText")).Text.Should().Be(BossHpCell.From(boss.Hp.Single(hp => hp.Mode == "mode_single_7")).Hp);
                ((Expander)health.FindName("OtherBossHpExpander")).IsExpanded.Should().BeFalse();
                Save((FrameworkElement)tab.Content, $"{theme}-{night}-{width}-{count}");
                detailsToggle.IsChecked = false; Pump();
                details.Visibility.Should().Be(Visibility.Collapsed);
                run.ActualHeight.Should().BeLessThanOrEqualTo(50);
                scan.IsVisible.Should().BeTrue();
                ((Button)run.FindName("ClearHighlightsButton")).IsVisible.Should().BeTrue();
                ((Button)run.FindName("ClearBansButton")).IsVisible.Should().BeTrue();
                bans.SetCovenantBans(count == 0 ? [] : names); Pump();
                detailsToggle.IsChecked.Should().BeFalse("publishing updated bans must preserve the collapsed state");
                Save(run, $"{theme}-{night}-{width}-collapsed");
            }

            var types = new EnemyTypeDataService();
            types.LoadAsync(Path.Combine(AppContext.BaseDirectory, "data")).GetAwaiter().GetResult();
            var dot = types.LookupByTypeName("持续")!;
            var fly = types.LookupByTypeName("飞行")!;
            void RefreshTypes() => enemy.Update(new(boss.Name, ["特训敌人·持续", "特训敌人·飞行"], []), boss, [dot, fly]);
            RefreshTypes(); Pump();
            var bossSection = (Expander)enemyView.FindName("BossExpander");
            Expander TypeSection(string name) => Descendants<Expander>(enemyView)
                .Single(e => e.DataContext is EnemyTypeEntry t && t.TypeName == name);
            var dotSection = TypeSection("持续");
            var flySection = TypeSection("飞行");
            Assert.True(bossSection.IsExpanded && dotSection.IsExpanded && flySection.IsExpanded);
            void ClickHeader(Expander section)
            {
                var toggle = Descendants<ToggleButton>(section).First();
                Assert.True(toggle.ActualWidth >= section.ActualWidth - 4);
                var peer = new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(toggle);
                ((System.Windows.Automation.Provider.IToggleProvider)peer).Toggle();
            }
            ClickHeader(dotSection); ClickHeader(bossSection); Pump();
            Assert.False(health.IsVisible);
            Assert.False(((FrameworkElement)dotSection.Content).IsVisible);
            Assert.True(((FrameworkElement)flySection.Content).IsVisible);
            RefreshTypes(); Pump();
            Assert.False(TypeSection("持续").IsExpanded);
            Assert.True(TypeSection("飞行").IsExpanded);
            TypeSection("飞行").IsExpanded = false;
            TypeSection("持续").IsExpanded = true; Pump();
            Save(enemyView, $"{theme}-{night}-folds");
            bossSection.IsExpanded = true; Pump();
            var hpExpander = (Expander)health.FindName("OtherBossHpExpander");
            var singleHp = (StackPanel)health.FindName("SingleBossHpSection");
            var multiHp = (ItemsControl)health.FindName("MultiBossHpItems");
            panel.Width = 360;
            foreach (var mode in new[] { "标准模拟", "险境模拟", "绝境模拟", "终极模拟" })
            {
                enemy.CurrentMode = mode;
                Pump();
                singleHp.Visibility.Should().Be(Visibility.Collapsed);
                multiHp.IsVisible.Should().BeTrue();
                multiHp.Items.Cast<BossHpCell>().Single(item => item.IsCurrent).Name.Should().Be(mode);
                hpExpander.Header.Should().Be("多人难度血量");
            }
            var viewTransform = enemyView.LayoutTransform;
            foreach (var scale in new[] { 1d, 1.25 })
            {
                enemyView.LayoutTransform = new ScaleTransform(scale, scale);
                hpExpander.BringIntoView();
                Pump();
                singleHp.ActualHeight.Should().Be(0);
                multiHp.Items.Count.Should().Be(4);
                var hiddenHp = Descendants<TextBlock>(multiHp).Where(text => text.IsVisible && text.Text.StartsWith("隐藏 ")).ToArray();
                hiddenHp.Should().HaveCount(4);
                if (scale > 1)
                    hiddenHp.Should().Contain(text => text.ActualHeight >= text.FontSize * 2, "long hidden HP must wrap in a narrow panel");
                Save((FrameworkElement)tab.Content, $"{theme}-{night}-multiplayer-{scale}");
            }
            enemyView.LayoutTransform = viewTransform;
            enemy.CurrentMode = null;
            Pump();
            singleHp.IsVisible.Should().BeTrue();
            hpExpander.Header.Should().Be("全部难度血量");
            enemy.RegulationLevel = 7;
            Pump();
            hpExpander.IsExpanded.Should().BeFalse();
            hpExpander.IsExpanded = true;
            Pump();
            singleHp.IsVisible.Should().BeTrue();

            var manualScans = 0;
            panel.ManualScanRequested += () => manualScans++;
            scan.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            manualScans.Should().Be(1);
            panel.UpdateManualScanStatus(2);
            scan.IsEnabled.Should().BeFalse();
            ((TextBlock)run.FindName("ManualScanStatusText")).Text.Should().Contain("2");
            panel.UpdateManualScanStatus(0);
            scan.IsEnabled.Should().BeTrue();
            ((TextBlock)run.FindName("ManualScanStatusText")).Visibility.Should().Be(Visibility.Collapsed);
            enemyView.FindName("RecognizeEnemyButton").Should().BeNull();
            enemyView.FindName("RecognitionStatusText").Should().BeNull();

            bans.SetBans([operators[0].Name]);
            bans.ToggleBan(operators[1].Name);
            operatorsVm.SetAvailableCovenants(["乌萨斯", "奥术"]);
            run.FindName("RunMenuButton").Should().BeNull();
            var onlyCurrent = (CheckBox)((OperatorBrowseView)panel.FindName("OperatorView")).FindName("OnlyCurrentRunToggle");
            panel.ApplyAutomaticCurrentRunFilters(); Pump();
            onlyCurrent.IsChecked.Should().BeFalse();
            panel.PlannerVm.OnlyCurrentRun.Should().BeFalse();
            panel.InitAutomaticCurrentRunFilters(true);
            panel.ApplyAutomaticCurrentRunFilters(); Pump();
            onlyCurrent.IsChecked.Should().BeTrue();
            panel.PlannerVm.OnlyCurrentRun.Should().BeTrue();
            onlyCurrent.IsChecked = false;
            operatorsVm.OnlyCurrentRun.Should().BeFalse();
            panel.PlannerVm.OnlyCurrentRun.Should().BeTrue("the two controls remain independent when changed manually");
            panel.ApplyAutomaticCurrentRunFilters(); Pump();
            operatorsVm.OnlyCurrentRun.Should().BeTrue();
            ((Button)run.FindName("ClearHighlightsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            bans.BannedNames.Should().BeEquivalentTo([operators[1].Name]);
            bans.BannedCovenants.Should().BeEmpty();
            operatorsVm.HasRunInfo.Should().BeFalse();
            portraits.Items.Cast<Operator>().Select(o => o.Name).Should().Equal(operators[1].Name);
            onlyCurrent.IsChecked.Should().BeFalse();
            panel.PlannerVm.OnlyCurrentRun.Should().BeFalse();
            ((Button)run.FindName("ClearBansButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            bans.BannedNames.Should().BeEmpty();
            portraits.Items.Cast<Operator>().Should().BeEmpty();
            enemy.BossName.Should().Be(boss.Name, "clearing bans must preserve enemy information");
        }
        finally
        {
            User32.UnregisterHotKey(new System.Windows.Interop.WindowInteropHelper(panel).Handle, 0x4153);
            panel.Close();
            Directory.Delete(folder, true);
        }
    }

    private static Rect Bounds(FrameworkElement child, Visual root)
        => child.TransformToAncestor(root).TransformBounds(new Rect(0, 0, child.ActualWidth, child.ActualHeight));

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Save(FrameworkElement element, string name)
    {
        var folder = Path.Combine(TestOutput.Directory, "run-info");
        Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(stream);
    }
}
