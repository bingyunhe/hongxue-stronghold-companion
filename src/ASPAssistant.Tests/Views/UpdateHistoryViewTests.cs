using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ASPAssistant.App;
using ASPAssistant.App.Windows;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

internal static class UpdateHistoryViewTests
{
    public static void Verify(GameShopTheme theme, bool night)
    {
        var appearance = new SidebarAppearance(); WindowUiScale.Initialize(appearance);
        ThemeManager.Apply(theme, night);
        var entries = UpdateHistory.Load(Path.Combine(AppContext.BaseDirectory, "data", "update-history.json"));
        entries.Should().HaveCount(23); entries[0].Version.Should().Be("v1.10.0"); entries[^1].Version.Should().Be("v1.0.0");
        entries.Single(entry => entry.Version == "v1.9.4").Content.Should().Contain("HongxueCompanion.exe").And.Contain("反馈与诊断").And.Contain("新增").And.Contain("修复").And.Contain("修改");
        entries.Single(entry => entry.Version == "v1.9.3").Content.Should().Contain("单阶段支持最大15人");
        entries.Should().OnlyContain(entry => !entry.Content.Contains("## 下载"));
        var directory = Path.Combine(TestOutput.Directory, "update-history"); Directory.CreateDirectory(directory);
        var window = new UpdateHistoryWindow(entries) { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); Pump();
            var list = (ItemsControl)window.FindName("HistoryItems");
            var expanders = Descendants<Expander>(list).ToArray();
            expanders.Should().HaveCount(23); expanders[0].IsExpanded.Should().BeTrue();
            expanders.Skip(1).Should().OnlyContain(item => !item.IsExpanded);
            Save(window, Path.Combine(directory, $"{theme}-{night}-latest.png"));
            var toggle = Descendants<ToggleButton>(expanders[0]).First();
            toggle.IsChecked = false; Pump(); expanders[0].IsExpanded.Should().BeFalse();
            Descendants<ToggleButton>(expanders[1]).First().IsChecked = true; Pump(); expanders[1].IsExpanded.Should().BeTrue();
            Save(window, Path.Combine(directory, $"{theme}-{night}-previous.png"));
            expanders[0].IsExpanded = true; Pump(); expanders[1].IsExpanded.Should().BeTrue("versions fold independently");
            var text = Descendants<TextBlock>(expanders[0]).First(item => item.Inlines.OfType<System.Windows.Documents.Hyperlink>().Any());
            text.Inlines.OfType<System.Windows.Documents.Hyperlink>().Select(link => link.NavigateUri.Host).Should().Equal("qm.qq.com", "mirrorchyan.com");
            var background = ((SolidColorBrush)window.FindResource("CardBackgroundBrush")).Color;
            var ratio = Ratio(((SolidColorBrush)text.Foreground).Color, background);
            ratio.Should().BeGreaterThanOrEqualTo(4.5);
            var hint = (TextBlock)window.FindName("HintText");
            var footer = (Border)hint.Parent;
            var footerRatio = Ratio(((SolidColorBrush)hint.Foreground).Color,
                Composite(((SolidColorBrush)footer.Background).Color, ((SolidColorBrush)window.Background).Color));
            footerRatio.Should().BeGreaterThanOrEqualTo(4.5);
            File.WriteAllText(Path.Combine(directory, $"contrast-{theme}-{night}.json"), JsonSerializer.Serialize(new { Content = ratio, Hint = footerRatio }));
            window.Width = 300; window.Height = 360; Pump();
            foreach (var scale in new[] { .75, 1.5 })
            {
                appearance.UiScale = scale; WindowUiScale.Refresh(appearance); Pump();
                WindowUiScale.ScaleOf(window).Should().BeApproximately(scale, .001);
                var scroll = (ScrollViewer)window.FindName("HistoryScroll");
                scroll.ScrollableHeight.Should().BeGreaterThan(0);
                Save(window, Path.Combine(directory, $"{theme}-{night}-small-{scale}.png"));
            }
        }
        finally { window.Close(); appearance.UiScale = 1; WindowUiScale.Refresh(appearance); }
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Save(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(root.RenderSize));
        bitmap.Render(background); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static Color Composite(Color a, Color b) => Color.FromRgb((byte)Math.Round(a.R * a.A / 255.0 + b.R * (1 - a.A / 255.0)),
        (byte)Math.Round(a.G * a.A / 255.0 + b.G * (1 - a.A / 255.0)), (byte)Math.Round(a.B * a.A / 255.0 + b.B * (1 - a.A / 255.0)));
    private static double Ratio(Color a, Color b)
    {
        static double Channel(byte value) { var x = value / 255.0; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); }
        static double L(Color x) => .2126 * Channel(x.R) + .7152 * Channel(x.G) + .0722 * Channel(x.B);
        return (Math.Max(L(a), L(b)) + .05) / (Math.Min(L(a), L(b)) + .05);
    }
}
