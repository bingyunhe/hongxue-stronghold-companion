using System.IO;
using System.Net.Http;
using System.Windows;
using ASPAssistant.App.Windows;
using ASPAssistant.Core.Services;

namespace ASPAssistant.App.Views;

public partial class SklandBrowseView
{
    private Task<SklandFilters>? filterRequest;
    private Task<SklandFavoritesSnapshot?>? favoritesState;
    private Task? favoritesSync;
    private CancellationTokenSource? favoritesCancellation;
    private SklandFavoritesCache FavoritesCache => new(Path.Combine(session?.Profile ?? SklandSessionWindow.UserProfile,
        "assistant-favorites-v1.json"));

    private async Task EnsureFiltersAsync()
    {
        if (options != null) return;
        var request = filterRequest ??= catalogue.LoadFiltersAsync();
        try
        {
            var filters = await request;
            if (options != null) return;
            options = filters;
            BuildFilter(Core, filters.Core);
            BuildFilter(Extra, filters.Extra);
            BuildFilter(Strategy, filters.Strategies);
            FilterStrategies();
        }
        finally { if (filterRequest == request) filterRequest = null; }
    }

    private Task<SklandFavoritesSnapshot?> CachedFavorites() => favoritesState ??= Task.Run(FavoritesCache.Read);

    private async Task CheckFavoritesOwnerAsync()
    {
        if (await CachedFavorites() is { } snapshot && snapshot.Account != session?.AccountId)
            ForgetFavorites();
    }

    private void ForgetFavorites()
    {
        favoritesCancellation?.Cancel();
        ClearFavoritesSnapshot();
    }

    private void ClearFavoritesSnapshot()
    {
        favoritesState = Task.FromResult<SklandFavoritesSnapshot?>(null);
        if (section == "collected") ResetFeed();
        FavoritesCache.Clear();
    }

    private void ShowFavorites(SklandFavoritesSnapshot snapshot)
    {
        var offset = ListScroll.VerticalOffset;
        guides.Clear();
        guides.AddRange(snapshot.Guides);
        loaded = true;
        next = null;
        Filter();
        ListScroll.ScrollToVerticalOffset(offset);
    }

    private async Task OpenFavoritesAsync()
    {
        if (RestoreAccountOnFirstVisit || SignedIn)
        {
            var restore = EnsureAccountRestoredAsync();
            var state = CachedFavorites();
            var snapshot = await state;
            if (section != "collected") return;
            if (state == favoritesState && snapshot != null && (options == null || snapshot.Season == options.Season)
                && (restoringAccount || restoreError != null || snapshot.Account == session?.AccountId))
            {
                if (!loaded) ShowFavorites(snapshot);
                Status.Text = $"{(SignedIn ? "上次同步" : "上次账号的收藏")} {snapshot.Updated.ToLocalTime():MM-dd HH:mm} · 正在更新…";
            }
            else if (!loaded) Status.Text = "正在读取收藏…";
            await restore;
            if (section != "collected") return;
        }
        if (!SignedIn)
        {
            if (!loaded) CountText.Text = "";
            Status.Text = restoreError == null ? "请点击「登录」查看我的收藏。"
                : loaded ? "正在显示上次账号的收藏。" + restoreError : restoreError;
            return;
        }
        await (favoritesSync ??= SyncFavoritesAsync(session!.AccountId!));
    }

    private async Task SyncFavoritesAsync(string account)
    {
        using var cancel = new CancellationTokenSource();
        favoritesCancellation = cancel;
        await Task.Yield(); // Publish the shared task before any synchronous completion or tab switch.
        UpdateAccount();
        try
        {
            await EnsureFiltersAsync();
            cancel.Token.ThrowIfCancellationRequested();
            if (await CachedFavorites() is { } old && (old.Season != options!.Season || old.Account != account))
                ClearFavoritesSnapshot();
            var collected = new List<SklandBrowseGuide>();
            var ids = new HashSet<string>();
            var cursors = new HashSet<string>();
            string? cursor = null;
            do
            {
                var page = await session!.LoadAsync("collected", cursor, SklandGuideQuery.Empty, cancel.Token, options!.Season);
                cancel.Token.ThrowIfCancellationRequested();
                collected.AddRange(page.Guides.Where(g => ids.Add(g.Id)).Select(g => g with { IsCollected = true }));
                cursor = page.Next;
                if (cursor != null && !cursors.Add(cursor)) throw new InvalidDataException("官网重复返回同一页。");
                if (section == "collected" && cursor != null) Status.Text = $"正在同步收藏，已读取 {collected.Count} 篇…";
            } while (cursor != null);
            if (account != session.AccountId) return;
            var snapshot = new SklandFavoritesSnapshot(account, options!.Season, DateTimeOffset.Now, collected);
            favoritesState = Task.FromResult<SklandFavoritesSnapshot?>(snapshot);
            if (section == "collected")
            {
                ShowFavorites(snapshot);
                Status.Text = $"同步完成 · {snapshot.Updated.ToLocalTime():HH:mm} · 共 {collected.Count} 篇";
            }
            try { FavoritesCache.Save(snapshot); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (section == "collected") Status.Text = "收藏已更新，本次未能保存到本地。";
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            if (section == "collected" && SignedIn) Status.Text = "已停止同步。";
        }
        catch (Exception ex) when (AccountError(ex) || ex is InvalidDataException or HttpRequestException or OperationCanceledException or KeyNotFoundException)
        {
            if (section == "collected") Status.Text = !SignedIn ? "账号已变化，请重新登录。"
                : (loaded ? "已保留上次收藏。" : "") + "同步失败，可点「同步收藏」重试。" + ex.Message;
        }
        finally
        {
            favoritesCancellation = null;
            favoritesSync = null;
            UpdateAccount();
        }
    }

    private async Task UpdateFavoriteReactionAsync(SklandBrowseGuide guide, string kind, bool active)
    {
        if (await CachedFavorites() is not { } snapshot || snapshot.Account != session?.AccountId || snapshot.Season != options?.Season) return;
        var items = snapshot.Guides.ToList();
        int index = items.FindIndex(g => g.Id == guide.Id);
        if (kind == "collected" && active && index < 0) items.Insert(0, guide with { IsCollected = true });
        else if (kind == "collected" && !active && index >= 0) items.RemoveAt(index);
        else if (kind == "liked" && index >= 0) items[index] = items[index] with { IsLiked = active };
        else return;
        snapshot = snapshot with { Guides = items };
        favoritesState = Task.FromResult<SklandFavoritesSnapshot?>(snapshot);
        FavoritesCache.Save(snapshot);
    }
}
