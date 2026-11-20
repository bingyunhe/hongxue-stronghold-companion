using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ASPAssistant.App;
using ASPAssistant.App.Views;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.ViewModels;
using ASPAssistant.Tests.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

internal static class LineupStateFilterViewTests
{
    public static void Verify(GameShopTheme theme, bool night)
    {
        var (vm, bans) = LineupStateFilterTestData.Create();
        var view = new LineupListView { DataContext = vm };
        view.SetTracking(new TrackingViewModel());
        var host = new Window { Content = view, Height = 1000, Opacity = 0, ShowActivated = false,
            ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            host.Show(); ThemeManager.Apply(theme, night); Pump();
            var filter = (ListBox)view.FindName("AvailabilityFilterList");
            var cards = (ItemsControl)view.FindName("LineupItems");
            var recommendations = (ItemsControl)view.FindName("RecommendItems");
            var section = (FrameworkElement)view.FindName("RecommendSection");
            var more = (Button)view.FindName("RecommendMore");
            var search = (TextBox)view.FindName("SearchBox");
            var toggle = (ToggleButton)view.FindName("RecommendDetailsToggle");
            filter.Items.Cast<ListBoxItem>().Select(i => i.Content).Should().Equal("全部", "本局可用", "待确认", "不可用");
            foreach (var width in new[] { 280, 360, 400 })
            {
                host.Width = width;
                for (var index = 0; index < 4; index++)
                {
                    filter.SelectedIndex = index; Pump();
                    vm.SelectedAvailabilityFilter.Should().Be((LineupAvailabilityFilter)index);
                    foreach (var item in filter.Items.Cast<ListBoxItem>())
                    {
                        var bounds = item.TransformToAncestor(view).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
                        bounds.Right.Should().BeLessThanOrEqualTo(view.ActualWidth + 0.5);
                    }
                    if (index == 2)
                    {
                        section.IsVisible.Should().BeTrue();
                        cards.Items.Count.Should().Be(0);
                        recommendations.Items.Count.Should().Be(3);
                        more.Content.Should().Be("全部 5 套");
                        more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                        recommendations.Items.Count.Should().Be(5);
                        more.Content.Should().Be("收起");
                        Save(view, theme, night, width, "pending-expanded");
                        var source = recommendations.ItemsSource;
                        toggle.IsChecked = false; Pump();
                        recommendations.Visibility.Should().Be(Visibility.Collapsed);
                        ((FrameworkElement)view.FindName("RecommendDetails")).IsVisible.Should().BeFalse();
                        more.IsVisible.Should().BeFalse();
                        recommendations.ItemsSource.Should().BeSameAs(source, "collapse only hides existing cards, without recalculation");
                        Save(view, theme, night, width, "pending-collapsed");
                        search.Text = "需替补"; Pump();
                        toggle.IsChecked.Should().BeFalse("search refresh must retain the user's collapsed state");
                        search.Clear(); Pump();
                        toggle.IsChecked = true; Pump();
                        recommendations.IsVisible.Should().BeTrue();
                        recommendations.Items.Count.Should().Be(5);
                        more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                        recommendations.Items.Count.Should().Be(3);
                    }
                    else
                    {
                        section.IsVisible.Should().BeFalse();
                        recommendations.Items.Count.Should().Be(0);
                        cards.Items.Count.Should().Be(index == 0 ? 7 : 1);
                        if (index == 1) cards.Items.Cast<LineupCardItem>().Single().Lineup.Id.Should().Be("safe");
                        if (index == 3) cards.Items.Cast<LineupCardItem>().Single().Lineup.Id.Should().Be("broken");
                    }
                    Save(view, theme, night, width, index.ToString());
                }
            }

            host.Width = 360;
            Button ActionButton(string name) => (Button)view.FindName(name);
            void Click(string name) { ActionButton(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
            filter.SelectedIndex = 0; Pump();
            Click("BulkMode");
            ActionButton("BulkDelete").IsEnabled.Should().BeFalse();
            Click("BulkAll");
            view.SelectedVisible().Length.Should().Be(7);
            filter.SelectedIndex = 2; Pump();
            cards.Items.Count.Should().Be(5, "all matching pending lineups can be selected");
            view.SelectedVisible().Length.Should().Be(5);
            search.Text = "需替补"; Pump();
            view.SelectedVisible().Length.Should().Be(3);
            var presenter = (ContentPresenter)cards.ItemContainerGenerator.ContainerFromIndex(0);
            var check = (CheckBox)cards.ItemTemplate.FindName("BulkCheck", presenter);
            check.IsChecked.Should().BeTrue();
            check.IsChecked = false;
            check.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            view.SelectedVisible().Length.Should().Be(2);
            string[] requested = [];
            view.DeleteSelectedRequested += rows => requested = rows.Select(l => l.Id).ToArray();
            Click("BulkDelete");
            requested.Should().BeEquivalentTo(vm.FilteredLineups.Skip(1).Select(l => l.Id));
            Save(view, theme, night, 360, "bulk");
            search.Clear(); Pump();
            view.SelectedVisible().Length.Should().Be(2, "hidden selections must not return");
            host.Width = 280; Pump();
            var right = ActionButton("BulkMode").TransformToAncestor(view).TransformBounds(new Rect(ActionButton("BulkMode").RenderSize)).Right;
            right.Should().BeLessThanOrEqualTo(view.ActualWidth);
            Click("BulkCancel");
            view.SelectedVisible().Should().BeEmpty();
            ActionButton("BulkAll").IsVisible.Should().BeFalse();
            Click("BulkMode");
            search.Text = "没有匹配的阵容"; Pump();
            ActionButton("BulkAll").IsEnabled.Should().BeFalse();
            ActionButton("BulkDelete").IsEnabled.Should().BeFalse();
            Click("BulkCancel");
            filter.SelectedIndex = 2;
            search.Text = "安全"; Pump();
            recommendations.Items.Count.Should().Be(0);
            more.Visibility.Should().Be(Visibility.Collapsed);
            ((TextBlock)view.FindName("NoMatchHint")).IsVisible.Should().BeTrue();
            search.Text = "需替补"; Pump();
            recommendations.Items.Count.Should().Be(3);
            recommendations.Items.Cast<LineupCardItem>().Should().OnlyContain(c => c.CanRepair);
            search.Clear();
            vm.ToggleFilterCovenant("炎"); Pump();
            recommendations.Items.Count.Should().Be(2);
            recommendations.Items.Cast<LineupCardItem>().Should().OnlyContain(c => !c.CanRepair);
            vm.ClearFilters();
            view.ShowRecommendations(); Pump();
            filter.SelectedIndex.Should().Be(2);
            recommendations.Items.Count.Should().Be(3);

            bans.ClearBans();
            view.ShowRecommendations(); Pump();
            filter.SelectedIndex.Should().Be(1);
            cards.Items.Count.Should().Be(7);
            section.IsVisible.Should().BeFalse();
            bans.SetCovenantBans(["炎", "维多利亚", "乌萨斯", "谢拉格"]);
            view.ShowRecommendations(); Pump();
            filter.SelectedIndex.Should().Be(3);
            cards.Items.Count.Should().Be(7);
            vm.SetRunInfo(null, null); Pump();
            filter.SelectedIndex.Should().Be(0);
            filter.Items.Cast<ListBoxItem>().Skip(1).Should().OnlyContain(i => !i.IsEnabled);
            cards.Items.Count.Should().Be(7);
        }
        finally { host.Close(); }
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Save(FrameworkElement view, GameShopTheme theme, bool night, int width, string state)
    {
        var folder = Path.Combine(TestOutput.Directory, "lineup-state-filters");
        Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, $"{theme}-{night}-{width}-{state}.png"));
        encoder.Save(stream);
    }
}
