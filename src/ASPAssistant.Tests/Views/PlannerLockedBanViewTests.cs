using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ASPAssistant.App;
using ASPAssistant.App.Views;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

internal static class PlannerLockedBanViewTests
{
    public static void Verify(GameShopTheme theme, bool night)
    {
        Operator[] operators = [
            new() { Name = "手动禁用必带", CoreCovenants = ["维多利亚"] },
            new() { Name = "盟约禁用必带", CoreCovenants = ["炎"] },
            new() { Name = "正常必带", CoreCovenants = ["维多利亚"] }];
        CovenantInfo[] covenants = [
            new() { Name = "炎", BondType = "SEASON", ActivateCount = 1 },
            new() { Name = "维多利亚", BondType = "SEASON", ActivateCount = 1 }];
        var bans = new BanViewModel();
        var vm = new LineupPlannerViewModel(new LineupPlanner(operators, covenants), operators, bans, EquipmentTestData.Rules);
        foreach (var op in operators) vm.AddLocked(op.Name);
        var view = new LineupPlannerView { DataContext = vm };
        var host = new Window { Content = view, Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
            Left = -20000, Top = -20000, Height = 850 };
        try
        {
            host.Show();
            ThemeManager.Apply(theme, night);
            var list = (ItemsControl)view.FindName("LockedOperatorsList");
            foreach (var width in new[] { 280, 360, 400 })
            {
                host.Width = width;
                vm.ShowResults = false;
                bans.SetBans(["手动禁用必带"]);
                bans.SetCovenantBans(["炎"]);
                Pump();
                list.Items.Count.Should().Be(3);
                foreach (var item in vm.LockedOperators)
                {
                    var chip = Desc<Border>(list).Single(b => ReferenceEquals(b.Tag, item.Operator));
                    chip.Opacity.Should().Be(item.Name == "正常必带" ? 1 : 0.55);
                    var marker = Desc<TextBlock>(chip).Single(t => t.Text == "已 Ban");
                    marker.Visibility.Should().Be(item.Name == "正常必带" ? Visibility.Collapsed : Visibility.Visible);
                    marker.ToolTip.Should().Be("本局暂不计入必带，解除 Ban 后自动恢复");
                    var bounds = chip.TransformToAncestor(list).TransformBounds(new Rect(0, 0, chip.ActualWidth, chip.ActualHeight));
                    bounds.Right.Should().BeLessThanOrEqualTo(list.ActualWidth + 0.5);
                }
                Save(view, theme, night, width, "banned");
                vm.ShowResults = true;
                Pump();
                var count = (TextBlock)view.FindName("ActiveLockedCountText");
                count.IsVisible.Should().BeTrue();
                count.Inlines.OfType<Run>().Last().Text.Should().Be("1");
                Save(view, theme, night, width, "count");

                bans.ClearBans();
                Pump();
                count.Inlines.OfType<Run>().Last().Text.Should().Be("3");
                vm.ShowResults = false;
                Pump();
                Desc<Border>(list).Where(b => b.Tag is Operator).Should().OnlyContain(b => b.Opacity == 1);
                Desc<TextBlock>(list).Where(t => t.Text == "已 Ban").Should().OnlyContain(t => t.Visibility == Visibility.Collapsed);
                Save(view, theme, night, width, "restored");
            }

            bans.SetBans(["手动禁用必带"]);
            Pump();
            var paused = Desc<Border>(list).Single(b => ReferenceEquals(b.Tag, operators[0]));
            var remove = Desc<Button>(paused).Single();
            remove.IsEnabled.Should().BeTrue();
            remove.Command.Execute(remove.CommandParameter);
            Pump();
            vm.LockedOperators.Select(item => item.Name).Should().Equal("盟约禁用必带", "正常必带");
            vm.ActiveLockedCount.Should().Be(2);
        }
        finally { host.Close(); }
    }

    public static void VerifyManualSubstitution(GameShopTheme theme, bool night)
    {
        var previousContext = SynchronizationContext.Current;
        var ops = Enumerable.Range(1, 12).Select(i => new Operator
        {
            Name = "候选干员" + i, Tier = i % 3 + 1, CoreCovenants = ["炎"], AdditionalCovenants = i % 2 == 0 ? ["精准"] : []
        }).ToArray();
        CovenantInfo[] covenants = [new() { Name = "炎", BondType = "SEASON", ActivateCount = 3 }, new() { Name = "精准", ActivateCount = 2 }];
        var vm = new LineupPlannerViewModel(new(ops, covenants), ops, new BanViewModel(), EquipmentTestData.Rules)
            { DebounceMilliseconds = 0, IsActive = true };
        var view = new LineupPlannerView { DataContext = vm };
        var host = new Window { Content = view, Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
            Left = -20000, Top = -20000, Height = 850, Width = 320 };
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(host.Dispatcher));
            host.Show();
            ThemeManager.Apply(theme, night);
            vm.AllCovenants.Single(c => c.Name == "炎").ToggleLevel1Command.Execute(null);
            vm.ShowResultPageCommand.Execute(null);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (vm.PendingSearch?.IsCompleted == false && DateTime.UtcNow < deadline) Pump();
            Pump();
            vm.VisibleLineups.Should().NotBeEmpty($"{vm.StatusText} {vm.StatusDetail}");
            var card = vm.VisibleLineups[0];
            card.HasSubstitutionAdvice.Should().BeTrue();
            var expander = Desc<Expander>(view).Single(e => e.IsVisible && ReferenceEquals(e.DataContext, card)
                && e.Header is string header && header.Contains("缺一名"));
            expander.IsExpanded = true;
            expander.BringIntoView();
            Pump();
            var selector = Desc<ComboBox>(expander).Single();
            selector.Items.Count.Should().BeGreaterThan(1);
            selector.SelectedItem = card.SubstitutionOptions.Last(c => c.Swap.Changes.Count > 0);
            Pump();
            selector.SelectedItem.Should().Be(card.SelectedSubstitution);
            Desc<TextBlock>(expander).Should().Contain(t => t.Text == card.SelectedSubstitution!.Changes);
            var text = Desc<TextBlock>(expander).Single(t => t.Text == card.SelectedSubstitution!.Advice);
            text.Text.Should().Contain("决定是否换回");
            text.IsVisible.Should().BeTrue();
            text.ActualWidth.Should().BeGreaterThan(100).And.BeLessThan(host.Width);
            text.BringIntoView();
            Pump();
            Save(view, theme, night, 320, "manual-substitution");
            vm.SetAutoRunState(new(6, null, ops.Select(o => o.Name).ToHashSet()));
            Pump();
            card.HasSubstitutionAdvice.Should().BeFalse();
            expander.Visibility.Should().Be(Visibility.Collapsed);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            host.Close();
        }
    }

    public static void VerifyPrecisionTransfer(bool night)
    {
        var data = new ASPAssistant.Core.Data.JsonDataStore(Path.Combine(AppContext.BaseDirectory, "TestData", "Regulation"));
        var ops = Task.Run(() => data.LoadOperatorsAsync()).GetAwaiter().GetResult()
            .Where(o => new[] { "迷迭香", "角峰", "烈夏", "折桠", "初雪", "蛇屠箱", "砾", "古米" }.Contains(o.Name)
                && (o.Name == "迷迭香" || !o.AdditionalCovenants.Contains("精准"))).ToArray();
        var (covenants, mappings) = Task.Run(() => data.LoadCovenantsAsync()).GetAwaiter().GetResult();
        var bans = new BanViewModel();
        var vm = new LineupPlannerViewModel(new(ops, covenants), ops, bans, new(mappings))
            { DebounceMilliseconds = 0, IsActive = true, Strategy = PlannerStrategy.Shapeshifter };
        var view = new LineupPlannerView { DataContext = vm };
        var host = new Window { Content = view, Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
            Left = -20000, Top = -20000, Height = 900, Width = 470 };
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(host.Dispatcher));
            host.Show();
            ThemeManager.Apply(GameShopTheme.Closure, night);
            bans.SetCovenantBans(["精准"]);
            var target = vm.AllCovenants.Single(c => c.Name == "精准");
            target.AvailableMembers.Should().Be(1);
            target.ToggleLevel1Command.Execute(null);
            Pump();
            vm.TransferGoalDetail.Should().Contain("迷迭香").And.Contain("还需 1 名转职");
            var detail = Desc<TextBlock>(view).Single(t => t.IsVisible && t.Text == vm.TransferGoalDetail);
            detail.BringIntoView();
            Pump();
            Save(view, GameShopTheme.Closure, night, 470, "precision-goal");
            vm.ShowResultPageCommand.Execute(null);
            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (vm.PendingSearch?.IsCompleted == false && DateTime.UtcNow < deadline) Pump();
            Pump();
            vm.SelectedVariant!.Transforms.Should().Be(1);
            vm.SelectedVariant = vm.VariantCells.Single(v => v.People == vm.SelectedVariant.People && v.Transforms == 3);
            var first = vm.VisibleLineups.First();
            first.Source.Transforms.Should().Be(1);
            Pump();
            first.EquipmentText.Should().Contain("变形同构体 ×1 + 精准狙击镜 ×1");
            var gear = Desc<TextBlock>(view).First(t => t.Text == first.EquipmentText && t.IsVisible);
            gear.BringIntoView();
            Pump();
            Save(view, GameShopTheme.Closure, night, 470, "precision-result");
            target.ToggleLevel2Command.Execute(null);
            while (vm.PendingSearch?.IsCompleted == false && DateTime.UtcNow < deadline) Pump();
            var tierTwo = vm.VisibleLineups.First().Source;
            tierTwo.Transforms.Should().Be(2);
            tierTwo.Operators.Where(o => o.TransformedCovenant == "精准").Select(o => o.Name)
                .Should().HaveCount(2).And.OnlyHaveUniqueItems();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); host.Close(); }
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static IEnumerable<T> Desc<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var nested in Desc<T>(child)) yield return nested;
        }
    }

    private static void Save(FrameworkElement view, GameShopTheme theme, bool night, int width, string state)
    {
        var folder = Path.Combine(TestOutput.Directory, "planner-locked-ban");
        Directory.CreateDirectory(folder);
        var image = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(folder, $"{theme}-{night}-{width}-{state}.png"));
        encoder.Save(stream);
    }
}
