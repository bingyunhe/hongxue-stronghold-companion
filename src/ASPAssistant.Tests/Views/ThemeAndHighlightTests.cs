// Modified for ASP Assistant Arknights on 2026-11-10; see NOTICE.
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
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

[Collection("Update environment")]
public partial class ThemeAndHighlightTests(WpfTestHost ui) : IClassFixture<WpfTestHost>
{
    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    public Task Configuration_ShowsDirectoryAndUnifiedActions(GameShopTheme theme, bool night)
        => ui.RunAsync(() => ConfigurationViewTests.Verify(theme, night), $"Configuration-{theme}-{night}");

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    public Task UpdateHistory_FoldsVersionsAcrossThemes(GameShopTheme theme, bool night)
        => ui.RunAsync(() => UpdateHistoryViewTests.Verify(theme, night), $"UpdateHistory-{theme}-{night}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DesertOverlayRestoresPlanAndCells(bool night)
        => ui.RunAsync(() => RoundRoutesViewTests.VerifyDesertOverlay(night), $"DesertOverlay-{night}");

    [Fact]
    public Task PalaceOverlayShowsOnlyPlannedNamesAndFacingByDefault()
        => ui.RunAsync(RoundRoutesViewTests.VerifyPalaceOverlay, nameof(PalaceOverlayShowsOnlyPlannedNamesAndFacingByDefault));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RecognizedAndCorrectedUnitsReachAllThreePlans(bool night)
        => ui.RunAsync(() => RoundRoutesViewTests.VerifyLiveRecommendations(night), $"LiveRecommendations-{night}");

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    public Task Feedback_ReviewsDiagnosticsAcrossThemes(GameShopTheme theme, bool night)
        => ui.RunAsync(() => FeedbackViewTests.Verify(theme, night), $"Feedback-{theme}-{night}");


    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    public Task PageScalesAndDialogs_AcrossThemes(GameShopTheme theme, bool night)
        => ui.RunAsync(() => PageScaleDialogViewTests.Verify(theme, night), $"PageScalesAndDialogs-{theme}-{night}");

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    public Task SidebarUiScale_AcrossThemes(GameShopTheme theme, bool night)
        => ui.RunAsync(() => SidebarUiScaleViewTests.Verify(theme, night), $"SidebarUiScale-{theme}-{night}");

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    public Task ProjectLinkAndOriginalGuideSharing(GameShopTheme theme, bool night)
        => ui.RunAsync(() => ProjectSharingViewTests.Verify(theme, night), $"ProjectLinkAndOriginalGuideSharing-{theme}-{night}");

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    public Task UpgradeReminders_EditorAndStatusCard(GameShopTheme theme, bool night)
        => ui.RunAsync(() => UpgradeReminderViewTests.Verify(theme, night), $"UpgradeReminders-{theme}-{night}");

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    public Task StrategySupportSearch_AcrossThemes(GameShopTheme theme, bool night)
        => ui.RunAsync(() => StrategySupportSearchViewTests.Verify(theme, night), $"StrategySupportSearch-{theme}-{night}");

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    public Task AutomaticPurchases_ExperimentalSettingAcrossThemes(GameShopTheme theme, bool night)
        => ui.RunAsync(() => ExperimentalPurchaseViewTests.Verify(theme, night), $"AutomaticPurchases-{theme}-{night}");

    [Fact]
    public Task EmulatorFix_PickerScopeAndExcludedPools() => ui.RunAsync(EmulatorFixViewTests.VerifyPicker, nameof(EmulatorFix_PickerScopeAndExcludedPools));

    [Fact]
    public Task EmulatorFix_OwnedDialogTopmostAndMoveSnap() => ui.RunAsync(EmulatorFixViewTests.VerifyPanel, nameof(EmulatorFix_OwnedDialogTopmostAndMoveSnap));

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    public Task ForceTopmost_LinkedControlsAndLayers(GameShopTheme theme, bool night)
        => ui.RunAsync(() => ForceTopmostViewTests.Verify(theme, night), $"ForceTopmost-{theme}-{night}");

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    public Task CloseBehavior_UnifiedDialogSettingsAndTray(GameShopTheme theme, bool night)
        => ui.RunAsync(() => CloseBehaviorViewTests.Verify(theme, night), $"CloseBehavior-{theme}-{night}");

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    public Task DialogPosition_CentersFinalSizeAndPreservesDragging(GameShopTheme theme, bool night)
        => ui.RunAsync(() => DialogPositionViewTests.Verify(theme, night), $"DialogPosition-{theme}-{night}");

    [Fact]
    public Task EmulatorFix_ConnectionLayoutAcrossThemes() => ui.RunAsync(EmulatorFixViewTests.VerifyConnectionLayout, nameof(EmulatorFix_ConnectionLayoutAcrossThemes));

    [Fact]
    public Task RecognizedEquipment_ShowsAcquiredInShoppingListAndReloads() => ui.RunAsync(EmulatorFixViewTests.VerifyEquipmentPurchase, nameof(RecognizedEquipment_ShowsAcquiredInShoppingListAndReloads));

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    public Task OperatorPicker_InteractionAcrossThemes(GameShopTheme theme, bool night)
        => ui.RunAsync(() => OperatorPickerPerformanceViewTests.VerifyInteraction(theme, night), $"OperatorPicker-Interaction-{theme}-{night}");

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    public Task OperatorPicker_VirtualizesTwoColumns(GameShopTheme theme, bool night)
        => ui.RunAsync(() => OperatorPickerPerformanceViewTests.VerifyVirtualization(theme, night), $"OperatorPicker-Virtualization-{theme}-{night}");

    [Fact]
    public async Task UiFailure_ClosesWindowsAndRestoresTheme()
    {
        var closed = false;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => ui.RunAsync(() =>
        {
            var window = new Window { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            window.Closed += (_, _) => closed = true;
            ThemeManager.Apply(GameShopTheme.Closure, false);
            throw new InvalidOperationException("simulated UI assertion failure");
        }, "failure isolation"));
        failure.Message.Should().Be("simulated UI assertion failure");
        closed.Should().BeTrue();
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Application.Current.Windows.Count.Should().Be(0);
            ThemeManager.Theme.Should().Be(GameShopTheme.Terminal);
            ThemeManager.IsNight.Should().BeTrue();
        });
    }

    [Fact]
    public Task ShopEconomy_RendersAndResetsAcrossThemesAndWidths() => ui.RunAsync(() =>
    {
        var vm = new TrackingViewModel();
        var view = new TrackingView { DataContext = vm };
        var row = (Border)view.FindName("EconomyRow");
        var round = (TextBlock)view.FindName("EconomyRound");
        var money = (TextBlock)view.FindName("EconomyMoney");
        var cost = (TextBlock)view.FindName("EconomyCost");
        var mode = (TextBlock)view.FindName("EconomyMode");
        foreach (var theme in Enum.GetValues<GameShopTheme>())
        foreach (var night in new[] { false, true })
        foreach (var width in new[] { 280, 360 })
        {
            void Layout()
            {
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                view.Measure(new Size(width, 900));
                view.Arrange(new Rect(0, 0, width, 900));
                view.UpdateLayout();
            }
            ThemeManager.Apply(theme, night);
            vm.Economy = null;
            Layout();
            row.Visibility.Should().Be(Visibility.Collapsed);
            vm.Economy = new(99, 999, 999, false, "规程等级20", true);
            Layout();
            row.Visibility.Should().Be(Visibility.Visible);
            round.Text.Should().Be("99");
            money.Text.Should().Be("999");
            cost.Text.Should().Be("999");
            mode.Text.Should().Be("规程等级20");
            foreach (var text in new[] { round, money, cost, mode })
                text.DesiredSize.Width.Should().BeLessThanOrEqualTo(text.ActualWidth + 1);
            var output = Path.Combine(TestOutput.Directory, "shop-economy");
            Directory.CreateDirectory(output);
            var bitmap = new RenderTargetBitmap(width, 900, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(output, $"{theme}-{night}-{width}.png"))) encoder.Save(file);
            vm.Economy = new(1, null, null, true, "标准模拟", true);
            Layout();
            money.Text.Should().Be("–");
            cost.Text.Should().Be("满级");
            mode.Text.Should().Be("标准模拟");
            vm.Economy = null;
            Layout();
            row.Visibility.Should().Be(Visibility.Collapsed);
        }
    }, nameof(ShopEconomy_RendersAndResetsAcrossThemesAndWidths));

    [Fact]
    public Task EnemyHp_FocusesRecognizedDifficulty() => ui.RunAsync(() =>
    {
        var bosses = new BossDataService();
        bosses.LoadAsync(Path.Combine(AppContext.BaseDirectory, "TestData", "Regulation")).GetAwaiter().GetResult();
        var boss = bosses.Lookup("假想敌：胄")!;
        var vm = new EnemyViewModel();
        vm.Update(new(boss.Name, ["特训敌人·元素"], []), boss, [new() { TypeName = "元素" }]);
        var view = new EnemyView { DataContext = vm };
        var health = (BossHealthView)view.FindName("HealthView");
        var current = (Border)health.FindName("CurrentBossHpCard");
        var currentText = (TextBlock)health.FindName("CurrentBossHpText");
        var other = (Expander)health.FindName("OtherBossHpExpander");
        var items = (ItemsControl)health.FindName("SingleBossHpItems");
        foreach (var theme in Enum.GetValues<GameShopTheme>())
        foreach (var night in new[] { false, true })
        foreach (var width in new[] { 280, 360 })
        {
            void Layout()
            {
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                view.Measure(new Size(width, 1400));
                view.Arrange(new Rect(0, 0, width, 1400));
                view.UpdateLayout();
            }
            ThemeManager.Apply(theme, night);
            vm.RegulationLevel = null;
            Layout();
            current.Visibility.Should().Be(Visibility.Collapsed);
            other.IsExpanded.Should().BeTrue();
            items.Items.Count.Should().Be(21);
            vm.RegulationLevel = 7;
            Layout();
            current.Visibility.Should().Be(Visibility.Visible);
            currentText.Text.Should().Be(BossHpCell.From(boss.Hp.Single(hp => hp.Mode == "mode_single_7")).Hp);
            other.IsExpanded.Should().BeFalse();
            other.Header.Should().Be("其他难度血量");
            items.Items.Count.Should().Be(20);
            var output = Path.Combine(TestOutput.Directory, "boss-hp-focus");
            Directory.CreateDirectory(output);
            var bitmap = new RenderTargetBitmap(width, 1400, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(output, $"{theme}-{night}-{width}.png"))) encoder.Save(file);
            other.IsExpanded = true;
            vm.OtherBossHpExpanded.Should().BeTrue();
            vm.RegulationLevel = 8;
            Layout();
            other.IsExpanded.Should().BeFalse();
            currentText.Text.Should().Be(BossHpCell.From(boss.Hp.Single(hp => hp.Mode == "mode_single_8")).Hp);
        }
    }, nameof(EnemyHp_FocusesRecognizedDifficulty));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MissingCovenantCatalogueAndPicker(bool night)
        => ui.RunAsync(() => OperatorCatalogueViewTests.VerifyMissingCovenants(night), $"missing-covenants-{night}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MissingCovenantOperatorLists(bool night)
        => ui.RunAsync(() => OperatorCatalogueViewTests.VerifyMissingCovenantLists(night), $"missing-lists-{night}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task OperatorCatalogue_CyclesLayouts(bool night)
        => ui.RunAsync(() => OperatorCatalogueViewTests.VerifyLayouts(night), $"{nameof(OperatorCatalogue_CyclesLayouts)}-{night}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TrackingTierBadge_UsesCatalogTiers(bool night)
        => ui.RunAsync(() => TrackingStageViewTests.VerifyTierBadges(night), $"{nameof(TrackingTierBadge_UsesCatalogTiers)}-{night}");

    [Fact]
    public Task OperatorPicker_PreservesSelectionWhileScrolling() => ui.RunAsync(OperatorPickerTests.VerifySelection, nameof(OperatorPicker_PreservesSelectionWhileScrolling));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LiveFieldPicker_ChecksRunCovenantsAndHoldings(bool night)
        => ui.RunAsync(() => OperatorPickerTests.VerifyRunHoldings(night), $"{nameof(LiveFieldPicker_ChecksRunCovenantsAndHoldings)}-{night}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LiveFieldPicker_SelectsFromRightEdge(bool night)
        => ui.RunAsync(() => OperatorPickerTests.VerifyLiveCorrection(night), $"{nameof(LiveFieldPicker_SelectsFromRightEdge)}-{night}");

    [Theory]
    [Trait("Category", "LocalScreenshot")]
    [InlineData(false)]
    [InlineData(true)]
    public Task LiveFieldPicker_RanksUnknownOnRequest(bool night)
        => ui.RunAsync(() => OperatorPickerTests.VerifyRankedCorrection(night), $"{nameof(LiveFieldPicker_RanksUnknownOnRequest)}-{night}");

    [Theory]
    [Trait("Category", "LocalScreenshot")]
    [InlineData(false)]
    [InlineData(true)]
    public Task LiveFieldQuickMenu_UsesCapturedSnakeAndRejectsStaleClick(bool night)
        => ui.RunAsync(() => OperatorPickerTests.VerifyRankedCorrection(night, true), $"{nameof(LiveFieldQuickMenu_UsesCapturedSnakeAndRejectsStaleClick)}-{night}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RoundRoutes_MapPreview(bool night)
        => ui.RunAsync(() => RoundRoutesViewTests.Verify(night), $"{nameof(RoundRoutes_MapPreview)}-{night}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RoundRoutes_GatePlayback(bool night)
        => ui.RunAsync(() => RoundRoutesViewTests.VerifyPlayback(night), $"{nameof(RoundRoutes_GatePlayback)}-{night}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RoundEnemyCards_MapAndRunInfoShareRecognition(bool night)
        => ui.RunAsync(() => RoundRoutesViewTests.VerifyRecognitionCards(night), $"{nameof(RoundEnemyCards_MapAndRunInfoShareRecognition)}-{night}");

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    public Task PlannerChrome_HasReadableContrast(GameShopTheme theme, bool night) => ui.RunAsync(() =>
    {
        var data = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "TestData", "Regulation"));
        var operators = data.LoadOperatorsAsync().GetAwaiter().GetResult();
        var (equipment, _) = data.LoadEquipmentAsync().GetAwaiter().GetResult();
        var (covenants, map) = data.LoadCovenantsAsync().GetAwaiter().GetResult();
        var calculator = new CovenantCalculator(operators, covenants, map);
        var folder = Path.Combine(Path.GetTempPath(), "planner-contrast-" + Guid.NewGuid().ToString("N"));
        var panel = new SidePanelWindow(new OperatorBrowseViewModel(), new EquipmentBrowseViewModel(),
            new TrackingViewModel(), new GameStateViewModel(), new BanViewModel(), new EnemyViewModel(),
            new LineupViewModel(new LineupStore(Path.Combine(folder, "lineups"), calculator.EquipmentRules), calculator),
            new LineupShareService(calculator.EquipmentRules, Path.Combine(folder, "share")), operators, equipment, [],
            new SettingsManager(folder), theme, new SidePanelPositionSettings { FreeMovement = true }, covenants: covenants)
        { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, Height = 1000 };
        panel.Show();
        ThemeManager.Apply(theme, night);
        ((TabControl)panel.FindName("MainTabs")).SelectedItem = panel.FindName("LineupsTab");
        ((ListBox)panel.FindName("LineupsSegment")).SelectedIndex = 1;
        panel.PlannerVm.IsActive = false;
        var view = (LineupPlannerView)panel.FindName("PlannerView");
        var goalTab = (Button)view.FindName("GoalTabButton");
        ((IInvokeProvider)new ButtonAutomationPeer(goalTab).GetPattern(PatternInterface.Invoke)!).Invoke();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        panel.PlannerVm.ShowGoals.Should().BeTrue();
        panel.PlannerVm.AllCovenants.Single(c => c.Name == "乌萨斯").Level = 2;
        panel.PlannerVm.AllCovenants.Single(c => c.Name == "奥术").Level = 2;
        panel.PlannerVm.StatusText = "计算完成";
        panel.PlannerVm.StatusDetail = "0.0 秒 · 改动后自动重算";
        var navigate = (Button)view.FindName("PlannerNavigationButton");
        var invoke = (IInvokeProvider)new ButtonAutomationPeer(navigate).GetPattern(PatternInterface.Invoke)!;
        var output = Path.Combine(TestOutput.Directory, "planner-chrome-contrast");
        Directory.CreateDirectory(output);
        using var assertions = new FluentAssertions.Execution.AssertionScope();
        foreach (var width in new[] { 280, 360, 400 })
        foreach (var results in new[] { false, true })
        {
            panel.Width = width;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            panel.PlannerVm.ShowResults.Should().Be(results);
            navigate.IsVisible.Should().BeTrue();
            navigate.Content.Should().Be(results ? "← 返回目标" : panel.PlannerVm.ViewResultsText);
            goalTab.Tag.Should().Be("Active");
            Save((FrameworkElement)panel.Content, Path.Combine(output, $"{theme}-{night}-{width}-{results}.png"));
            var texts = new[] { "AutoTabButton", "GoalTabButton", "PlannerNavigationButton" }
                .SelectMany(name => Descendants<TextBlock>((Button)view.FindName(name)))
                .Concat(Descendants<TextBlock>(view).Where(t => t.Text is "计算完成" or "0.0 秒 · 改动后自动重算")).ToList();
            texts.Should().HaveCount(5);
            foreach (var text in texts)
            {
                var foreground = ((SolidColorBrush)text.Foreground).Color;
                var background = Background(text);
                var a = Luminance(foreground);
                var b = Luminance(background);
                ((Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05)).Should().BeGreaterThanOrEqualTo(4.5,
                    $"{theme}, night={night}, width={width}, results={results}, text={text.Text}");
            }
            var before = navigate.TransformToAncestor(view).TransformBounds(new Rect(navigate.RenderSize));
            invoke.Invoke();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var after = navigate.TransformToAncestor(view).TransformBounds(new Rect(navigate.RenderSize));
            after.Right.Should().BeApproximately(before.Right, 1);
            after.Top.Should().BeApproximately(before.Top, 1);
            panel.PlannerVm.ShowResults.Should().Be(!results);
            panel.PlannerVm.AllCovenants.Where(c => c.IsSelected).Select(c => (c.Name, c.Level))
                .Should().BeEquivalentTo(new[] { ("乌萨斯", 2), ("奥术", 2) });
        }
        if (theme == GameShopTheme.Closure)
        {
            var anchor = (StackPanel)((DataTemplate)view.Resources["RouteOperatorTemplate"]).LoadContent();
            view.Content = anchor;
            var op = operators.Single(o => o.Name == "青枳");
            var tip = (ToolTip)anchor.ToolTip;
            tip.PlacementTarget = anchor;
            const string shortNote = "本阶段新加入";
            const string longNote = "过渡卡：成型阵容前会被替换 · 需要三连获得（终端 2 只出 Ⅱ 阶及以下） · 本阶段新加入 · 每回合约为成型阵容盟约叠 9 层（按普通版本估算）";
            foreach (var scale in new[] { 1.0, 1.25 })
            {
                panel.Appearance.UiScale = scale;
                double singleLineHeight = 0;
                foreach (var note in new[] { shortNote, longNote })
                {
                    anchor.DataContext = new RouteOperatorItem { Operator = op, Name = op.Name, ExtraInfo = note };
                    tip.IsOpen = true;
                    try
                    {
                        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                        var label = Descendants<TextBlock>(tip).Single(t => t.Text == note);
                        var cards = (StackPanel)VisualTreeHelper.GetParent(Descendants<ContentControl>(tip).Single(c => Equals(c.Tag, "普通")));
                        var banner = (Border)label.Parent;
                        banner.ActualWidth.Should().BeApproximately(cards.ActualWidth, 1);
                        tip.ActualWidth.Should().BeLessThan(cards.ActualWidth + 20);
                        if (note == shortNote) singleLineHeight = label.ActualHeight;
                        else label.ActualHeight.Should().BeGreaterThan(singleLineHeight * 1.5);
                        Save((FrameworkElement)tip.Content, Path.Combine(output, $"route-{night}-{scale}-{(note == shortNote ? "short" : "long")}.png"));
                    }
                    finally { tip.IsOpen = false; }
                }
            }
        }
        panel.Close();

        static Color Background(DependencyObject element)
        {
            for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is Border { Background: SolidColorBrush brush } && brush.Color.A == 255) return brush.Color;
            throw new InvalidOperationException("Planner text has no opaque background.");
        }
        static double Luminance(Color color)
        {
            static double Linear(byte value) => value / 255.0 <= 0.04045
                ? value / 255.0 / 12.92 : Math.Pow((value / 255.0 + 0.055) / 1.055, 2.4);
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
    }, $"{nameof(PlannerChrome_HasReadableContrast)}-{theme}-{night}");

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    public Task TransientHints_HaveReadableContrast(GameShopTheme theme, bool night) => ui.RunAsync(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "hint-contrast-" + Guid.NewGuid().ToString("N"));
        var calculator = new CovenantCalculator([], [], new Dictionary<string, string>());
        var panel = new SidePanelWindow(new OperatorBrowseViewModel(), new EquipmentBrowseViewModel(),
            new TrackingViewModel(), new GameStateViewModel(), new BanViewModel(), new EnemyViewModel(),
            new LineupViewModel(new LineupStore(Path.Combine(folder, "lineups"), calculator.EquipmentRules), calculator),
            new LineupShareService(calculator.EquipmentRules, Path.Combine(folder, "share")), [], [], [],
            new SettingsManager(folder), GameShopTheme.Terminal, new SidePanelPositionSettings());
        var content = (FrameworkElement)panel.Content;
        panel.Content = null;
        var host = new Window { Content = content, Width = 280, Height = 900, WindowStyle = WindowStyle.None,
            Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        host.Show();
        ThemeManager.Apply(theme, night);
        var toast = (Border)panel.FindName("ToastBorder");
        var toastText = (TextBlock)panel.FindName("ToastText");
        toastText.Text = "已追踪阵容“示例阵容”：新增 7 干员 / 2 装备";
        toast.Visibility = Visibility.Visible;
        toast.Opacity = 1;
        var tooltip = new ToolTip { Content = "把这套阵容的干员加入追踪" };
        tooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        tooltip.Arrange(new Rect(tooltip.DesiredSize));
        tooltip.UpdateLayout();
        var tooltipText = Descendants<TextBlock>(tooltip).Single(t => t.Text == (string)tooltip.Content);
        var output = Path.Combine(TestOutput.Directory, "hint-contrast");
        Directory.CreateDirectory(output);
        using var assertions = new FluentAssertions.Execution.AssertionScope();
        foreach (var (name, picker) in new[]
        {
            ("equipment", new SimplePickerDialog([new Equipment { Name = "示例装备", Tier = 3,
                Normal = new EquipmentVariant { EffectDescription = "提升攻击力。" } }], null, false)),
            ("strategy", new SimplePickerDialog("选择策略", [new SimplePickerDialog.PickerItem(
                "example", "示例策略", null, Summary: "示例策略：提升本局阵容的作战能力。")], null))
        })
        {
            var pickerContent = (FrameworkElement)picker.Content;
            picker.Content = null;
            new Window { Content = pickerContent, Width = 420, Height = 300, WindowStyle = WindowStyle.None,
                Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 }.Show();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var list = (ListBox)picker.FindName("ItemsList");
            var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            var summary = (ToolTip)item.ToolTip;
            summary.PlacementTarget = item;
            summary.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            summary.Arrange(new Rect(summary.DesiredSize));
            summary.UpdateLayout();
            var text = (TextBlock)summary.Content;
            text.Text.Should().NotBeNullOrEmpty();
            ((SolidColorBrush)text.Foreground).Color.Should().Be(((SolidColorBrush)tooltip.Foreground).Color,
                $"{name} summary inherits the readable tooltip text color in {theme}, night={night}");
            summary.Background.Should().BeSameAs(tooltip.Background);
            Save(summary, Path.Combine(output, $"{theme}-{night}-{name}-summary.png"));
        }
        foreach (var width in new[] { 280, 360 })
        {
            host.Width = width;
            content.Measure(new Size(width, 900));
            content.Arrange(new Rect(0, 0, width, 900));
            content.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            toast.IsVisible.Should().BeTrue();
            Save(tooltip, Path.Combine(output, $"{theme}-{night}-{width}-tooltip.png"));
            var toastPreview = new Border { Width = toast.ActualWidth, Height = toast.ActualHeight, Background = new VisualBrush(toast) };
            toastPreview.Measure(toast.RenderSize);
            toastPreview.Arrange(new Rect(toast.RenderSize));
            Save(toastPreview, Path.Combine(output, $"{theme}-{night}-{width}-toast.png"));
            var surface = ((SolidColorBrush)panel.FindResource("PanelBackgroundBrush")).Color;
            foreach (var (text, background) in new[] { (tooltipText, tooltip.Background), (toastText, toast.Background) })
            {
                var foreground = ((SolidColorBrush)text.Foreground).Color;
                var color = ((SolidColorBrush)background).Color;
                var alpha = color.A / 255.0;
                var composite = Color.FromRgb((byte)(color.R * alpha + surface.R * (1 - alpha)),
                    (byte)(color.G * alpha + surface.G * (1 - alpha)), (byte)(color.B * alpha + surface.B * (1 - alpha)));
                var a = Luminance(foreground);
                var b = Luminance(composite);
                var contrast = (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
                contrast.Should().BeGreaterThanOrEqualTo(4.5, $"{theme}, night={night}, width={width}, text={text.Text}");
            }
        }

        static double Luminance(Color color)
        {
            static double Linear(byte value) => value / 255.0 <= 0.04045
                ? value / 255.0 / 12.92 : Math.Pow((value / 255.0 + 0.055) / 1.055, 2.4);
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
    }, $"{nameof(TransientHints_HaveReadableContrast)}-{theme}-{night}");

    [Fact]
    public Task CustomRoster_TierMouseClick() => ui.RunAsync(LineupFeatureTests.VerifyTierMouse, nameof(CustomRoster_TierMouseClick));

    [Fact]
    public Task CustomRoster_FilterAndPinSelection() => ui.RunAsync(LineupFeatureTests.VerifyCustomFilterOrder, nameof(CustomRoster_FilterAndPinSelection));

    [Fact]
    public Task CustomCarry_RecognizesCorrectsAndRendersAllThemes() => ui.RunAsync(LineupFeatureTests.VerifyCustomCarry, nameof(CustomCarry_RecognizesCorrectsAndRendersAllThemes));

    [Fact]
    public Task LineupEditor_FiltersAndEditsLineups() => ui.RunAsync(LineupFeatureTests.Verify, nameof(LineupEditor_FiltersAndEditsLineups));

    [Fact]
    public Task CandidateEquipment_PickerEditorAndTracking() => ui.RunAsync(CandidateEquipmentViewTests.Verify, nameof(CandidateEquipment_PickerEditorAndTracking));

    [Fact]
    public Task LineupRecommendations_FilterAndOpenRepair() => ui.RunAsync(LineupRecommendationViewTests.Verify, nameof(LineupRecommendations_FilterAndOpenRepair));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PrecisionTransfer_UsesOneCarrierUnderThreeCap(bool night)
        => ui.RunAsync(() => PlannerLockedBanViewTests.VerifyPrecisionTransfer(night), $"PrecisionTransfer-{night}");

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    public Task ManualSubstitution_ExpandsAndUpdatesAfterPurchase(GameShopTheme theme, bool night)
        => ui.RunAsync(() => PlannerLockedBanViewTests.VerifyManualSubstitution(theme, night), $"ManualSubstitution-{theme}-{night}");

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    public Task LineupStates_FilterAndShowPendingRecommendations(GameShopTheme theme, bool night)
        => ui.RunAsync(() => LineupStateFilterViewTests.Verify(theme, night), $"{nameof(LineupStates_FilterAndShowPendingRecommendations)}-{theme}-{night}");

    [Fact]
    public Task BulkLineupDeletion_PreservesHiddenLineups()
        => ui.RunAsync(UpdateAndTrayTests.VerifyBulkLineupDeletion, nameof(BulkLineupDeletion_PreservesHiddenLineups));

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    public Task EditorRedesign_PickersAndCovenantIcons(GameShopTheme theme, bool night)
        => ui.RunAsync(() => EditorRedesignViewTests.Verify(theme, night), $"{nameof(EditorRedesign_PickersAndCovenantIcons)}-{theme}-{night}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task StrategyPicker_FirstSelectionFitsViewport(bool night)
        => ui.RunAsync(() => EditorRedesignViewTests.VerifyStrategyViewport(night), $"StrategyPickerViewport-{night}");

    [Fact]
    public Task StrategyPicker_SavedPrioritiesAndCancellation()
        => ui.RunAsync(EditorRedesignViewTests.VerifyStrategyOrder, nameof(StrategyPicker_SavedPrioritiesAndCancellation));

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    public Task EditorCopy_SubmenuAndPhaseResult(GameShopTheme theme, bool night)
        => ui.RunAsync(() => EditorCopyMenuTests.Verify(theme, night), $"{nameof(EditorCopy_SubmenuAndPhaseResult)}-{theme}-{night}");

    [Fact]
    public Task UpdateSettings_TrayAndHotkeys() => ui.RunAsync(UpdateAndTrayTests.Verify, nameof(UpdateSettings_TrayAndHotkeys));

    [Fact]
    public Task MirrorChyanSettings_MasksAndSavesAtBothScales() =>
        ui.RunAsync(UpdateAndTrayTests.VerifyMirrorChyanSettings, nameof(MirrorChyanSettings_MasksAndSavesAtBothScales));

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Ursus, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    public Task Tracking_StageSelection(GameShopTheme theme, bool night)
        => ui.RunAsync(() => TrackingStageViewTests.Verify(theme, night), $"{nameof(Tracking_StageSelection)}-{theme}-{night}");

    [Fact]
    public Task Tracking_RendersAcrossThemesAndWidths() => ui.RunAsync(TrackingWorkflowViewTests.Verify, nameof(Tracking_RendersAcrossThemesAndWidths));

    [Fact]
    public Task Tracking_WorkflowUpdatesTargets() => ui.RunAsync(TrackingWorkflowViewTests.VerifyWorkflow, nameof(Tracking_WorkflowUpdatesTargets));

    [Fact]
    public Task Tracking_EquipmentAndGroups() => ui.RunAsync(TrackingWorkflowViewTests.VerifyEquipmentAndGroups, nameof(Tracking_EquipmentAndGroups));

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    public Task EquipmentCompletion_KeepsEquipmentAcrossReload(GameShopTheme theme, bool night)
        => ui.RunAsync(() => EquipmentCompletionViewTests.Verify(theme, night), $"{nameof(EquipmentCompletion_KeepsEquipmentAcrossReload)}-{theme}-{night}");

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    public Task OperatorCatalogue_ShowsSupportAndSelectedCustom(GameShopTheme theme, bool night)
        => ui.RunAsync(() => OperatorCatalogueViewTests.Verify(theme, night), $"{nameof(OperatorCatalogue_ShowsSupportAndSelectedCustom)}-{theme}-{night}");

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    public Task CoreBan_UpdatesCodexAndPreservesManualBan(GameShopTheme theme, bool night)
        => ui.RunAsync(() => CoreBanViewTests.Verify(theme, night), $"{nameof(CoreBan_UpdatesCodexAndPreservesManualBan)}-{theme}-{night}");

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    public Task PlannerLockedBan_PausesAndRestoresAcrossThemes(GameShopTheme theme, bool night)
        => ui.RunAsync(() => PlannerLockedBanViewTests.Verify(theme, night), $"{nameof(PlannerLockedBan_PausesAndRestoresAcrossThemes)}-{theme}-{night}");

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    public Task RecognitionHotkey_SettingsAndDispatch(GameShopTheme theme, bool night)
        => ui.RunAsync(() => RecognitionHotkeyViewTests.Verify(theme, night), $"{nameof(RecognitionHotkey_SettingsAndDispatch)}-{theme}-{night}");

    [Fact]
    public Task Codex_PreservesFiltersAndNavigation() => ui.RunAsync(ClearAndLineupSourceViewTests.VerifyCodex, nameof(Codex_PreservesFiltersAndNavigation));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Codex_ReferencePages(bool night) => ui.RunAsync(() => ClearAndLineupSourceViewTests.VerifyReferenceCodex(night), $"{nameof(Codex_ReferencePages)}-{night}");

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Terminal, true)]
    [InlineData(GameShopTheme.Ursus, false)]
    [InlineData(GameShopTheme.Ursus, true)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    public Task RunInfo_BansAndEnemyRemainUsable(GameShopTheme theme, bool night)
        => ui.RunAsync(() => RunInfoViewTests.Verify(theme, night), $"{nameof(RunInfo_BansAndEnemyRemainUsable)}-{theme}-{night}");

    [Fact]
    public Task ClearInputs_PreserveLineupSources() => ui.RunAsync(ClearAndLineupSourceViewTests.Verify, nameof(ClearInputs_PreserveLineupSources));

    [Fact]
    public Task SklandImport_ReviewsAndSavesStages() => ui.RunAsync(SklandImportViewTests.Verify, nameof(SklandImport_ReviewsAndSavesStages));

    [Fact]
    public Task SklandBrowse_FiltersSwitchesStagesAndImports() => ui.RunAsync(SklandImportViewTests.VerifyBrowse, nameof(SklandBrowse_FiltersSwitchesStagesAndImports));

    [Fact]
    public Task LocalNameSearch_CompactInputsAndKeyboardSelection() =>
        ui.RunAsync(SklandImportViewTests.VerifyLocalNameSearch, nameof(LocalNameSearch_CompactInputsAndKeyboardSelection));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SklandPublish_ShowsStagesAndBlocksDisconnectedAccount(bool night) =>
        ui.RunAsync(() => SklandImportViewTests.VerifyPublishPreview(night), $"SklandPublishPreview-{night}");

    [Fact]
    public Task UpdateDialog_ReportsDownloadProgress() => ui.RunAsync(() =>
    {
        const string invite = "https://qm.qq.com/q/x5Z2uPdJRK";
        const string mirror = "https://mirrorchyan.com/zh/projects?rid=hongxue-stronghold-companion";
        string notes = $"## 更新内容\n[点击加入 QQ 交流群（1104531618）]({invite})\n\n已有 Mirror酱 CDK 可以 [前往 Mirror酱 高速下载]({mirror})\n\n## 下载与使用\n安装说明";
        var info = new ASPAssistant.Core.Services.UpdateInfo(
            "v99.9.9", "99.9.9", notes, "https://example.test/a.zip", "a.zip", null);
        var opened = new List<string>();
        bool clientAvailable = true;
        var dialog = new UpdateDialog(info, uri =>
        {
            opened.Add(uri);
            if (!clientAvailable && uri.StartsWith("mqqapi:")) throw new System.ComponentModel.Win32Exception(1155);
        });
        var release = (TextBlock)dialog.FindName("ReleaseNotesText");
        var links = release.Inlines.OfType<System.Windows.Documents.Hyperlink>().ToArray();
        links.Select(link => link.NavigateUri.AbsoluteUri).Should().Equal(invite, mirror);
        release.Text.Should().NotContain("安装说明");
        opened.Should().BeEmpty();
        links[0].RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
        opened.Should().ContainSingle().Which.Should().Contain("uin=1104531618&card_type=group");
        clientAvailable = false;
        links[0].RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
        opened.Skip(1).Should().Equal(opened[0], invite);
        links[1].RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
        opened.Last().Should().Be(mirror);
        dialog.ReportProgress(0.42);
        var bar = (System.Windows.Controls.ProgressBar)dialog.FindName("DialogProgress");
        var percent = (TextBlock)dialog.FindName("ProgressPercentText");
        var hint = (TextBlock)dialog.FindName("DownloadHintText");
        bar.Value.Should().Be(42);
        percent.Text.Should().Be("42%");
        hint.Text.Should().StartWith("正在下载安装包");

        dialog.ReportTransfer(new(740_000, 1_360_000, 2_300_000));
        hint.Text.Should().Be("已下载 0.74 / 1.36 MB  ·  2.3 MB/s");
        bar.Value.Should().Be(54);
        dialog.ReportTransfer(new(740_000, 0, 840_000));
        hint.Text.Should().Be("已下载 0.74 MB  ·  840 KB/s");
        bar.IsIndeterminate.Should().BeTrue();
        percent.Visibility.Should().Be(Visibility.Collapsed);
        dialog.ReportProgress(0.87);
        bar.IsIndeterminate.Should().BeFalse();
        hint.Text.Should().Be("正在校验安装包…");
        dialog.ReportProgress(0.92);
        hint.Text.Should().Be("正在解压安装包…");

        dialog.ReportProgress(1.0, "MirrorChyan CDK 已过期");
        bar.Value.Should().Be(100);
        percent.Text.Should().Be("100%");
        hint.Text.Should().Contain("立即重启").And.Contain("CDK 已过期").And.Contain("GitHub Releases");
        var body = (FrameworkElement)dialog.Content;
        dialog.Content = null;
        var surface = new Border { Width = 320, Child = body };
        surface.SetResourceReference(Border.BackgroundProperty, "PanelBackgroundBrush");
        foreach (var night in new[] { false, true })
        {
            ThemeManager.Apply(GameShopTheme.Closure, night);
            dialog.ReportTransfer(new(740_000, 1_360_000, 2_300_000));
            surface.Measure(new Size(320, double.PositiveInfinity));
            surface.Arrange(new Rect(surface.DesiredSize));
            surface.UpdateLayout();
            Save(surface, Path.Combine(TestOutput.Directory, $"update-speed-Closure-{night}.png"));
            dialog.ReportProgress(1.0, "MirrorChyan CDK 已过期");
            surface.Measure(new Size(320, double.PositiveInfinity));
            surface.Arrange(new Rect(surface.DesiredSize));
            surface.UpdateLayout();
            hint.TransformToAncestor(surface).TransformBounds(new Rect(hint.RenderSize)).Bottom.Should().BeLessThan(surface.ActualHeight);
            Save(surface, Path.Combine(TestOutput.Directory, $"mirrorchyan-fallback-Closure-{night}.png"));
        }
        dialog.ReportFailure("更新源返回 HTTP 503");
        hint.Text.Should().Contain("下载失败").And.Contain("503").And.Contain("重试");
        bar.Visibility.Should().Be(Visibility.Collapsed);
        percent.Visibility.Should().Be(Visibility.Collapsed);
        dialog.ReportProgress(0);
        bar.Visibility.Should().Be(Visibility.Visible);
        percent.Visibility.Should().Be(Visibility.Visible);
        bar.Value.Should().Be(0);
        percent.Text.Should().Be("0%");
        hint.Text.Should().StartWith("正在下载安装包");
        dialog.Close();
    }, nameof(UpdateDialog_ReportsDownloadProgress));

    [Fact]
    public Task ThemeSwitch_UpdatesControlsAndPreservesHighlightAndSelection() => ui.RunAsync(() =>
    {
        var vm = new OperatorBrowseViewModel();
        vm.LoadOperators([
            new() { Name = "同盟甲", Tier = 2, CoreCovenants = ["炎"], AdditionalCovenants = ["灵巧"] },
            new() { Name = "同盟乙", Tier = 3, CoreCovenants = ["炎"], AdditionalCovenants = ["灵巧"] },
            new() { Name = "异盟", Tier = 2, CoreCovenants = ["谢拉格"], AdditionalCovenants = ["坚守"] }
        ]);
        vm.SetAvailableCovenants(["炎", "灵巧"]);
        var view = new OperatorBrowseView { DataContext = vm };
        var host = new Window { Content = view, Width = 360, Height = 720, Opacity = 0,
            ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        host.SetResourceReference(Window.BackgroundProperty, "PanelBackgroundBrush");
        host.Show();
        var filter = (System.Windows.Controls.Primitives.ToggleButton)view.FindName("FilterToggle");
        filter.IsChecked = true;
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var selected = vm.CoreChips.Single(c => c.Value == "炎");
        var tier = vm.TierChips.Single(c => c.Value == "2");
        var coreControl = Descendants<System.Windows.Controls.Primitives.ToggleButton>(view).Single(c => ReferenceEquals(c.DataContext, selected));
        var tierControl = Descendants<System.Windows.Controls.Primitives.ToggleButton>(view).Single(c => ReferenceEquals(c.DataContext, tier));
        coreControl.IsChecked = true;
        tierControl.IsChecked = true;
        ((TextBox)view.FindName("SearchBox")).Text = "同盟";
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        vm.FilteredOperators.Select(o => o.Name).Should().Equal("同盟甲");
        var activeCount = vm.ActiveFilterCount;
        var dialog = new ConfirmDialog("主题切换验证", "切换日间、夜间及主题后，正文与操作文字应保持清晰。")
        { Owner = host, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        dialog.Show();
        var output = Path.Combine(TestOutput.Directory, "theme-switch");
        Directory.CreateDirectory(output);
        var backgrounds = new HashSet<Color>();
        foreach (var (theme, night) in new[] {
            (GameShopTheme.Terminal, false), (GameShopTheme.Ursus, false), (GameShopTheme.Closure, false),
            (GameShopTheme.Closure, true), (GameShopTheme.Ursus, true), (GameShopTheme.Terminal, true),
            (GameShopTheme.Terminal, false) })
        {
            ThemeManager.Apply(theme, night);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            view.UpdateLayout(); dialog.UpdateLayout();
            var expected = new ResourceDictionary { Source = new Uri(
                $"/HongxueCompanion;component/Resources/Themes/{theme}.{(night ? "Dark" : "Light")}.xaml", UriKind.Relative) };
            Color ColorOf(string key) => ((SolidColorBrush)expected[key]).Color;
            ((SolidColorBrush)view.Background).Color.Should().Be(ColorOf("PanelBackgroundBrush"));
            backgrounds.Add(((SolidColorBrush)view.Background).Color);
            ((SolidColorBrush)((TextBox)view.FindName("SearchBox")).Foreground).Color.Should().Be(ColorOf("TextPrimaryBrush"));
            view.DataContext.Should().BeSameAs(vm);
            vm.CoreChips.Single(c => c.Value == "炎").Should().BeSameAs(selected);
            vm.SelectedTierFilters.Should().Equal(2);
            vm.CoreChips.Where(c => c.IsSelected).Select(c => c.Value).Should().Equal("炎");
            vm.SearchText.Should().Be("同盟");
            vm.ActiveFilterCount.Should().Be(activeCount);
            vm.HighlightedCovenants.Should().BeEquivalentTo("炎", "灵巧");
            vm.FilteredOperators.Select(o => o.Name).Should().Equal("同盟甲");
            filter.IsChecked.Should().BeTrue();
            coreControl.IsChecked.Should().BeTrue(); tierControl.IsChecked.Should().BeTrue();
            selected.IsHighlighted.Should().BeTrue();
            var coreBorder = (Border)coreControl.Template.FindName("bg", coreControl);
            ((SolidColorBrush)coreBorder.Background).Color.Should().Be(ColorOf("AccentSoftBrush"));
            ((SolidColorBrush)coreBorder.BorderBrush).Color.Should().Be(ColorOf("AccentBorderBrush"));
            ((SolidColorBrush)((TextBlock)coreControl.Template.FindName("label", coreControl)).Foreground).Color.Should().Be(ColorOf("AccentBrush"));
            var dot = (Border)coreControl.Template.FindName("dot", coreControl);
            dot.Visibility.Should().Be(Visibility.Visible);
            ((SolidColorBrush)dot.Background).Color.Should().Be(ColorOf("FinalBrush"));
            ((SolidColorBrush)((Border)tierControl.Template.FindName("bg", tierControl)).Background).Color.Should().Be(ColorOf("AccentSolidBrush"));
            ((SolidColorBrush)((TextBlock)tierControl.Template.FindName("label", tierControl)).Foreground).Color.Should().Be(ColorOf("OnAccentBrush"));
            ((SolidColorBrush)dialog.Background).Color.Should().Be(ColorOf("PanelBackgroundBrush"));
            ((SolidColorBrush)((TextBlock)dialog.FindName("MessageText")).Foreground).Color.Should().Be(ColorOf("TextPrimaryBrush"));
            foreach (var (name, window) in new[] { ("catalogue", host), ("dialog", (Window)dialog) })
            {
                var content = (FrameworkElement)window.Content;
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen())
                {
                    context.DrawRectangle(window.Background, null, new Rect(content.RenderSize));
                    context.DrawRectangle(new VisualBrush(content), null, new Rect(content.RenderSize));
                }
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"{theme}-{night}-{name}.png")); encoder.Save(file);
            }
        }
        backgrounds.Should().HaveCount(6, "all six palettes must actually reach the existing controls");
        vm.ClearCovenantHighlights();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        ((Border)coreControl.Template.FindName("dot", coreControl)).Visibility.Should().Be(Visibility.Collapsed);
        coreControl.IsChecked.Should().BeTrue();
        coreControl.IsChecked = false; tierControl.IsChecked = false;
        ((TextBox)view.FindName("SearchBox")).Text = "";
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        vm.FilteredOperators.Should().HaveCount(3);
        dialog.Close(); host.Close();
    }, nameof(ThemeSwitch_UpdatesControlsAndPreservesHighlightAndSelection));

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) yield return result;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    [Theory]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    public Task SklandCompleteEditor(GameShopTheme theme, bool night)
        => ui.RunAsync(() => SklandCompleteEditorViewTests.Verify(theme, night), nameof(SklandCompleteEditor));

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    public Task AutoRecommend(GameShopTheme theme, bool night)
        => ui.RunAsync(() => AutoPlanViewTests.Verify(theme, night), $"AutoRecommend-{theme}-{night}");

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    public Task GameStartSettingsAndScaleRestore(GameShopTheme theme, bool night)
        => ui.RunAsync(() => GameStartSettingsViewTests.Verify(theme, night), $"GameStartSettings-{theme}-{night}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AutomaticCurrentRunFilterSetting_PersistsAndAppliesOnlyOnRecognition(bool night)
        => ui.RunAsync(() => GameStartSettingsViewTests.VerifyCurrentRunFilterPreference(night), $"CurrentRunFilterSetting-{night}");

    [Theory]
    [InlineData(GameShopTheme.Terminal, false)]
    [InlineData(GameShopTheme.Closure, false)]
    [InlineData(GameShopTheme.Closure, true)]
    public Task StageRepairPreview(GameShopTheme theme, bool night)
        => ui.RunAsync(() => StageRepairViewTests.Verify(theme, night), $"StageRepairPreview-{theme}-{night}");

    [Fact]
    public Task Issue32_DesktopAssociationDoesNotWaitForAdb()
        => ui.RunAsync(Issue32ViewTests.VerifyWindowAssociation, nameof(Issue32_DesktopAssociationDoesNotWaitForAdb));

    private static void Save(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),
            (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
