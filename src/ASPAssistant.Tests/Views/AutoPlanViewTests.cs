using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ASPAssistant.App;
using ASPAssistant.App.Views;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

/// <summary>规划 tab, 自动 page: candidate cards and the route with 抓位 / 升本 advice, on the real season data.</summary>
internal static class AutoPlanViewTests
{
    public static void Verify()
    {
        var data = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "TestData", "Regulation"));
        var operators = data.LoadOperatorsAsync().GetAwaiter().GetResult();
        var (covenants, _) = data.LoadCovenantsAsync().GetAwaiter().GetResult();
        var vm = new LineupPlannerViewModel(new LineupPlanner(operators, covenants), operators, new BanViewModel(), EquipmentTestData.Rules)
        {
            DebounceMilliseconds = 0, CurrentTerminalLevel = 3
        };
        // As in the app: the planning continuations come back to the dispatcher thread that owns the bound collections.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        vm.IsActive = true;
        vm.ShowAutoPageCommand.Execute(null);
        Complete(vm.PendingAutoPlan!);
        vm.AutoPlans.Should().HaveCount(10);

        foreach (var night in new[] { true, false })
        foreach (var width in new[] { 300, 380 })
            Render(vm, night, width, $"list-{(night ? "dark" : "light")}-{width}", view =>
            {
                ((Button)view.FindName("AutoTabButton")).Tag.Should().Be("Active");
                ((Button)view.FindName("GoalTabButton")).Tag.Should().NotBe("Active");
                var list = (ItemsControl)view.FindName("AutoPlanList");
                list.IsVisible.Should().BeTrue();
                list.Items.Count.Should().Be(10);
                ((TextBlock)view.FindName("AutoStatusLine")).Text.Should().Contain("10 个主盟约");
                ((TextBlock)view.FindName("StatusLine")).IsVisible.Should().BeFalse("the target search footer is hidden on the 自动 page");
                var styles = Descendants<Button>(list).Where(b => b.DataContext is AutoStyleChoice).ToList();
                styles.Should().HaveCount(20);
                // Both style buttons of a card fit side by side without clipping their label.
                styles.Should().OnlyContain(b => b.IsVisible && b.ActualWidth >= 90);
                styles.Count(b => ((AutoStyleChoice)b.DataContext).IsRecommended).Should().Be(10);
                ((ItemsControl)view.FindName("RouteStagesList")).IsVisible.Should().BeFalse();
            });

        var plan = vm.AutoPlans.First(p => p.Source.RecommendedStyle == AutoPlanStyle.SmallFirst);
        foreach (var style in new[] { AutoPlanStyle.SmallFirst, AutoPlanStyle.CoreFirst })
        {
            Complete(vm.PlanAutoRouteAsync(plan.Styles.Single(s => s.Style == style)));
            vm.RouteStages.Should().HaveCount(6);
            // Terminal 3: 开局 is behind, 前期 is the current (open) stage.
            vm.RouteStages[0].IsPast.Should().BeTrue();
            var current = vm.RouteStages.Single(s => s.IsCurrent);
            current.Title.Should().Be("前期");
            foreach (var width in new[] { 300, 380 })
                Render(vm, true, width, $"route-{style}-{width}", view =>
                {
                    ((Button)view.FindName("AutoTabButton")).Tag.Should().Be("Active");
                    ((ItemsControl)view.FindName("AutoPlanList")).IsVisible.Should().BeFalse();
                    var stages = (ItemsControl)view.FindName("RouteStagesList");
                    stages.IsVisible.Should().BeTrue();
                    stages.Items.Count.Should().Be(6);
                    var texts = Descendants<TextBlock>(stages).Where(t => t.IsVisible).Select(t => t.Text).ToList();
                    // The open stage shows what it is for, the pace badge and the upgrade condition.
                    texts.Should().Contain(current.FocusText).And.Contain(current.PaceText).And.Contain(current.UpgradeText);
                    // …and when: its rounds in the header, their funds under the advice.
                    texts.Should().Contain(current.LevelText).And.Contain(current.FundsText);
                    current.LevelText.Should().StartWith("第 ").And.EndWith("终端 3");
                    if (current.HasUnlock) texts.Should().Contain(current.UnlockText);
                    if (current.HasReserve) texts.Should().Contain(current.ReserveTitle).And.Contain(current.Reserve[0].Name);
                    // Collapsed stages keep their advice out of the way.
                    texts.Should().NotContain(vm.RouteStages[^1].FocusText);
                });
        }
        vm.RouteStages.Take(3).Select(s => s.FocusText).Should().OnlyContain(f => f.StartsWith("凑本家"));

        // 抓位: open a stage that has cards to buy ahead (the stepper opens only that stage).
        var buyAhead = vm.RouteStages.First(s => s.HasReserve);
        vm.SelectRouteStepCommand.Execute(vm.RouteSteps.Single(s => ReferenceEquals(s.Stage, buyAhead)));
        Render(vm, true, 380, "route-reserve", view =>
        {
            var stages = (ItemsControl)view.FindName("RouteStagesList");
            var texts = Descendants<TextBlock>(stages).Where(t => t.IsVisible).Select(t => t.Text).ToList();
            texts.Should().Contain(buyAhead.ReserveTitle).And.Contain(buyAhead.FocusText);
            buyAhead.Reserve.Should().OnlyContain(r => texts.Contains(r.Name) && texts.Contains(r.Sub));
        });

        // Back to the list: the cards are still there.
        vm.CloseRouteCommand.Execute(null);
        Render(vm, true, 380, "list-after-route", view =>
        {
            ((ItemsControl)view.FindName("AutoPlanList")).IsVisible.Should().BeTrue();
            ((ItemsControl)view.FindName("RouteStagesList")).IsVisible.Should().BeFalse();
        });
        ThemeManager.Apply(GameShopTheme.Terminal, true);
    }

    private static void Render(LineupPlannerViewModel vm, bool night, int width, string name, Action<LineupPlannerView> verify)
    {
        ThemeManager.Apply(GameShopTheme.Terminal, night);
        var view = new LineupPlannerView { DataContext = vm };
        var host = new Window
        {
            Content = view, ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, Width = width, Height = 1400
        };
        try
        {
            host.Show();
            Pump();
            verify(view);
            Save(view, name);
        }
        finally
        {
            host.Close();
            view.DataContext = null;
        }
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
        var directory = Path.Combine(AppContext.BaseDirectory, "TestResults", "auto-plan-previews");
        Directory.CreateDirectory(directory);
        using var file = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(file);
    }
}
