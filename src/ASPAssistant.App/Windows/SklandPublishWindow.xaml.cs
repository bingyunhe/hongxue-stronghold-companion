using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;

namespace ASPAssistant.App.Windows;

public partial class SklandPublishWindow : Window
{
    private readonly SklandPublishDraft draft;
    private readonly SklandSessionWindow session;
    private readonly string accountId;
    private readonly string receipt;
    private bool sending;
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    internal SklandPublishWindow(Lineup lineup, IReadOnlyList<Strategy> strategies, SklandSessionWindow session)
    {
        draft = SklandPublishDraft.FromLineup(lineup);
        this.session = session;
        accountId = session.AccountId!;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId + "/" + lineup.Id)));
        receipt = Path.Combine(Path.GetDirectoryName(session.Profile)!, "Submissions", key + ".json");
        InitializeComponent();
        WindowUiScale.PrepareCentered(this);
        Account.Text = "发布账号 · " + (session.Nickname ?? "尚未登录");
        DraftTitle.Text = draft.Title;
        var names = draft.StrategyIds.Select(id => strategies.FirstOrDefault(s => s.Id == id)?.Name ?? id).ToArray();
        StrategyText.Text = "首选策略 · " + (names.FirstOrDefault() ?? "未指定") +
            (names.Length > 1 ? "\n备选策略 · " + string.Join("、", names.Skip(1)) : "");
        RenderStages(null);
        Loaded += async (_, _) => await PrepareAsync();
        Closing += (_, e) => { if (sending) e.Cancel = true; };
    }

    private object Arguments(SklandPublishDraft content) => new { accountId, draft = JsonSerializer.SerializeToElement(content, WebJson) };

    internal void RenderStages(string[][]? bonds)
    {
        StageList.ItemsSource = draft.Tabs.Select((stage, index) => new
        {
            Heading = stage.Final ? "成型阵容" : $"{stage.Name} · 第 {stage.Start}–{stage.End} 回合",
            Operators = "干员 · " + string.Join("、", stage.Operators.Select(op => op.Name + (op.Elite ? "（精锐）" : ""))),
            Gear = "需求装备 · " + (stage.Gear.Length > 0 ? string.Join("、", stage.Gear) : "无"),
            Bonds = "达成盟约 · " + (bonds == null ? "待官网核对" : bonds[index].Length > 0 ? string.Join("、", bonds[index]) : "无"),
            Notes = stage.Notes
        }).ToList();
    }

    private async Task PrepareAsync()
    {
        Submit.IsEnabled = false;
        if (session.AccountId == null) { Status.Text = "重新登录后可提交。"; return; }
        try
        {
            var existing = File.Exists(receipt) ? await File.ReadAllTextAsync(receipt) : null;
            if (existing != null)
            {
                using var doc = JsonDocument.Parse(existing);
                var record = doc.RootElement;
                string? guideId = record.TryGetProperty("guideId", out var id) ? id.GetString() : null;
                if (!string.IsNullOrEmpty(guideId))
                {
                    Status.Text = "此阵容已提交，可到「我发布的」查看审核状态。";
                    ShowGuideLink(guideId);
                    return;
                }
                Status.Text = "上次提交结果待核对。请先在「我发布的」确认。";
                if (MessageBox.Show(this, "如果官网已有这篇攻略，请取消。已在「我发布的」核实没有这篇攻略，并准备重新提交？",
                    "核对上次提交", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                File.Delete(receipt);
            }
            var bonds = await session.CallAsync("prepare", Arguments(draft));
            RenderStages(bonds.Deserialize<string[][]>());
            Status.Text = "提交后由森空岛审核。";
            Submit.IsEnabled = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or JsonException or UnauthorizedAccessException)
        { Status.Text = ex.Message; }
    }

    private void ShowGuideLink(string id)
    {
        CopyGuide.Tag = "https://game.skland.com/arknights/stronghold-guide/detail?header=0&guideId=" + Uri.EscapeDataString(id);
        Submit.Visibility = Visibility.Collapsed;
        CopyGuide.Visibility = Visibility.Visible;
    }

    private void OnCopyGuide(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText((string)CopyGuide.Tag); Status.Text = "攻略链接已复制。"; }
        catch (System.Runtime.InteropServices.ExternalException) { Status.Text = "剪贴板正被占用，请再点一次。"; }
    }

    private async void OnSubmit(object sender, RoutedEventArgs e)
    {
        var title = DraftTitle.Text.Trim();
        if (title.Length is 0 or > 40 || Original.IsChecked != true) { Status.Text = "请填写 1–40 字的标题并确认发布权。"; return; }
        if (sending || !Submit.IsEnabled) return;
        sending = true;
        Submit.IsEnabled = false;
        bool recorded = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(receipt)!);
            using (var file = new FileStream(receipt, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(file, new { title, state = "pending" });
            recorded = true;
            var result = await session.CallAsync("publish", Arguments(draft with { Title = title, Scope = Scope.Text.Trim() }));
            var id = result.GetProperty("guideId").GetString();
            Status.Text = string.IsNullOrEmpty(id) ? "官网已响应，请在「我发布的」核对。"
                : "已提交森空岛审核，可在「我发布的」查看。";
            if (!string.IsNullOrEmpty(id))
            {
                var temporary = receipt + ".tmp";
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { guideId = id, title, state = "submitted" }));
                File.Move(temporary, receipt, true);
                ShowGuideLink(id);
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or JsonException or UnauthorizedAccessException)
        { Status.Text = recorded ? "提交结果待核对，请查看「我发布的」。" + ex.Message : ex.Message; }
        finally { sending = false; }
    }
}
