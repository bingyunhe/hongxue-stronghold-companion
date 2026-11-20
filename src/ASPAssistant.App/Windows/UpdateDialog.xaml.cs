using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Documents;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Text.RegularExpressions;
using ASPAssistant.Core.Services;

namespace ASPAssistant.App.Windows;

public partial class UpdateDialog : Window
{
    public event Action? DownloadRequested;

    private const string QqInvite = "https://qm.qq.com/q/x5Z2uPdJRK";
    private const string QqClient = "mqqapi://card/show_pslcard?src_type=internal&version=1&uin=1104531618&card_type=group&source=qrcode";
    private const string MirrorDownload = "https://mirrorchyan.com/zh/projects?rid=hongxue-stronghold-companion";

    public UpdateDialog(UpdateInfo updateInfo) : this(updateInfo, uri =>
    {
        using var process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }) { }

    internal UpdateDialog(UpdateInfo updateInfo, Action<string> openAddress)
    {
        InitializeComponent();
        WindowUiScale.PrepareCentered(this);

        TitleText.Text = $"发现新版本 {updateInfo.TagName}";

        var currentVersion = Assembly.GetEntryAssembly()?.GetName().Version;
        CurrentVersionText.Text = currentVersion == new Version(0, 0, 0) || currentVersion is null
            ? "0.0.0 (dev)"
            : currentVersion.ToString(3);

        LatestVersionText.Text = updateInfo.Version;
        if (updateInfo.DownloadSizeText.Length > 0) DownloadHintText.Text = updateInfo.DownloadSizeText;
        // 公告保留标题和更新内容；下载使用说明留在发布页。
        var notes = updateInfo.ReleaseNotes;
        var nextSection = Regex.Match(notes, @"(?m)^##[ \t]+下载与使用[ \t]*\r?$");
        if (nextSection.Success)
            notes = notes[..nextSection.Index];
        notes = string.IsNullOrWhiteSpace(notes) ? "（暂无更新说明）" : notes.Trim();
        int start = 0;
        foreach (Match match in Regex.Matches(notes, @"\[([^\]\r\n]+)\]\(([^()\s]+)\)"))
        {
            var url = match.Groups[2].Value;
            if (url != QqInvite && url != MirrorDownload) continue;
            ReleaseNotesText.Inlines.Add(new Run(notes[start..match.Index]));
            var link = new Hyperlink(new Run(match.Groups[1].Value)) { NavigateUri = new Uri(url) };
            link.SetResourceReference(Hyperlink.ForegroundProperty, "AccentBrush");
            link.Click += (_, _) =>
            {
                try
                {
                    if (url == QqInvite)
                    {
                        try { openAddress(QqClient); return; }
                        catch (Win32Exception) { /* QQ 协议不可用，转到腾讯邀请页。 */ }
                    }
                    openAddress(url);
                }
                catch (Win32Exception)
                {
                    DownloadHintText.Text = url == QqInvite
                        ? "无法打开邀请页，可在 QQ 中搜索群号 1104531618。"
                        : $"无法打开默认浏览器，请手动访问 {url}";
                }
            };
            ReleaseNotesText.Inlines.Add(link);
            start = match.Index + match.Length;
        }
        ReleaseNotesText.Inlines.Add(new Run(notes[start..]));
    }

    /// <summary>把下载进度（0～1）与阶段文案同步到对话框。</summary>
    public void ReportProgress(double fraction, string? downloadNotice = null)
    {
        DownloadButton.Visibility = Visibility.Collapsed;
        DialogProgress.Visibility = Visibility.Visible;
        ProgressPercentText.Visibility = Visibility.Visible;
        var percent = (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100);
        DialogProgress.Value = percent;
        ProgressPercentText.Text = percent + "%";
        DownloadHintText.Text = fraction switch
        {
            < 0.85 => "正在下载安装包…",
            < 0.95 => "正在校验安装包…",
            < 1.0 => "正在解压安装包…",
            _ => (downloadNotice is null ? "" : $"{downloadNotice}，已使用 GitHub Releases 下载。\n")
                 + "下载完成，退出时安装并重新打开，也可点击“立即重启”。"
        };
    }

    public void ShowTransferStatus(string message) => DownloadHintText.Text = message;

    public void ReportFailure(string reason, bool canResume = false)
    {
        DownloadButton.Content = canResume ? "继续下载" : "重试下载";
        DownloadHintText.Text = $"下载失败：{reason}。可点击“{DownloadButton.Content}”。";
        DownloadButton.Visibility = Visibility.Visible;
        DialogProgress.Visibility = Visibility.Collapsed;
        ProgressPercentText.Visibility = Visibility.Collapsed;
    }

    private void OnDownload(object sender, RoutedEventArgs e)
    {
        ReportProgress(0);
        DownloadRequested?.Invoke();
    }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
