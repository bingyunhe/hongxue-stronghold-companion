using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Reflection;
using ASPAssistant.App;
using ASPAssistant.App.Views;
using ASPAssistant.App.Windows;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

internal static class OperatorPickerTests
{
    internal static void VerifyLiveCorrection(bool night)
    {
        ThemeManager.Apply(GameShopTheme.Closure, night);
        var data = Path.Combine(AppContext.BaseDirectory, "TestData", "Regulation");
        var operators = new JsonDataStore(data).LoadOperatorsAsync().GetAwaiter().GetResult()
            .GroupBy(o => o.Name).ToDictionary(g => g.Key, g => g.First());
        var deploy = new DeployView();
        deploy.Initialize(operators, [], GarrisonBoardStore.LoadAsync(data).GetAwaiter().GetResult(), new TrackingViewModel(),
            new SettingsManager(Path.Combine(Path.GetTempPath(), "live-picker-tests")), new DeployPoolState(), false);
        var area = SystemParameters.WorkArea;
        var owner = new Window { Content = deploy, Width = 280, Height = 500, Left = area.Right - 280, Top = area.Top,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        owner.Show();
        var mark = new FieldMark(new(2, 1), 4, default, [], null);
        var tile = (Border)typeof(DeployView).GetMethod("LiveTile", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(deploy, [new LiveUnit(mark, 1, null, null, false, false)])!;
        var change = tile.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "更换干员并记住"));
        Exception? failure = null;
        try
        {
            owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                var picker = Assert.Single(owner.OwnedWindows.Cast<Window>().OfType<OperatorPickerWindow>());
                try
                {
                    Assert.InRange(picker.Left + picker.ActualWidth, area.Left, area.Right + 1);
                    var filters = (OperatorBrowseViewModel)picker.DataContext;
                    filters.TierChips.Single(c => c.Value == "4").IsSelected = true;
                    filters.SearchText = "夜莺";
                    picker.UpdateLayout();
                    var list = (ListBox)picker.FindName("OperatorsList");
                    var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                    Assert.Equal("夜莺", ((Operator)row.Content).Name);
                    var surface = (FrameworkElement)picker.Content;
                    var view = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    view.Render(surface);
                    var png = new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(view));
                    using var output = File.Create(Path.Combine(TestOutput.Directory, $"live-correction-Closure-{night}.png"));
                    png.Save(output);
                    RaiseMouse(row, Mouse.PreviewMouseDownEvent);
                    RaiseMouse(row, Mouse.MouseUpEvent);
                    Assert.Equal("夜莺", picker.Result);
                }
                catch (Exception ex) { failure = ex; }
                finally { if (picker.IsVisible) picker.Close(); }
            });
            change.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Null(failure);
            Assert.Contains("已移动", ((TextBlock)deploy.FindName("LiveMessage")).Text);
        }
        finally { owner.Close(); }
    }

    public static void VerifySelection()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
        try
        {
            var operators = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "TestData", "Regulation"))
                .LoadOperatorsAsync().GetAwaiter().GetResult();
            foreach (var name in new[] { "褐果", "拉普兰德", "夏栎", "安哲拉", "复奏" })
            {
                var picker = new OperatorPickerWindow(operators, "", new HashSet<string>())
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
                    ShowActivated = false, ShowInTaskbar = false, Opacity = 0
                };
                try
                {
                    var list = (ListBox)picker.FindName("OperatorsList");
                    list.Height = 261;
                    picker.Show();
                    var content = (FrameworkElement)picker.Content;
                    content.Measure(new Size(778, 698));
                    content.Arrange(new Rect(0, 0, 778, 698));
                    content.UpdateLayout();
                    var scroll = Descendants<ScrollViewer>(list).First();
                    var index = list.Items.Cast<Operator>().ToList().FindIndex(op => op.Name == name);
                    list.ScrollIntoView(list.Items[index]);
                    Pump();
                    var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
                    row.BringIntoView();
                    Pump();
                    var point = row.TranslatePoint(new Point(100, 10), list);
                    var hit = VisualTreeHelper.HitTest(list, point).VisualHit;
                    ((Operator)((ListBoxItem)ItemsControl.ContainerFromElement(list, hit)).Content).Name.Should().Be(name);
                    RaiseMouse((UIElement)hit, Mouse.PreviewMouseDownEvent);
                    var pressedOffset = scroll.VerticalOffset;

                    // Scroll one row between press and release; row height changes with the picker layout.
                    list.SelectedItem = row.Content;
                    row.BringIntoView();
                    scroll.ScrollToVerticalOffset(pressedOffset + row.ActualHeight + row.Margin.Top + row.Margin.Bottom);
                    Pump();
                    hit = VisualTreeHelper.HitTest(list, point).VisualHit;
                    ((Operator)((ListBoxItem)ItemsControl.ContainerFromElement(list, hit)).Content).Name.Should().Be(((Operator)list.Items[index + 2]).Name,
                        "the same screen point now hits the following grid row after scrolling");
                    RaiseMouse((UIElement)hit, Mouse.MouseUpEvent);
                    picker.Result.Should().Be(name, "scrolling between press and release must preserve the clicked operator");
                }
                finally { picker.Close(); }
            }

            foreach (var strategySelected in new[] { false, true })
            foreach (var useKeyboard in new[] { false, true })
            {
                var picker = new OperatorPickerWindow(operators, "",
                    strategySelected ? new HashSet<string> { "band_mech" } : new HashSet<string>());
                try
                {
                    var filters = (OperatorBrowseViewModel)picker.DataContext;
                    filters.SearchText = "暴躁铁皮";
                    var list = (ListBox)picker.FindName("OperatorsList");
                    var content = (FrameworkElement)picker.Content;
                    content.Measure(new Size(778, 698));
                    content.Arrange(new Rect(0, 0, 778, 698));
                    content.UpdateLayout();
                    var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                    if (useKeyboard)
                        PressEnter(list, content);
                    else
                    {
                        RaiseMouse(row, Mouse.PreviewMouseDownEvent);
                        RaiseMouse(row, Mouse.MouseUpEvent);
                    }
                    picker.Result.Should().Be(strategySelected ? LineupEquipmentService.MechName : null);
                }
                finally { picker.Close(); }
            }

            foreach (var useKeyboard in new[] { false, true })
            {
                var picker = new OperatorPickerWindow(operators, "", new HashSet<string>());
                try
                {
                    var filters = (OperatorBrowseViewModel)picker.DataContext;
                    filters.SearchText = "拉普兰德";
                    filters.SelectedTierFilters = [1];
                    var list = (ListBox)picker.FindName("OperatorsList");
                    var content = (FrameworkElement)picker.Content;
                    content.Measure(new Size(778, 698));
                    content.Arrange(new Rect(0, 0, 778, 698));
                    content.UpdateLayout();
                    list.Items.Cast<Operator>().Select(op => op.Name).Should().Equal("拉普兰德");
                    var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                    if (useKeyboard)
                    {
                        PressEnter(list, content);
                    }
                    else
                    {
                        // Releasing over a row without a row press (e.g. after dragging the scrollbar) is ignored.
                        RaiseMouse(row, Mouse.MouseUpEvent);
                        picker.Result.Should().BeNull();
                        RaiseMouse(Descendants<ScrollBar>(list).First(bar => bar.Orientation == Orientation.Vertical), Mouse.PreviewMouseDownEvent);
                        RaiseMouse(row, Mouse.MouseUpEvent);
                        picker.Result.Should().BeNull("a scrollbar press must not choose an operator");
                        RaiseMouse(row, Mouse.PreviewMouseDownEvent);
                        RaiseMouse(list, Mouse.MouseUpEvent);
                        picker.Result.Should().BeNull("releasing outside a row cancels the click");
                        RaiseMouse(row, Mouse.MouseUpEvent);
                        picker.Result.Should().BeNull("a cancelled press must not leak into a later release");
                        RaiseMouse(row, Mouse.PreviewMouseDownEvent);
                        RaiseMouse(row, Mouse.MouseUpEvent);
                    }
                    picker.Result.Should().Be("拉普兰德");
                }
                finally { picker.Close(); }
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static void RaiseMouse(UIElement source, RoutedEvent routedEvent)
        => source.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = routedEvent });

    private static void PressEnter(ListBox list, FrameworkElement content)
    {
        list.SelectedItem = list.Items[0];
        list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new PickerPresentationSource(content),
            Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) yield return result;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private sealed class PickerPresentationSource(Visual root) : PresentationSource
    {
        public override Visual RootVisual { get; set; } = root;
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget? GetCompositionTargetCore() => null;
    }
}
