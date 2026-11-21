using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ASPAssistant.Core;
using ASPAssistant.Core.Services;
using Microsoft.Win32;

namespace ASPAssistant.App.Windows;

public partial class FeedbackWindow : Window
{
    private FeedbackReport _report;
    public FeedbackWindow(FeedbackReport report)
    {
        _report = report;
        InitializeComponent();
        WindowUiScale.PrepareCentered(this);
        foreach (var game in report.Samples.Where(s => s.IsGameReport && s.GameRecords.Count > 0))
        {
            var check = new CheckBox { Content = game.Label, Tag = game, IsChecked = game.GameRecords.Any(r => r.Pending) };
            check.Click += OnBatchSelection;
            GameChecks.Children.Add(check);
        }
        CategoryBox.SelectedIndex = report.Samples.Any(s => s.IsGameReport) ? 1 : 0;
        CategoryBox.SelectionChanged += (_, _) => UpdateCategory();
        UpdateCategory();
    }

    private void UpdateCategory()
    {
        var report = _report;
        ScreenshotChoice.IsChecked = false;
        if (CategoryBox.SelectedIndex == 1 && report.Samples.Length > 0)
        {
            EventChoice.ItemsSource = report.Samples;
            EventChoice.Visibility = Visibility.Visible;
            EventChoice.SelectedIndex = 0;
            return;
        }
        BatchPanel.Visibility = Visibility.Collapsed;
        ExportButton.Content = "导出反馈包";
        ExportButton.IsEnabled = true;
        _report = report with { Selected = null };
        EventChoice.ItemsSource = null;
        EventChoice.Visibility = Visibility.Collapsed;
        MarkPanel.Visibility = Visibility.Collapsed;
        FrameChoice.ItemsSource = null;
        ScreenshotChoice.Content = "附上这次识别画面";
        ScreenshotChoice.IsEnabled = report.Recognition is { Frame.Length: > 0 };
        RecognitionText.Text = report.Recognition is { } snapshot
            ? $"已固定 {snapshot.CapturedAt.LocalDateTime:MM-dd HH:mm:ss} 的{snapshot.Source}记录，距现在 {(DateTimeOffset.Now - snapshot.CapturedAt).TotalSeconds:F0} 秒。" +
                (snapshot.Frame.Length == 0 ? "画面超过 16 MB，仅保留识别结果。" : "")
            : "暂无识别记录。请在问题出现后打开此窗口；仍可反馈程序报错或功能建议。";
    }

    private void OnEventChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EventChoice.SelectedItem is not RecognitionSample sample) return;
        _report = _report with { Selected = sample };
        MarkPanel.Visibility = sample.IsGameReport ? Visibility.Visible : Visibility.Collapsed;
        MarkChoice.ItemsSource = sample.Cases;
        MarkChoice.SelectedIndex = sample.Cases.Length > 0 ? 0 : -1;
        ScreenshotChoice.IsChecked = false;
        ScreenshotChoice.IsEnabled = sample.Frames.Length > 0;
        ScreenshotChoice.Content = $"附上这组游戏画面（{sample.Frames.Length} 张）";
        RecognitionText.Text = sample.Result;
        FrameChoice.ItemsSource = sample.Frames;
        FrameChoice.SelectedIndex = sample.Frames.Length - 1;
        if (sample.IsGameReport)
        {
            ScreenshotChoice.IsChecked = sample.Frames.Length > 0;
            if (string.IsNullOrWhiteSpace(DescriptionBox.Text)) DescriptionBox.Text = "棋盘识别标记";
        }
        BatchPanel.Visibility = sample.IsGameReport && sample.GameRecords.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshBatch();
    }

    private RecognitionSample[] BatchRecords() => GameChecks.Children.OfType<CheckBox>()
        .Where(c => !GamePicker.IsExpanded || c.IsChecked == true)
        .SelectMany(c => ((RecognitionSample)c.Tag).GameRecords)
        .Where(r => GamePicker.IsExpanded || r.Pending).ToArray();

    private void OnSelectAllGames(object sender, RoutedEventArgs e)
    {
        foreach (CheckBox check in GameChecks.Children) check.IsChecked = true;
        RefreshBatch();
    }

    private void OnBatchSelection(object sender, RoutedEventArgs e) => RefreshBatch();

    private void RefreshBatch()
    {
        if (BatchPanel.Visibility != Visibility.Visible)
        {
            ExportButton.Content = "导出反馈包";
            ExportButton.IsEnabled = true;
            return;
        }
        var records = BatchRecords();
        var frames = ScreenshotChoice.IsChecked == true ? records.SelectMany(r => r.Frames).ToArray() : [];
        var bytes = frames.Sum(frame => (long)frame.Image.Length) + records.Sum(r => 512L + System.Text.Encoding.UTF8.GetByteCount(r.Result + r.Kind + r.Note + string.Join("",r.Actions)));
        BatchSummary.Text = $"{records.Select(r => r.Game).Distinct().Count()} 局 · {records.Length} 条记录 · {frames.Length} 张截图 · 预计 {bytes/1024d:N0} KB";
        ExportButton.Content = GamePicker.IsExpanded ? "导出所选对局" : "导出未导出记录";
        ExportButton.IsEnabled = records.Length > 0;
    }

    private void OnMarkChanged(object sender, SelectionChangedEventArgs e)
    {
        var i = MarkChoice.SelectedIndex;
        if (i >= 0 && _report.Selected is { } sample)
            ExpectedBox.Text = sample.Cases[i].Expected;
    }

    private void OnExpectedChanged(object sender, TextChangedEventArgs e)
    {
        int i = MarkChoice.SelectedIndex;
        if (i >= 0 && _report.Selected is { IsGameReport: true } sample)
        {
            if (sample.Cases[i].Expected == ExpectedBox.Text) return;
            if (sample.GameRecords.Count > 0)
            {
                try { FieldFeedback.SetNote(sample,i,ExpectedBox.Text); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { StatusText.Text = "说明未能保存，请检查本地存储。"; return; }
            }
            sample.Cases[i] = sample.Cases[i] with { Expected = ExpectedBox.Text };
            RefreshBatch();
        }
    }

    private void OnFrameChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScreenshotChoice.IsChecked == true) UpdateScreenshot();
    }

    private string Category => ((ComboBoxItem)CategoryBox.SelectedItem).Content.ToString()!;

    private void OnDescriptionChanged(object sender, TextChangedEventArgs e) =>
        StatusText.Text = "反馈包仅保存到本机，请检查内容后自行提交。";

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != FeedbackTabs || FeedbackTabs.SelectedIndex != 1) return;
        PreviewBox.Text = BatchPanel.Visibility == Visibility.Visible
            ? BatchSummary.Text + "\n" + string.Join("\n",BatchRecords().Select(r => $"{r.Label} · {r.Result} {r.Note}"))
            : _report.Preview(Category, DescriptionBox.Text, StepsBox.Text);
    }

    private void OnScreenshotChanged(object sender, RoutedEventArgs e)
        { UpdateScreenshot(); RefreshBatch(); }

    private void UpdateScreenshot()
    {
        FrameChoice.Visibility = ScreenshotChoice.IsChecked == true && _report.Selected is not null
            ? Visibility.Visible : Visibility.Collapsed;
        if (ScreenshotChoice.IsChecked != true)
        {
            ScreenshotPreview.Source = null;
            ScreenshotPreview.Visibility = Visibility.Collapsed;
            return;
        }
        var bytes = FrameChoice.SelectedItem is SampleFrame frame ? frame.Image : _report.Recognition!.Frame;
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 700;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        ScreenshotPreview.Source = image;
        ScreenshotPreview.Visibility = Visibility.Visible;
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DescriptionBox.Text))
        {
            StatusText.Text = "请先填写问题描述。";
            FeedbackTabs.SelectedIndex = 0;
            DescriptionBox.Focus();
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "保存反馈包", Filter = "反馈包 (*.zip)|*.zip",
            FileName = $"鸿雪反馈-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != true) return;
        ExportButton.IsEnabled = false;
        var category = Category;
        var description = DescriptionBox.Text;
        var steps = StepsBox.Text;
        var include = ScreenshotChoice.IsChecked == true;
        var report = _report;
        var records = BatchPanel.Visibility == Visibility.Visible ? BatchRecords() : null;
        IsEnabled = false;
        try
        {
            await Task.Run(() => report.Export(dialog.FileName, category, description, steps, include, records));
            StatusText.Text = "反馈包已保存。可打开反馈页面，填写问题并手动附上 ZIP 文件。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn("Feedback", $"Export failed: {ex.Message}");
            StatusText.Text = "保存失败，请选择新的文件名或可写目录。";
        }
        finally { IsEnabled = true; RefreshBatch(); }
    }

    private void OnOpenIssue(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/bingyunhe/hongxue-stronghold-companion/issues/new") { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            AppLogger.Warn("Feedback", $"Could not open issue page: {ex.Message}");
            StatusText.Text = "无法打开浏览器，请通过项目地址进入 Issues 提交反馈。";
        }
    }
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
