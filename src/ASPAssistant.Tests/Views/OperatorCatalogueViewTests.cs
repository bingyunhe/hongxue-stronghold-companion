using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ASPAssistant.App;
using ASPAssistant.App.Controls;
using ASPAssistant.App.Views;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

internal static class OperatorCatalogueViewTests
{
    public static void Verify(GameShopTheme theme, bool night)
    {
        ThemeManager.Apply(theme, night);
        var store = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "data"));
        var operators = store.LoadOperatorsAsync().GetAwaiter().GetResult();
        var custom = store.LoadCustomOperatorsAsync().GetAwaiter().GetResult();
        operators.AddRange(custom);
        var pith = custom.Single(o => o.Name == "Pith(卫戍协议)");
        pith.IsSelectedCustom = true;
        var vm = new OperatorBrowseViewModel { SelectedOperatorSource = OperatorSourceFilter.Default, SearchText = PlannerStrategies.PithName };
        vm.LoadOperators(operators);
        var tracking = new TrackingViewModel();
        var view = new OperatorBrowseView { DataContext = vm, IsTrackedCheck = tracking.IsTracked };
        view.TrackingRequested += (name, type) => tracking.AddTracking(name, type);
        view.UntrackingRequested += tracking.RemoveTracking;
        var host = new Window { Content = view, Width = 360, Height = 850, Opacity = 0, ShowActivated = false,
            ShowInTaskbar = false, Left = -20000, Top = -20000, WindowStyle = WindowStyle.None };
        host.Show();
        try
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            view.UpdateLayout();
            var support = vm.FilteredOperators.Single();
            support.Name.Should().Be(PlannerStrategies.PithName);
            var card = Desc<OperatorCard>(view).Single(c => ((Operator)c.DataContext).Name == support.Name);
            ((Button)card.FindName("TrackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            tracking.IsTracked(support.Name).Should().BeTrue();
            Save(view, theme, night, "support");
            var details = new ContentControl { Content = support,
                ContentTemplate = (DataTemplate)Application.Current.FindResource("OperatorDetailsTemplate") };
            var tooltip = new ToolTip { Content = details, DataContext = support };
            tooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            tooltip.Arrange(new Rect(tooltip.DesiredSize));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            details.UpdateLayout();
            details.ActualWidth.Should().BeGreaterThanOrEqualTo(464);
            Desc<TextBlock>(details).Select(t => t.Text).Should().Contain(support.Name);
            Desc<TextBlock>(details).SelectMany(t => t.Inlines.OfType<Run>()).Select(r => r.Text)
                .Should().Contain("战术咏唱·双型");
            Save(details, theme, night, "support-details");

            ((ListBox)view.FindName("OperatorSourceList")).SelectedValue = OperatorSourceFilter.Custom;
            ((TextBox)view.FindName("SearchBox")).Text = "Pith";
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            view.UpdateLayout();
            vm.FilteredOperators.Should().Equal(pith);
            Desc<OperatorCard>(view).Should().Contain(c => ((Operator)c.DataContext).Name == pith.Name);
            pith.AdditionalCovenants.Should().Equal("协防干员");
            pith.Normal.Skill.Should().BeNull();
            Save(view, theme, night, "custom-pith");
        }
        finally { host.Close(); }
    }

    public static void VerifyLayouts(bool night)
    {
        ThemeManager.Apply(GameShopTheme.Closure, night);
        var vm = new OperatorBrowseViewModel { SelectedOperatorSource = OperatorSourceFilter.Default };
        var operators = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "data")).LoadOperatorsAsync().GetAwaiter().GetResult();
        vm.LoadOperators(operators);
        var view = new OperatorBrowseView { DataContext = vm };
        string? tracked = null, banned = null;
        view.TrackingRequested += (name, _) => tracked = name;
        view.BanToggleRequested += (name, _) => banned = name;
        var host = new Window { Content = view, Width = 400, Height = 580, WindowStyle = WindowStyle.None,
            ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        var button = (Button)view.FindName("LayoutButton");
        void Layout() { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); view.UpdateLayout(); }
        void Cycle() { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(); }
        try
        {
            host.Show();
            for (int columns = 1; columns <= 3; columns++)
            {
                Layout();
                var cards = Desc<OperatorCard>(view).ToList();
                Assert.InRange(cards.Count, columns, vm.MatchedCount - 1);
                var first = cards[0];
                var location = first.TranslatePoint(new Point(), view);
                for (int i = 0; i < columns; i++)
                {
                    Assert.Equal(columns, cards[i].CatalogColumns);
                    Assert.Equal(location.Y, cards[i].TranslatePoint(new Point(), view).Y);
                    if (i > 0) Assert.True(cards[i].TranslatePoint(new Point(), view).X > location.X);
                }
                Assert.True(cards[columns].TranslatePoint(new Point(), view).Y > location.Y);
                Assert.Equal(columns == 3 ? Visibility.Collapsed : Visibility.Visible,
                    ((ItemsControl)first.FindName("CovenantChips")).Visibility);
                Save(view, GameShopTheme.Closure, night, $"layout-{columns}");
                if (columns < 3) Cycle();
            }
            var items = (ItemsControl)view.FindName("OperatorListControl");
            var scroll = Desc<ScrollViewer>(items).First();
            scroll.ScrollToVerticalOffset(650); Layout();
            for (int step = 0; step < 3; step++)
            {
                var anchor = Desc<OperatorCard>(items).First(c =>
                {
                    var y = c.TranslatePoint(new Point(), scroll).Y;
                    return y + c.ActualHeight > 0 && y < scroll.ViewportHeight;
                });
                var op = anchor.DataContext;
                var before = anchor.TranslatePoint(new Point(), scroll).Y;
                Cycle();
                var after = Desc<OperatorCard>(items).Single(c => ReferenceEquals(c.DataContext, op))
                    .TranslatePoint(new Point(), scroll).Y;
                Assert.InRange(Math.Abs(after - before), 0, 1);
            }
            vm.SearchText = "纯烬艾雅法拉"; Layout();
            var selected = Desc<OperatorCard>(view).Single();
            foreach (string name in new[] {"TrackButton", "BanButton"})
                ((Button)selected.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(vm.SearchText, tracked); Assert.Equal(vm.SearchText, banned);
            host.Width = 280; Layout();
            Save(view, GameShopTheme.Closure, night, "compact-narrow");
            Cycle();
            Assert.Equal("纯烬艾雅法拉", vm.SearchText);
            Assert.Equal(1, Desc<OperatorCard>(view).Single().CatalogColumns);
        }
        finally { host.Close(); }
    }

    private static IEnumerable<T> Desc<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var nested in Desc<T>(child)) yield return nested;
        }
    }

    private static void Save(FrameworkElement view, GameShopTheme theme, bool night, string suffix)
    {
        var directory = Path.Combine(TestOutput.Directory, "operator-catalogue");
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, $"{theme}-{night}-{suffix}.png"));
        png.Save(file);
    }
}
