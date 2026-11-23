using System.Net.Http;
using System.Text.Json;

namespace ASPAssistant.Core.Services;

public sealed record SklandBrowseStage(string Name, int Type, int Start, int End, string[] Operators,
    string[] Equipment, string[] CoreBonds, string[] ExtraBonds, string Notes)
{
    public IReadOnlySet<string> SkippedEquipment { get; init; } = new HashSet<string>();
    public IEnumerable<string> Bonds => CoreBonds.Concat(ExtraBonds);
    public bool MatchesBonds(string? core, IReadOnlyCollection<string> extra) =>
        (core == null || CoreBonds.Contains(core)) && extra.All(ExtraBonds.Contains);
}

public sealed record SklandBrowseStrategy(string Id, string Name);
public sealed record SklandBrowsePortrait(string Name, int Tier);

public sealed record SklandBrowseGuide(string Title, string Author, string SeasonName, string Phase, string Published,
    string Collected, string Liked, string Scope, SklandBrowseStrategy[] Strategies, SklandBrowsePortrait[] Portraits,
    SklandBrowseStage[] Stages, JsonElement Item, JsonElement Resources)
{
    public string Id => Item.GetProperty("guide").GetProperty("guideId").GetString()!;
    public bool IsCollected { get; init; } = Item.TryGetProperty("rel", out var rel) && rel.GetProperty("collected").GetBoolean();
    public bool IsLiked { get; init; } = Item.TryGetProperty("rel", out var relation) && relation.GetProperty("liked").GetBoolean();
    public string CollectLabel => (IsCollected ? "已收藏 " : "收藏 ") + Collected;
    public string LikeLabel => (IsLiked ? "已赞 " : "赞 ") + Liked;
    public string ReviewStatus => Item.GetProperty("guide").TryGetProperty("auditStatus", out var status)
        ? status.GetInt32() switch { 1 => "审核中", 3 => "未通过审核", _ => "" } : "";
    public SklandBrowseStage? Formation => Stages.FirstOrDefault(s => s.Type == 1);
    public SklandBrowseStrategy? Primary => Strategies.FirstOrDefault();
    public IEnumerable<SklandBrowseStrategy> Alternatives => Strategies.Skip(1);
    public string AuthorInitial => System.Globalization.StringInfo.GetNextTextElement(Author);
    public SklandBrowseStage? StageForRound(int round, string? core, IReadOnlyCollection<string> extra)
    {
        var matching = Stages.Where(s => s.MatchesBonds(core, extra));
        return matching.FirstOrDefault(s => s.Type == 2 && round >= s.Start && round <= s.End)
            ?? (core == null && extra.Count == 0 ? Formation : null) ?? matching.FirstOrDefault();
    }

    public bool Matches(string query, string? core, IReadOnlyCollection<string> extra, string? strategy) =>
        (Title.Contains(query, StringComparison.OrdinalIgnoreCase) || Author.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Stages.Any(s => s.Operators.Any(n => n.Contains(query, StringComparison.OrdinalIgnoreCase))))
        && (strategy == null || Strategies.Any(s => s.Name == strategy))
        && Stages.Any(s => s.MatchesBonds(core, extra));
}

public sealed record SklandBrowsePage(IReadOnlyList<SklandBrowseGuide> Guides, string? Next);

public sealed record SklandFilterOption(string Id, string Name, string Initiator = "");
public sealed record SklandFilters(string Season, SklandFilterOption[] Core, SklandFilterOption[] Extra, SklandFilterOption[] Strategies);
public sealed record SklandGuideQuery(string Title, string? Core, string[] Extra, string? Strategy)
{
    public static SklandGuideQuery Empty { get; } = new("", null, [], null);

    public IEnumerable<KeyValuePair<string, string>> Parameters()
    {
        if (Title.Length > 0) yield return new("titleKeyword", Title);
        if (Core != null) yield return new("seasonBondId", Core);
        foreach (var id in Extra) yield return new("regularBondIds", id);
        if (Strategy != null) yield return new("strategyId", Strategy);
    }

}

public sealed class SklandGuideCatalogue(HttpClient http)
{
    private SklandFilters? filters;
    private async Task<JsonElement> GetDataAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://zonai.skland.com/web/v1/game/garrison/guide/" + path);
        request.Headers.Referrer = new Uri("https://game.skland.com/");
        request.Headers.Add("Origin", "https://game.skland.com");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = json.RootElement;
        if (root.GetProperty("code").GetInt32() != 0)
            throw new InvalidDataException("森空岛：" + root.GetProperty("message").GetString());
        return root.GetProperty("data").Clone();
    }

    public async Task<SklandFilters> LoadFiltersAsync(CancellationToken ct = default)
    {
        if (filters != null) return filters;
        var data = await GetDataAsync("resource", ct).ConfigureAwait(false);
        var season = data.GetProperty("versionInfo").GetProperty("seasonId").GetString()
            ?? throw new InvalidDataException("森空岛未提供当前赛季。");
        SklandFilterOption[] Bonds(int type) => data.GetProperty("bonds").EnumerateArray()
            .Where(b => b.GetProperty("bondType").GetInt32() == type && b.GetProperty("bondId").GetString() != "suntShip")
            .Select(b => new SklandFilterOption(b.GetProperty("bondId").GetString()!, b.GetProperty("name").GetString()!)).ToArray();
        return filters = new(season, Bonds(1), Bonds(2), data.GetProperty("strategies").EnumerateArray()
            .Select(s => new SklandFilterOption(s.GetProperty("strategyId").GetString()!, s.GetProperty("name").GetString()!,
                s.GetProperty("initiatorName").GetString()!)).ToArray());
    }

    public async Task<SklandBrowsePage> LoadAsync(string? cursor, SklandGuideQuery query, CancellationToken ct = default)
    {
        try
        {
            var options = await LoadFiltersAsync(ct).ConfigureAwait(false);
            using var parameters = new FormUrlEncodedContent(query.Parameters());
            var search = await parameters.ReadAsStringAsync(ct).ConfigureAwait(false);
            var data = await GetDataAsync($"index?source=COMMUNITY&sortType=1&seasonId={Uri.EscapeDataString(options.Season)}&pageSize=10&pageToken={Uri.EscapeDataString(cursor ?? "")}" +
                (search.Length == 0 ? "" : "&" + search), ct).ConfigureAwait(false);
            return ReadPage(data, cursor);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("森空岛列表格式已变化，请稍后重试。", ex);
        }
    }

    public static SklandBrowsePage ReadPage(JsonElement data, string? cursor)
    {
        var next = data.GetProperty("hasMore").GetBoolean() ? data.GetProperty("pageToken").GetString() : null;
        if (next != null && (next.Length == 0 || next == cursor)) throw new InvalidDataException("森空岛返回了无效的下一页。");
        return new(data.GetProperty("list").EnumerateArray().Where(x => x.GetProperty("invalid").GetInt32() == 0)
            .Select(x => ReadGuide(x, ResourceFor(data, x.GetProperty("guide")))).ToArray(), next);
    }

    internal static JsonElement ResourceFor(JsonElement data, JsonElement guide) =>
        data.GetProperty("seasonResourceMap").GetProperty(guide.GetProperty("seasonId").GetString()!)
            .GetProperty("versionResourceMap").GetProperty(guide.GetProperty("versionId").GetString()!);

    internal static SklandBrowseGuide ReadGuide(JsonElement item, JsonElement resource)
    {
        var guide = item.GetProperty("guide");
        var stages = new List<SklandBrowseStage>();
        foreach (var tab in guide.GetProperty("tabs").EnumerateArray())
        {
            int type = tab.GetProperty("tabType").GetInt32();
            int start = type == 2 ? tab.GetProperty("startRound").GetInt32() : 0;
            int end = type == 2 ? tab.GetProperty("endRound").GetInt32() : 0;
            var bonds = tab.GetProperty("activeBonds");
            string[] ReadBonds(string key) => bonds.ValueKind == JsonValueKind.Object && bonds.TryGetProperty(key, out var ids)
                ? ids.EnumerateArray().Select(id => resource.GetProperty("bonds").GetProperty(id.GetString()!).GetProperty("name").GetString()!).ToArray() : [];
            string[] Names(string field, string key, string group) => tab.GetProperty(field).EnumerateArray()
                .Select(e => resource.GetProperty(group).GetProperty(e.GetProperty(key).GetString()!).GetProperty("name").GetString()!).ToArray();
            var equipment = tab.GetProperty("recItems").EnumerateArray()
                .Select(e => resource.GetProperty("items").GetProperty(e.GetProperty("itemId").GetString()!)).ToArray();
            stages.Add(new SklandBrowseStage(type switch { 1 => "成型阵容", 2 => $"{start}–{end} 回合", _ => "其他阶段" },
                type, start, end, Names("recChars", "charId", "chars"), equipment.Select(e => e.GetProperty("name").GetString()!).ToArray(),
                ReadBonds("seasonBondIds"), ReadBonds("regularBondIds"), tab.GetProperty("operationIdea").GetString()!)
            {
                SkippedEquipment = equipment.Where(e => !SklandGuideImporter.KeepEquipmentOnImport(e))
                    .Select(e => e.GetProperty("name").GetString()!).ToHashSet()
            });
        }
        var portraits = new List<SklandBrowsePortrait>();
        foreach (var tab in guide.GetProperty("tabs").EnumerateArray().Where(t => t.GetProperty("tabType").GetInt32() == 1).Take(1))
            foreach (var entry in tab.GetProperty("recChars").EnumerateArray())
            {
                var op = resource.GetProperty("chars").GetProperty(entry.GetProperty("charId").GetString()!);
                portraits.Add(new(op.GetProperty("name").GetString()!, op.GetProperty("shopLevel").GetInt32()));
            }
        var strategies = guide.GetProperty("strategyIds").EnumerateArray().Select(v => v.GetString()!)
            .Select(id => new SklandBrowseStrategy(id, resource.GetProperty("strategies").GetProperty(id).GetProperty("name").GetString()!)).ToArray();
        var seasonTag = item.GetProperty("seasonTag");
        var stats = item.GetProperty("rts");
        var timestamp = guide.GetProperty("publishAtTs");
        long seconds = timestamp.ValueKind == JsonValueKind.String ? long.Parse(timestamp.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : timestamp.GetInt64();
        var published = DateTimeOffset.FromUnixTimeSeconds(seconds).ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        return new(guide.GetProperty("title").GetString()!, item.GetProperty("poster").GetProperty("nickname").GetString()!,
            seasonTag.GetProperty("name").GetString()!, seasonTag.GetProperty("phaseName").GetString()!, published,
            stats.GetProperty("collected").ToString(), stats.GetProperty("liked").ToString(), guide.GetProperty("applicableScope").GetString() ?? "",
            strategies, portraits.ToArray(),
            stages.OrderBy(s => s.Type == 1).ThenBy(s => s.Start).ToArray(), item, resource);
    }
}
