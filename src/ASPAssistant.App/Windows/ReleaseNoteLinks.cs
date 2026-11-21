using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Documents;

namespace ASPAssistant.App.Windows;

internal static class ReleaseNoteLinks
{
    private const string QqInvite = "https://qm.qq.com/q/x5Z2uPdJRK";
    private const string QqClient = "mqqapi://card/show_pslcard?src_type=internal&version=1&uin=1104531618&card_type=group&source=qrcode";
    private const string MirrorDownload = "https://mirrorchyan.com/zh/projects?rid=hongxue-stronghold-companion";

    public static void Open(string address)
    {
        using var process = Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
    }

    public static void Render(TextBlock target, string notes, Action<string> openAddress, Action<string> showError)
    {
        target.Inlines.Clear();
        int start = 0;
        foreach (Match match in Regex.Matches(notes, @"\[([^\]\r\n]+)\]\(([^()\s]+)\)"))
        {
            var url = match.Groups[2].Value;
            if (url != QqInvite && url != MirrorDownload) continue;
            target.Inlines.Add(new Run(notes[start..match.Index]));
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
                    showError(url == QqInvite
                        ? "无法打开邀请页，可在 QQ 中搜索群号 1104531618。"
                        : $"无法打开默认浏览器，请手动访问 {url}");
                }
            };
            target.Inlines.Add(link);
            start = match.Index + match.Length;
        }
        target.Inlines.Add(new Run(notes[start..]));
    }
}
