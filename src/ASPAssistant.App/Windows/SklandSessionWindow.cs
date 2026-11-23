using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ASPAssistant.Core.Services;

namespace ASPAssistant.App.Windows;

internal sealed class SklandSessionWindow : Window
{
    private const string Home = "https://game.skland.com/arknights/stronghold-guide";
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHGetKnownFolderPath(in Guid folder, uint flags, IntPtr token,
        [MarshalAs(UnmanagedType.LPWStr)] out string path);

    internal static string UserProfile
    {
        get
        {
            // FOLDERID_LocalAppDataLow is the per-user, non-roaming location for a restricted browser host.
            SHGetKnownFolderPath(new Guid("A520A1A4-1780-4FF6-BD18-167343C5AF16"), 0, IntPtr.Zero, out var localLow);
            return Path.Combine(localLow, "HongxueCompanion", "Skland", "Browser");
        }
    }
    internal string Profile { get; }
    private readonly WebView2 browser = new();
    private readonly TextBlock hint = new() { Text = "请在森空岛官网完成验证，再点击「完成登录」。", Margin = new Thickness(8) };
    private readonly Button done = new() { Content = "完成登录", Margin = new Thickness(8) };
    private TaskCompletionSource<bool>? login;
    private Task? initializing;
    private bool disposed;
    private static readonly string Bridge = ReadBridge();
    internal string? AccountId { get; private set; }
    internal event Action? AccountInvalidated;
    internal string? Nickname { get; private set; }

    private static string ReadBridge()
    {
        var assembly = typeof(SklandSessionWindow).Assembly;
        using var reader = new StreamReader(assembly.GetManifestResourceStream(assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(".SklandGuideSession.js", StringComparison.Ordinal)))!);
        return reader.ReadToEnd();
    }

    internal SklandSessionWindow(Window owner, string profile)
    {
        Profile = profile;
        Owner = owner;
        Title = "森空岛官网登录";
        Width = 860; Height = 700;
        MinWidth = 400; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var panel = new DockPanel();
        var bar = new DockPanel();
        bar.SetResourceReference(Panel.BackgroundProperty, "PanelBackgroundBrush");
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        done.SetResourceReference(StyleProperty, "DialogPrimaryButton");
        DockPanel.SetDock(done, Dock.Right);
        bar.Children.Add(done); bar.Children.Add(hint);
        DockPanel.SetDock(bar, Dock.Top);
        panel.Children.Add(bar); panel.Children.Add(browser);
        Content = new Border { Child = panel };
        done.Click += async (_, _) =>
        {
            done.IsEnabled = false;
            try
            {
                if (await ReadAccountAsync()) { Hide(); login?.TrySetResult(true); }
                else hint.Text = "尚未取得登录账号，请在官网完成登录后重试。";
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
            { hint.Text = ex.Message; }
            finally { done.IsEnabled = true; }
        };
        Closing += (_, e) =>
        {
            if (disposed || !Owner.IsVisible && login == null) return;
            e.Cancel = true; Hide(); login?.TrySetResult(false);
        };
        owner.Closed += (_, _) => { disposed = true; login?.TrySetResult(false); browser.Dispose(); Close(); };
    }

    internal async Task InitializeAsync()
    {
        try { await (initializing ??= InitializeBrowserAsync()); }
        catch { initializing = null; throw; }
    }

    private async Task InitializeBrowserAsync()
    {
        // Create the WPF control's HWND before the hidden session is used to restore an account.
        bool starting = browser.CoreWebView2 == null;
        if (starting)
        {
            ShowActivated = false;
            var handle = new WindowInteropHelper(this).EnsureHandle();
            // WebView2 needs a shown HWND. Cloak its desktop surface before WPF can present the first frame.
            SystemBorder.SetCloaked(handle, true);
            try
            {
                Show();
                await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Loaded);
                Directory.CreateDirectory(Profile);
                var env = await CoreWebView2Environment.CreateAsync(userDataFolder: Profile);
                await browser.EnsureCoreWebView2Async(env);
            }
            finally
            {
                if (!disposed)
                {
                    Hide();
                    SystemBorder.SetCloaked(handle, false);
                    ShowActivated = true;
                }
            }
        }
        var web = browser.CoreWebView2!;
        web.Settings.AreHostObjectsAllowed = false;
        web.Settings.IsPasswordAutosaveEnabled = false;
        web.Settings.IsGeneralAutofillEnabled = false;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Navigated(object? sender, CoreWebView2NavigationCompletedEventArgs e) =>
            ready.TrySetResult(e.IsSuccess);
        web.NavigationCompleted += Navigated;
        try
        {
            web.Navigate(Home);
            if (!await ready.Task.WaitAsync(TimeSpan.FromSeconds(35))) throw new IOException("森空岛官网加载失败。");
        }
        finally { web.NavigationCompleted -= Navigated; }
    }

    internal async Task<bool> LoginAsync()
    {
        await InitializeAsync();
        if (login != null) { Activate(); return await login.Task; }
        if (await ReadAccountAsync()) return true;
        login = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Show(); Activate();
        try
        {
            // Let the player interact with the first-party dialog, including if they cancel it.
            _ = CallLoginAsync();
            return await login.Task;
        }
        finally { login = null; }
    }

    private async Task CallLoginAsync()
    {
        try
        {
            await CallAsync("login", new { }, TimeSpan.FromMinutes(5));
            if (login is { } pending && await ReadAccountAsync()) { Hide(); pending.TrySetResult(true); }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        { hint.Text = "请在官网点「未登录」，登录后点「完成登录」。"; }
    }

    internal async Task<bool> ReadAccountAsync()
    {
        var user = await CallAsync("account", new { });
        AccountId = user.ValueKind == JsonValueKind.Null ? null : user.GetProperty("id").GetString();
        Nickname = AccountId == null ? null : user.GetProperty("name").GetString();
        return AccountId != null;
    }

    internal async Task RestoreAccountAsync()
    {
        await InitializeAsync();
        try { await ReadAccountAsync(); }
        catch
        {
            // Retry navigation as well if the page loaded but its account module did not.
            initializing = null;
            throw;
        }
    }

    internal async Task SignOutAsync()
    {
        await browser.CoreWebView2.Profile.ClearBrowsingDataAsync();
        AccountId = Nickname = null;
        initializing = null;
    }

    internal async Task<JsonElement> CallAsync(string command, object args, TimeSpan? timeout = null)
    {
        var web = browser.CoreWebView2;
        if (!Uri.TryCreate(web.Source, UriKind.Absolute, out var location) || location.GetLeftPart(UriPartial.Authority) != "https://game.skland.com")
            throw new IOException("请完成官网登录并返回攻略站。");
        var id = Guid.NewGuid().ToString("N");
        var expectedAccount = AccountId;
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Received(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || source.GetLeftPart(UriPartial.Authority) != "https://game.skland.com") return;
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("requestId", out var requestId) || requestId.GetString() != id) return;
            if (root.TryGetProperty("error", out var error))
            {
                if (expectedAccount != null && expectedAccount == AccountId && root.TryGetProperty("changed", out var changed) && changed.GetBoolean())
                {
                    AccountId = Nickname = null;
                    AccountInvalidated?.Invoke();
                }
                completion.TrySetException(new IOException(error.GetString()));
            }
            else completion.TrySetResult(root.GetProperty("value").Clone());
        }
        web.WebMessageReceived += Received;
        try
        {
            var input = JsonSerializer.Serialize(args);
            await web.ExecuteScriptAsync($"void (async()=>{{if(location.origin!=='https://game.skland.com')return; const requestId='{id}'; {Bridge}; try{{const value=await runGuideCommand('{command}',{input});chrome.webview.postMessage({{requestId,value}})}}catch(e){{chrome.webview.postMessage({{requestId,changed:e.name==='GuideAccountChanged',error:String(e.message || '官网操作失败。').slice(0,240)}})}}}})()");
            return await completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(30));
        }
        finally { web.WebMessageReceived -= Received; }
    }

    internal async Task<SklandBrowsePage> LoadAsync(string section, string? cursor, SklandGuideQuery query, CancellationToken ct, string? season = null)
    {
        var data = await CallAsync("list", new { accountId = AccountId, section, cursor, season,
            titleKeyword = query.Title, seasonBondId = query.Core, regularBondIds = query.Extra, strategyId = query.Strategy }).WaitAsync(ct);
        return SklandGuideCatalogue.ReadPage(data, cursor);
    }
}
