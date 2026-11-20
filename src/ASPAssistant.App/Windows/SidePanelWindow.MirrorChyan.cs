using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Navigation;

namespace ASPAssistant.App.Windows;

public partial class SidePanelWindow
{
    private void RefreshMirrorChyanCdkStatus()
        => MirrorChyanCdkStatus.Text = _mirrorChyanCdk.HasSavedCdk ? "已保存 · 留空保存可清除" : "选填 · 留空使用 GitHub 下载";

    private void OnOpenMirrorChyan(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Win32Exception) { MirrorChyanCdkStatus.Text = "无法打开浏览器，可访问 mirrorchyan.com。"; }
        e.Handled = true;
    }

    private async void OnSaveMirrorChyanCdk(object sender, RoutedEventArgs e)
    {
        SaveMirrorChyanCdkButton.IsEnabled = MirrorChyanCdkBox.IsEnabled = false;
        try
        {
            await _mirrorChyanCdk.SaveAsync(MirrorChyanCdkBox.Password);
            MirrorChyanCdkBox.Clear();
            RefreshMirrorChyanCdkStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            MirrorChyanCdkBox.Clear();
            MirrorChyanCdkStatus.Text = "保存失败，请检查 Windows 用户和设置目录权限。";
        }
        finally { SaveMirrorChyanCdkButton.IsEnabled = MirrorChyanCdkBox.IsEnabled = true; }
    }
}
