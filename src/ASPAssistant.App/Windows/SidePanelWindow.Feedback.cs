using System.IO;
using System.Windows;
using ASPAssistant.Core;
using ASPAssistant.Core.Services;

namespace ASPAssistant.App.Windows;

public partial class SidePanelWindow
{
    private async void OnOpenFeedback(object sender, RoutedEventArgs e)
    {
        FeedbackButton.IsEnabled = false;
        try
        {
            var report = await Task.Run(FeedbackReport.Prepare);
            new FeedbackWindow(report) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn("Feedback", $"Prepare failed: {ex.Message}");
            ShowToast("无法读取诊断记录，请检查日志目录权限后重试。");
        }
        finally { FeedbackButton.IsEnabled = true; }
    }
}
