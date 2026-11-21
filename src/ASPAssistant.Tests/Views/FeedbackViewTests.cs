using System.Text.Json;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ASPAssistant.App;
using ASPAssistant.App.Windows;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Services;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

internal static class FeedbackViewTests
{
    public static void Verify(GameShopTheme theme, bool night)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var appearance = new SidebarAppearance();
        WindowUiScale.Initialize(appearance);
        ThemeManager.Apply(theme, night);
        var output = Path.Combine(TestOutput.Directory, "feedback");
        Directory.CreateDirectory(output);
        var pixels = new byte[160 * 90 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 80; pixels[i + 1] = 100; pixels[i + 2] = 160; pixels[i + 3] = 255; }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(160, 90, 96, 96, PixelFormats.Bgra32, null, pixels, 160 * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var report = new FeedbackReport("版本：界面验证\n系统：Windows", "[WARN] [ShopPurchase] 无法连接本地代理 127.0.0.1:7890",
            new(DateTimeOffset.Now.AddSeconds(-3), "商店识别", stream.ToArray(), "{\"识别结果\":\"风絮\",\"区域\":[0.1,0.8,0.1,0.03]}"));
        var samples = new RecognitionSampleHistory();
        var id = samples.Begin("干员复核 · D8", 8);
        samples.Step(id, "点击前", stream.ToArray());
        samples.Step(id, "详情仍是宴", stream.ToArray());
        samples.Observe(stream.ToArray(), 8, DateTimeOffset.Now);
        report = report with { Samples = samples.Freeze() };
        var owner = new Window { Width = 400, Height = 600, Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        owner.Show();
        var dialog = new FeedbackWindow(report) { Owner = owner, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        var contrast = new Dictionary<string, double>();
        try
        {
            dialog.Show(); Pump();
            var category = (ComboBox)dialog.FindName("CategoryBox");
            category.SelectedIndex.Should().Be(0);
            category.SelectedIndex = 1;
            category.SelectedIndex.Should().Be(1);
            var screenshot = (CheckBox)dialog.FindName("ScreenshotChoice");
            screenshot.IsChecked.Should().BeFalse();
            screenshot.IsEnabled.Should().BeTrue();
            ((Button)dialog.FindName("ExportButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((TextBlock)dialog.FindName("StatusText")).Text.Should().Contain("请先填写");
            Pump();
            Save(dialog, Path.Combine(output, $"{theme}-{night}-required.png"));
            ((TextBox)dialog.FindName("DescriptionBox")).Text = "商店内风絮未高亮，希望检查识别结果。";
            ((TextBlock)dialog.FindName("StatusText")).Text.Should().NotContain("请先填写");
            ((TextBox)dialog.FindName("StepsBox")).Text = "打开商店 → 将风絮加入追踪 → 刷新商店，预期高亮目标。";
            var choices = (ComboBox)dialog.FindName("EventChoice");
            choices.Items.Count.Should().Be(2);
            choices.SelectedIndex = 1;
            ((TextBlock)dialog.FindName("RecognitionText")).Text.Should().Contain("顺序");
            choices.SelectedIndex = 0;
            screenshot.IsChecked = true; Pump();
            var frames = (ComboBox)dialog.FindName("FrameChoice");
            frames.Items.Count.Should().Be(2);
            frames.SelectedIndex = 0;
            frames.BringIntoView(); Pump();
            Save(dialog, Path.Combine(output, $"{theme}-{night}-event-frames.png"));
            ((Image)dialog.FindName("ScreenshotPreview")).Source.Should().NotBeNull();
            foreach (var name in new[] { "RecognitionText", "PrivacyText", "StatusText" })
            {
                var text = (TextBlock)dialog.FindName(name);
                var background = ((SolidColorBrush)dialog.Background).Color;
                if (name == "StatusText") background = Composite(((SolidColorBrush)((Border)((StackPanel)text.Parent).Parent).Background).Color, background);
                contrast[name] = Ratio(((SolidColorBrush)text.Foreground).Color, background);
                contrast[name].Should().BeGreaterThanOrEqualTo(4.5);
            }
            var title = (TextBlock)dialog.FindName("TitleText");
            contrast["title"] = Ratio(((SolidColorBrush)title.Foreground).Color,
                Composite(((SolidColorBrush)((Border)((DockPanel)title.Parent).Parent).Background).Color, ((SolidColorBrush)dialog.Background).Color));
            contrast["title"].Should().BeGreaterThanOrEqualTo(4.5);
            var footer = (Border)((StackPanel)((TextBlock)dialog.FindName("StatusText")).Parent).Parent;
            foreach (var name in new[] { "IssueButton", "ExportButton" })
            {
                var button = (Button)dialog.FindName(name);
                contrast[name] = Ratio(((SolidColorBrush)button.Foreground).Color,
                    Composite(((SolidColorBrush)button.Background).Color, Composite(((SolidColorBrush)footer.Background).Color, ((SolidColorBrush)dialog.Background).Color)));
                contrast[name].Should().BeGreaterThanOrEqualTo(4.5);
            }
            Save(dialog, Path.Combine(output, $"{theme}-{night}-normal.png"));
            ((TabControl)dialog.FindName("FeedbackTabs")).SelectedIndex = 1; Pump();
            ((TextBox)dialog.FindName("PreviewBox")).Text.Should().Contain("风絮").And.Contain("127.0.0.1:7890");
            Save(dialog, Path.Combine(output, $"{theme}-{night}-preview.png"));
            ((TabControl)dialog.FindName("FeedbackTabs")).SelectedIndex = 0;
            dialog.Width = 300; dialog.Height = 380;
            ((TextBox)dialog.FindName("DescriptionBox")).Text = string.Concat(Enumerable.Repeat("长问题描述与异常信息 ABC123。", 100));
            Pump();
            var root = (FrameworkElement)dialog.Content;
            foreach (var name in new[] { "IssueButton", "ExportButton", "DescriptionBox" })
            {
                var element = (FrameworkElement)dialog.FindName(name);
                var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
                bounds.Right.Should().BeLessThanOrEqualTo(root.ActualWidth + .5, name);
            }
            Save(dialog, Path.Combine(output, $"{theme}-{night}-small.png"));
            foreach (var scale in new[] { .75, 1.5 })
            {
                appearance.UiScale = scale;
                WindowUiScale.Refresh(appearance);
                Pump();
                WindowUiScale.ScaleOf(dialog).Should().BeApproximately(scale, .001);
                Save(dialog, Path.Combine(output, $"{theme}-{night}-scale-{scale}.png"));
            }
            screenshot.IsChecked = false;
            ((Image)dialog.FindName("ScreenshotPreview")).Source.Should().BeNull();
            var group = report.Samples[0] with { IsGameReport = true, Cases =
                [new("标记1 · A7",3,"空格",1,""),new("标记2 · C7",3,"点成B7",2,"")] };
            choices.ItemsSource = new[] { group };
            choices.SelectedIndex = 0;
            var cases = (ComboBox)dialog.FindName("MarkChoice");
            var expected = (TextBox)dialog.FindName("ExpectedBox");
            screenshot.IsChecked.Should().BeTrue();
            expected.Text = "应为折桠";
            cases.SelectedIndex = 1;
            expected.Text = "应选中暴躁铁皮";
            cases.SelectedIndex = 0;
            expected.Text.Should().Be("应为折桠");
            ((TabControl)dialog.FindName("FeedbackTabs")).SelectedIndex = 1; Pump();
            ((TextBox)dialog.FindName("PreviewBox")).Text.Should().Contain("应选中暴躁铁皮").And.Contain("应为折桠");
        }
        finally { dialog.Close(); owner.Close(); appearance.UiScale = 1; WindowUiScale.Refresh(appearance); }
        var empty = new FeedbackWindow(new("运行信息", "无日志", null)) { Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            empty.Show(); Pump();
            ((CheckBox)empty.FindName("ScreenshotChoice")).IsEnabled.Should().BeFalse();
            ((TextBlock)empty.FindName("RecognitionText")).Text.Should().Contain("暂无识别记录");
            Save(empty, Path.Combine(output, $"{theme}-{night}-empty.png"));
        }
        finally { empty.Close(); }
        var batchDir = Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        try
        {
            FieldFeedback.Save(batchDir,Guid.NewGuid(),report.Samples[0]);
            FieldFeedback.Save(batchDir,Guid.NewGuid(),report.Samples[0]);
            var batch = new FeedbackWindow(report with { Samples = FieldFeedback.LoadGames(batchDir) })
                { Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                batch.Show(); Pump();
                var summary = (TextBlock)batch.FindName("BatchSummary");
                summary.Text.Should().Contain("2 局 · 2 条记录");
                var picker = (Expander)batch.FindName("GamePicker");
                picker.IsExpanded = true;
                var checks = (StackPanel)batch.FindName("GameChecks");
                var first = (CheckBox)checks.Children[0];
                first.IsChecked = false;
                first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                summary.Text.Should().Contain("1 局 · 1 条记录");
                ((CheckBox)batch.FindName("ScreenshotChoice")).IsChecked = false;
                summary.Text.Should().Contain("0 张截图");
                picker.BringIntoView(); Pump();
                Save(batch,Path.Combine(output,$"{theme}-{night}-batch.png"));
            }
            finally { batch.Close(); }
        }
        finally { Directory.Delete(batchDir,true); }
        VerifyAbout(theme, night, output);
        File.WriteAllText(Path.Combine(output, $"contrast-{theme}-{night}.json"), JsonSerializer.Serialize(contrast));
    }

    private static void VerifyAbout(GameShopTheme theme, bool night, string output)
    {
        var directory = Path.Combine(Path.GetTempPath(), "feedback-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var calculator = new CovenantCalculator([], [], new Dictionary<string, string>());
        var panel = new SidePanelWindow(new(), new(), new(), new(), new(), new(),
            new(new LineupStore(Path.Combine(directory, "lineups"), calculator.EquipmentRules), calculator),
            new(calculator.EquipmentRules, Path.Combine(directory, "share")), [], [], [], new SettingsManager(directory), theme, new())
            { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, Width = 300, Height = 620 };
        try
        {
            panel.Show(); ThemeManager.Apply(theme, night); Pump();
            ((Button)panel.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var settings = (Border)panel.FindName("SettingsPanel");
            Wait(() => settings.Opacity >= .9999 && settings.RenderTransform is TranslateTransform transform && Math.Abs(transform.X) < .01);
            settings.BeginAnimation(UIElement.OpacityProperty, null); settings.Opacity = 1;
            var move = (TranslateTransform)settings.RenderTransform;
            move.BeginAnimation(TranslateTransform.XProperty, null); move.X = 0;
            ((TabControl)panel.FindName("SettingsCategoryList")).SelectedIndex = 3;
            var about = (FrameworkElement)panel.FindName("AboutSettingsPage");
            about.BringIntoView(); Pump();
            ((FrameworkElement)panel.FindName("FeedbackButton")).BringIntoView(); Pump();
            var title = (TextBlock)panel.FindName("FeedbackTitle");
            Ratio(((SolidColorBrush)title.Foreground).Color, Composite(((SolidColorBrush)((Border)((StackPanel)title.Parent).Parent).Background).Color,
                ((SolidColorBrush)panel.FindResource("PanelBackgroundBrush")).Color)).Should().BeGreaterThanOrEqualTo(4.5);
            var link = (System.Windows.Documents.Hyperlink)panel.FindName("QqGroupLink");
            link.NavigateUri.Scheme.Should().Be("mqqapi");
            link.NavigateUri.Query.Should().Contain("uin=1104531618").And.Contain("card_type=group");
            var group = (TextBlock)link.Parent;
            group.BringIntoView(); Pump();
            var project = (TextBlock)panel.FindName("ProjectAddressText");
            group.TranslatePoint(new Point(), about).Y.Should().BeGreaterThan(project.TranslatePoint(new Point(), about).Y);
            Save(panel, Path.Combine(output, $"{theme}-{night}-about.png"));
            panel.FindName("RecognitionFeedbackButton").Should().BeNull();
            {
                var button = (Button)panel.FindName("FeedbackButton");
                button.IsDescendantOf(about).Should().BeTrue();
                Exception? error = null;
                var closed = false;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                timer.Tick += (_, _) =>
                {
                    var feedback = Application.Current.Windows.OfType<FeedbackWindow>().SingleOrDefault();
                    if (feedback is null || !feedback.IsLoaded) return;
                    timer.Stop();
                    feedback.Opacity = 0; feedback.ShowActivated = false;
                    try
                    {
                        var hasGames = FeedbackReport.Prepare().Samples.Any(s => s.IsGameReport);
                        ((ComboBox)feedback.FindName("CategoryBox")).SelectedIndex.Should().Be(hasGames ? 1 : 0);
                        ((Button)panel.FindName("FeedbackButton")).IsEnabled.Should().BeFalse();
                    }
                    catch (Exception ex) { error = ex; }
                    finally { feedback.Close(); closed = true; }
                };
                timer.Start();
                try
                {
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Wait(() => closed && button.IsEnabled);
                }
                finally { timer.Stop(); }
                if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            }
        }
        finally { panel.Close(); Directory.Delete(directory, true); }
    }

    private static void Wait(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Feedback UI did not reach the expected state");
            Pump(); Thread.Sleep(5);
        }
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Save(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) context.DrawRectangle(window.Background, null, new Rect(root.RenderSize));
        bitmap.Render(visual); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static Color Composite(Color top, Color bottom) => Color.FromRgb(
        (byte)Math.Round(top.R * top.A / 255.0 + bottom.R * (1 - top.A / 255.0)),
        (byte)Math.Round(top.G * top.A / 255.0 + bottom.G * (1 - top.A / 255.0)),
        (byte)Math.Round(top.B * top.A / 255.0 + bottom.B * (1 - top.A / 255.0)));
    private static double Ratio(Color a, Color b)
    {
        static double Channel(byte value) { var n = value / 255.0; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); }
        static double Luminance(Color color) => .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        var x = Luminance(a); var y = Luminance(b);
        return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
}
