using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ASPAssistant.App.Controls;
using ASPAssistant.App.Windows;
using ASPAssistant.Core.Services;

namespace ASPAssistant.App.Views;

public partial class SklandBrowseView : UserControl
{
    private readonly List<SklandBrowseGuide> guides = [];
    private SklandGuideCatalogue catalogue = null!;
    private Func<int> currentRound = null!;
    private Func<SklandBrowseStage, string> runFit = null!;
    private Func<SklandBrowseGuide, Task> import = null!;
    private bool loaded;
    private bool loading;
    private string? next;
    private string section = "square";
    private SklandSessionWindow? session;
    private Task? initialRestore;
    private bool restoringAccount;
    private string? restoreError;
    private bool accountBusy;
    private readonly DispatcherTimer searchDelay = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private int queryRevision;
    private CancellationTokenSource? activeRead;
    private bool resetting;
    private bool composing;
    private SklandFilters? options;
    internal bool RestoreAccountOnFirstVisit { get; set; }
    private SklandSessionWindow Session
    {
        get
        {
            if (session != null) return session;
            session = new SklandSessionWindow(Window.GetWindow(this), SklandSessionWindow.UserProfile);
            session.AccountInvalidated += () =>
            {
                ForgetFavorites();
                UpdateAccount();
                if (section == "collected") Status.Text = "账号已变化，请重新登录。";
            };
            return session;
        }
    }
    private bool SignedIn => session?.AccountId != null;
    internal SklandSessionWindow? SignedSession => SignedIn ? session : null;

    private static bool AccountError(Exception ex) => ex is IOException or InvalidOperationException or TimeoutException
        or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or System.Text.Json.JsonException
        or Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException;

    private void UpdateAccount()
    {
        Account.Content = restoringAccount ? "正在连接" : restoreError != null ? "重试" : SignedIn ? "退出" : "登录";
        Account.IsEnabled = !restoringAccount;
        Account.ToolTip = restoreError ?? (SignedIn ? $"当前账号：{session!.Nickname}" : "使用森空岛官网登录");
        PublicTab.IsChecked = section == "square";
        SavedTab.IsChecked = section == "collected";
        OwnTab.IsChecked = section == "published";
        Refresh.Content = section == "collected" ? favoritesSync != null ? "停止" : "同步收藏" : "刷新";
        Refresh.IsEnabled = section == "collected" ? favoritesCancellation?.IsCancellationRequested != true : !loading;
        Cards.Tag = section != "collected" || SignedIn && !accountBusy && !restoringAccount;
        Back.Content = "‹  " + (section == "square" ? "攻略广场" : section == "collected" ? "我的收藏" : "我发布的");
    }

    private void ResetFeed()
    {
        guides.Clear();
        loaded = false;
        next = null;
        More.Visibility = Detail.Visibility = Visibility.Collapsed;
        Detail.DataContext = Stages.ItemsSource = null;
        Square.Visibility = Visibility.Visible;
        resetting = true;
        searchDelay.Stop();
        queryRevision++;
        activeRead?.Cancel();
        Search.Clear();
        LocalSearch.Clear();
        StrategySearch.Clear();
        ClearChips();
        StrategyBody.Visibility = Visibility.Collapsed;
        StrategyToggle.Content = "推荐策略 ▸ 展开";
        SearchHint.Text = section == "square" ? "搜索攻略标题" : "在已加载的攻略中查标题";
        System.Windows.Automation.AutomationProperties.SetName(Search, SearchHint.Text);
        resetting = false;
        Filter();
        UpdateAccount();
    }

    private void ShowAccountError(string message)
    {
        if (!SignedIn && (section != "square" || Equals(Account.Content, "退出")))
        {
            section = "square";
            ResetFeed();
        }
        Status.Text = message;
    }

    internal async Task<bool> ConnectAsync()
    {
        await EnsureAccountRestoredAsync();
        if (SignedIn) return true;
        if (restoreError != null) { Status.Text = restoreError; return false; }
        if (accountBusy || loading) return false;
        accountBusy = true;
        try
        {
            if (!await Session.LoginAsync()) return false;
            await CheckFavoritesOwnerAsync();
            ResetFeed();
            await LoadAsync(false);
            return true;
        }
        catch (Exception ex) when (AccountError(ex)) { Status.Text = "连接森空岛失败：" + ex.Message; return false; }
        finally { accountBusy = false; UpdateAccount(); }
    }

    private async void OnAccount(object sender, RoutedEventArgs e)
    {
        if (accountBusy || restoringAccount) return;
        if (restoreError != null)
        {
            initialRestore = RestoreAccountAsync();
            await initialRestore;
            if (restoreError == null)
            {
                if (section == "collected") { await LoadAsync(false); return; }
                ResetFeed();
                if (loading) searchDelay.Start();
                else await LoadAsync(false);
            }
            return;
        }
        if (loading) return;
        if (!SignedIn) { await ConnectAsync(); return; }
        if (MessageBox.Show(Window.GetWindow(this), $"退出森空岛账号「{session!.Nickname}」？", "森空岛", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        await DisconnectAsync();
    }

    internal async Task DisconnectAsync()
    {
        accountBusy = true;
        try { ForgetFavorites(); await session!.SignOutAsync(); section = "square"; ResetFeed(); await LoadAsync(false); }
        catch (Exception ex) when (AccountError(ex)) { ShowAccountError(ex.Message); }
        finally { accountBusy = false; }
    }

    private async void OnSection(object sender, RoutedEventArgs e)
    {
        var selected = (string)((ToggleButton)sender).Tag;
        UpdateAccount();
        if (accountBusy || selected == section) return;
        section = selected;
        ResetFeed();
        if (section == "collected") { await LoadAsync(false); return; }
        if (restoringAccount)
        {
            CountText.Text = "";
            Status.Text = "正在确认森空岛登录状态…";
            return;
        }
        if (loading) searchDelay.Start();
        else await LoadAsync(false);
    }

    private async void OnReaction(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (accountBusy || loading || section == "collected" && !SignedIn) return;
        if (!SignedIn) { await ConnectAsync(); return; }
        var button = (Button)sender;
        var guide = (SklandBrowseGuide)button.DataContext;
        var kind = (string)button.Tag;
        bool active = kind == "collected" ? !guide.IsCollected : !guide.IsLiked;
        accountBusy = true;
        favoritesCancellation?.Cancel();
        UpdateAccount();
        try
        {
            await session!.CallAsync("reaction", new { accountId = session.AccountId, id = guide.Id, kind, active });
            int index = guides.IndexOf(guide);
            if (section == "collected" && kind == "collected" && !active) guides.RemoveAt(index);
            else guides[index] = kind == "collected" ? guide with { IsCollected = active } : guide with { IsLiked = active };
            await UpdateFavoriteReactionAsync(guide, kind, active);
            Filter();
            Status.Text = kind == "collected" ? active ? "已收藏到森空岛。" : "已取消收藏。" : active ? "已点赞。" : "已取消点赞。";
        }
        catch (Exception ex) when (AccountError(ex)) { ShowAccountError(ex.Message); }
        finally { accountBusy = false; UpdateAccount(); }
    }

    private Func<string, string, object?> resolveDetails = null!;
    private ToolTip DetailsTip => (ToolTip)Resources["GuideDetailsTip"];

    public SklandBrowseView()
    {
        InitializeComponent();
        searchDelay.Tick += async (_, _) =>
        {
            if (loading || accountBusy) return;
            searchDelay.Stop();
            await LoadAsync(false);
        };
        TextCompositionManager.AddPreviewTextInputStartHandler(Search, (_, _) => { composing = true; searchDelay.Stop(); });
        TextCompositionManager.AddPreviewTextInputHandler(Search, (_, _) =>
        {
            if (!composing) return;
            composing = false;
            Dispatcher.BeginInvoke(() => { if (!composing) QueryChanged(); }, DispatcherPriority.Background);
        });
        Search.LostKeyboardFocus += (_, _) => { if (composing) { composing = false; QueryChanged(); } };
        DetailsTip.Closed += (_, _) =>
        {
            if (DetailsTip.IsOpen) return;
            DetailsTip.Content = DetailsTip.DataContext = null;
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) CloseDetails(); };
        PreviewMouseDown += (_, _) => CloseDetails();
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.VerticalChange != 0 || e.HorizontalChange != 0) CloseDetails();
        }));
    }

    private void CloseDetails()
    {
        if (DetailsTip.IsOpen) DetailsTip.IsOpen = false;
    }

    private void OnDetailsOpening(object sender, ToolTipEventArgs e) =>
        e.Handled = !PrepareDetails((FrameworkElement)sender);

    private bool PrepareDetails(FrameworkElement target)
    {
        string kind = (string)target.Tag;
        var strategy = target.DataContext switch
        {
            SklandBrowseGuide guide => guide.Primary,
            SklandBrowseStrategy alternative => alternative,
            _ => null
        };
        string? name = target is ToggleButton chip ? (string)chip.Content : target.DataContext switch
        {
            SklandBrowsePortrait portrait => portrait.Name,
            string text => text,
            _ => strategy?.Name
        };
        if (name == null) return false;
        var model = resolveDetails(kind, target is ToggleButton { DataContext: SklandFilterOption option }
            ? kind == "Strategy" ? option.Id : option.Name : strategy?.Id ?? name);
        var tip = DetailsTip;
        tip.DataContext = model ?? name;
        tip.Tag = kind == "Equipment" && Stages.SelectedItem is SklandBrowseStage stage
            ? stage.SkippedEquipment.Contains(name) ? "此装备会销毁，导入时跳过。" : "导入时保留。"
            : kind == "Covenant" ? target is ToggleButton ? "攻略筛选条件" : Detail.IsVisible ? "本阶段攻略标注的盟约" : "攻略成型阵容标注的盟约" : null;
        var template = model == null ? "GuideNameOnly" : kind == "Covenant" ? "ScrollableCovenantDetails" : $"Guide{kind}Details";
        tip.Content = ((DataTemplate)FindResource(template)).LoadContent();
        return true;
    }

    internal void Configure(SklandGuideCatalogue source, Func<int> round, Func<SklandBrowseStage, string> fit,
        Func<SklandBrowseGuide, Task> openEditor, Func<string, string, object?> lookup)
    {
        catalogue = source;
        currentRound = round;
        runFit = fit;
        import = openEditor;
        resolveDetails = lookup;
    }

    private Task EnsureAccountRestoredAsync() => initialRestore ??=
        RestoreAccountOnFirstVisit && Directory.Exists(SklandSessionWindow.UserProfile)
            ? RestoreAccountAsync() : Task.CompletedTask;

    private async Task RestoreAccountAsync()
    {
        restoringAccount = true;
        restoreError = null;
        UpdateAccount();
        try { await Session.RestoreAccountAsync(); await CheckFavoritesOwnerAsync(); }
        catch (Exception ex) when (AccountError(ex))
        {
            restoreError = "暂时无法确认登录状态，可点「重试」。" + ex.Message;
            Status.Text = restoreError;
        }
        finally { restoringAccount = false; UpdateAccount(); }
    }

    internal async Task ShowAsync()
    {
        await EnsureAccountRestoredAsync();
        if (accountBusy) return;
        if (!loaded || section == "collected") await LoadAsync(false);
        else if (Detail.IsVisible && Stages.SelectedItem is SklandBrowseStage stage) Fit.Text = runFit(stage);
    }

    private async Task LoadAsync(bool append)
    {
        if (section == "collected") { await OpenFavoritesAsync(); return; }
        if (loading || restoringAccount) return;
        searchDelay.Stop();
        if (section != "square" && !SignedIn)
        {
            CountText.Text = "";
            Status.Text = restoreError ?? $"请点击「登录」查看{(section == "collected" ? "我的收藏" : "我发布的攻略")}。";
            return;
        }
        using var read = new CancellationTokenSource();
        activeRead = read;
        int revision = queryRevision;
        var query = section == "square" ? new SklandGuideQuery(Search.Text.Trim(), CheckedOptions(Core).FirstOrDefault()?.Id,
            CheckedOptions(Extra).Select(o => o.Id).ToArray(), CheckedOptions(Strategy).FirstOrDefault()?.Id) : SklandGuideQuery.Empty;
        loading = true;
        More.IsEnabled = false;
        UpdateAccount();
        Status.Text = "正在读取森空岛攻略…";
        try
        {
            await EnsureFiltersAsync();
            if (revision != queryRevision) return;
            var page = SignedIn ? await session!.LoadAsync(section, append ? next : null, query, read.Token)
                : await catalogue.LoadAsync(append ? next : null, query, read.Token);
            if (revision != queryRevision) return;
            if (!append) guides.Clear();
            var ids = guides.Select(g => g.Id).ToHashSet();
            guides.AddRange(page.Guides.Where(g => ids.Add(g.Id)));
            next = page.Next;
            loaded = true;
            Status.Text = restoreError ?? (!SignedIn ? "已加载匿名可见结果，登录可查看更多。" : next != null ? "可继续加载下一页。" : section != "square"
                ? guides.Count == 0 ? "此账号在本赛季暂无攻略。" : "已加载全部攻略。" : "已加载本次查询的全部结果。");
            More.Visibility = next == null || !SignedIn ? Visibility.Collapsed : Visibility.Visible;
            Filter();
            if (!append) ListScroll.ScrollToTop();
        }
        catch (Exception ex) when (AccountError(ex) || ex is InvalidDataException or HttpRequestException or OperationCanceledException or KeyNotFoundException)
        {
            if (revision == queryRevision)
            {
                CountText.Text = "本次查询未完成。";
                ClearEmpty.Visibility = Visibility.Collapsed;
                ShowAccountError("加载失败，可点刷新重试。" + ex.Message);
            }
        }
        finally
        {
            activeRead = null;
            loading = false;
            Refresh.IsEnabled = More.IsEnabled = true;
            UpdateAccount();
        }
    }

    private static IEnumerable<SklandFilterOption> CheckedOptions(WrapPanel group) => group.Children.OfType<ToggleButton>()
        .Where(b => b.IsChecked == true).Select(b => b.DataContext).OfType<SklandFilterOption>();

    private static string[] Selected(WrapPanel group) => CheckedOptions(group).Select(o => o.Name).ToArray();

    private void BuildFilter(WrapPanel group, SklandFilterOption[] choices)
    {
        foreach (var option in choices.Prepend(new("", "全部")))
        {
            var chip = new ToggleButton { Content = option.Name, IsChecked = option.Id == "", Style = (Style)FindResource("GuideChip") };
            if (option.Id != "")
            {
                chip.DataContext = option;
                if (group == Strategy) chip.Content = option.Name + " · " + option.Initiator;
                chip.Tag = group == Strategy ? "Strategy" : "Covenant";
                chip.ToolTip = DetailsTip;
                chip.ToolTipOpening += OnDetailsOpening;
                ToolTipService.SetInitialShowDelay(chip, group == Strategy ? 250 : PanelToolTip.CovenantInitialDelay);
                ToolTipService.SetBetweenShowDelay(chip, group == Strategy ? 0 : PanelToolTip.CovenantSwitchInterval);
                ToolTipService.SetShowDuration(chip, 60000);
            }
            chip.Click += OnChip;
            group.Children.Add(chip);
        }
        ((ToggleButton)group.Children[0]).IsChecked = Selected(group).Length == 0;
    }

    private void OnChip(object sender, RoutedEventArgs e)
    {
        var chip = (ToggleButton)sender;
        var group = (WrapPanel)chip.Parent;
        var all = (ToggleButton)group.Children[0];
        if (chip == all || group != Extra)
            foreach (var sibling in group.Children.OfType<ToggleButton>().Where(b => b != chip)) sibling.IsChecked = false;
        all.IsChecked = Selected(group).Length == 0;
        QueryChanged();
    }

    private void ClearChips()
    {
        foreach (var group in new[] { Core, Extra, Strategy })
            foreach (var chip in group.Children.OfType<ToggleButton>()) chip.IsChecked = chip == group.Children[0];
    }

    private void OnClearFilters(object sender, RoutedEventArgs e) { ClearChips(); QueryChanged(); }

    private void OnClearEmpty(object sender, RoutedEventArgs e)
    {
        ClearChips();
        Search.Clear();
        LocalSearch.Clear();
        QueryChanged();
    }

    private void OnRemoveStrategy(object sender, RoutedEventArgs e)
    {
        foreach (var chip in Strategy.Children.OfType<ToggleButton>()) chip.IsChecked = chip == Strategy.Children[0];
        QueryChanged();
    }

    private void OnToggleStrategies(object sender, RoutedEventArgs e)
    {
        bool expand = StrategyBody.Visibility != Visibility.Visible;
        StrategyBody.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        StrategyToggle.Content = expand ? "推荐策略 ▾ 收起" : "推荐策略 ▸ 展开";
        UpdateLayout();
        if (expand) { StrategySearch.Focus(); StrategySearch.BringIntoView(); }
        else { StrategyToggle.Focus(); StrategyHeader.BringIntoView(); }
    }

    private void OnStrategySearch(object sender, TextChangedEventArgs e) => FilterStrategies();

    private void FilterStrategies()
    {
        var term = StrategySearch.Text.Trim();
        int matches = 0;
        foreach (var chip in Strategy.Children.OfType<ToggleButton>())
        {
            bool visible = chip.DataContext is SklandFilterOption o
                ? SearchMatch.Matches(term, o.Name, o.Initiator)
                : term.Length == 0;
            chip.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible) matches++;
        }
        NoStrategy.Visibility = matches == 0 && term.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void QueryChanged()
    {
        if (resetting || catalogue == null) return;
        Filter();
        if (section != "square") return;
        CountText.Text = "正在查找符合条件的攻略…";
        ClearEmpty.Visibility = Visibility.Collapsed;
        queryRevision++;
        activeRead?.Cancel();
        next = null;
        More.Visibility = Visibility.Collapsed;
        searchDelay.Stop();
        if (!composing) searchDelay.Start();
    }

    private void Filter()
    {
        if (DetailsTip.PlacementTarget is not ToggleButton) CloseDetails();
        var core = Selected(Core).FirstOrDefault();
        var extra = Selected(Extra);
        var strategy = Selected(Strategy).FirstOrDefault();
        int count = extra.Length + (core != null ? 1 : 0) + (strategy != null ? 1 : 0);
        FilterButton.Content = count == 0 ? "筛选" : $"筛选 · {count}";
        var query = Search.Text.Trim();
        ChosenStrategy.Visibility = strategy == null ? Visibility.Collapsed : Visibility.Visible;
        ChosenStrategy.Content = "已选：" + strategy + " ×";
        var local = LocalSearch.Text.Trim();
        var visible = guides.Where(g => g.Title.Contains(query, StringComparison.OrdinalIgnoreCase) && g.Matches("", core, extra, strategy)
            && (local.Length == 0 || g.Author.Contains(local, StringComparison.OrdinalIgnoreCase) ||
                g.Stages.Any(s => s.Operators.Any(n => SearchMatch.Matches(local, n))))).ToList();
        Cards.ItemsSource = visible;
        bool empty = loaded && visible.Count == 0 && (count > 0 || query.Length > 0 || local.Length > 0);
        ClearEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = empty ? next != null || local.Length > 0 ? "已加载结果中没有符合条件的攻略" : "当前条件下没有攻略"
            : $"已加载 {guides.Count} 篇，符合筛选 {visible.Count} 篇";
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (section == "collected" && favoritesCancellation != null) { favoritesCancellation.Cancel(); UpdateAccount(); return; }
        if (!accountBusy) await LoadAsync(false);
    }
    private async void OnMore(object sender, RoutedEventArgs e) { if (!accountBusy) await LoadAsync(true); }
    private void OnSearch(object sender, TextChangedEventArgs e) => QueryChanged();
    private void OnLocalSearch(object sender, TextChangedEventArgs e) { if (catalogue != null) Filter(); }

    private void OnFilters(object sender, RoutedEventArgs e) =>
        Filters.Visibility = Filters.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void OnOpen(object sender, RoutedEventArgs e) => Open((SklandBrowseGuide)((Button)sender).DataContext);
    internal void Open(SklandBrowseGuide guide)
    {
        CloseDetails();
        Square.Visibility = Visibility.Collapsed;
        Detail.DataContext = guide;
        Detail.Visibility = Visibility.Visible;
        ImportMessage.Text = "";
        Stages.ItemsSource = guide.Stages;
        Stages.SelectedItem = guide.StageForRound(currentRound(), Selected(Core).FirstOrDefault(), Selected(Extra));
        AlternatePanel.Visibility = guide.Strategies.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        ScopeText.Text = string.IsNullOrWhiteSpace(guide.Scope) ? "作者未填写" : guide.Scope;
        ScopeText.MaxHeight = 54;
        ScopeToggle.Content = "展开";
        UpdateScopeToggle();
        StageScroll.ScrollToTop();
    }

    private void UpdateScopeToggle() => ScopeToggle.Visibility =
        double.IsPositiveInfinity(ScopeText.MaxHeight) || ScopeText.ActualHeight >= 53 ? Visibility.Visible : Visibility.Collapsed;

    private void OnScopeSizeChanged(object sender, SizeChangedEventArgs e) => UpdateScopeToggle();

    private void OnScopeToggle(object sender, RoutedEventArgs e)
    {
        bool expand = !double.IsPositiveInfinity(ScopeText.MaxHeight);
        ScopeText.MaxHeight = expand ? double.PositiveInfinity : 54;
        ScopeToggle.Content = expand ? "收起" : "展开";
    }

    private void OnStage(object sender, SelectionChangedEventArgs e)
    {
        CloseDetails();
        StagePanel.DataContext = Stages.SelectedItem;
        if (Stages.SelectedItem is not SklandBrowseStage stage) return;
        Fit.Text = runFit(stage);
        GearStatus.Text = stage.Equipment.Length == 0 ? "此阶段未列出装备" : "";
        BondStatus.Text = stage.Bonds.Any() ? "" : "此阶段未列出盟约";
        StageScroll.ScrollToTop();
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        CloseDetails();
        Detail.Visibility = Visibility.Collapsed;
        Square.Visibility = Visibility.Visible;
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        Import.IsEnabled = false;
        try { await import((SklandBrowseGuide)Detail.DataContext); }
        catch (InvalidDataException ex) { ImportMessage.Text = ex.Message; }
        finally { Import.IsEnabled = true; }
    }
}
