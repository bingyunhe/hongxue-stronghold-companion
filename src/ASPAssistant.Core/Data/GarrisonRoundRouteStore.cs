using System.Text.Json;
using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Data;

public sealed record GarrisonRoundPath(string Lane, bool Flying, bool Approx, BoardCell[][] Segments);
public sealed record GarrisonRouteSource(string Level, string Route, string Enemy, bool Candidate = false);
public readonly record struct SpawnCount(int Min, int Max)
{
    public override string ToString() => Min == Max ? Min.ToString() : $"{Min}–{Max}";
}
public sealed record GarrisonRoundRoute(GarrisonRoundPath Path, bool Candidate, IReadOnlyList<GarrisonRouteSource> Sources)
{
    public IReadOnlyDictionary<string, SpawnCount> Enemies { get; init; } = new Dictionary<string, SpawnCount>();
    public bool Overlaps { get; init; }
}
public enum GarrisonRouteStatus { Confirmed, Candidates, WaitingForEnemies, Unmatched, Uncovered }
public sealed record GarrisonRoundRouteMatch(GarrisonRouteStatus Status, IReadOnlyList<GarrisonRoundRoute> Routes, string[]? Grid = null)
{
    public IReadOnlyDictionary<string, SpawnCount> GateTotals { get; init; } = new Dictionary<string, SpawnCount>();
    public bool RosterMismatch { get; init; }
}

public sealed class GarrisonRoundRouteStore
{
    private sealed record Group(string Key, Dictionary<string, Dictionary<string, int>> Units);
    private sealed record Board(int? Grid, Dictionary<string, int> Routes);
    private sealed record Level(Dictionary<string, Board> Maps, Group[] Groups);
    private sealed record Mode(HashSet<string> Maps, HashSet<string> Inactive, Dictionary<int, string[]> Rounds);
    private sealed record Dataset(Dictionary<string, Mode> Modes, Dictionary<string, Level> Levels, GarrisonRoundPath[] Paths, string[][] Grids);
    private readonly Dataset _data;
    private readonly Dictionary<(string Mode, int Round), HashSet<string>> _rosters = [];

    internal IReadOnlySet<string>? PossibleEnemies(string? mode, int round)
    {
        if (mode is null || !_data.Modes.TryGetValue(mode, out var rules) || !rules.Rounds.TryGetValue(round, out var ids))
            return null;
        lock (_rosters)
        {
            if (!_rosters.TryGetValue((mode, round), out var names))
                _rosters[(mode, round)] = names = ids.SelectMany(id => _data.Levels[id].Groups)
                    .Where(g => !rules.Inactive.Contains(g.Key)).SelectMany(g => g.Units.Keys).ToHashSet(StringComparer.Ordinal);
            return names;
        }
    }

    private GarrisonRoundRouteStore(Dataset data) => _data = data;

    public static async Task<GarrisonRoundRouteStore?> LoadAsync(string dataDirectory)
    {
        string json;
        try { json = await File.ReadAllTextAsync(Path.Combine(dataDirectory, "garrison_round_routes.json")); }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
        var data = JsonSerializer.Deserialize<Dataset>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new JsonException("本回合路线数据为空");
        return new(data);
    }

    public GarrisonRoundRouteMatch Find(string map, string? mode, int round, IReadOnlyList<string> roster, bool rosterComplete = true)
    {
        if (mode is null || !_data.Modes.TryGetValue(mode, out var rules) || !rules.Maps.Contains(map)
            || !rules.Rounds.TryGetValue(round, out var levels)) return new(GarrisonRouteStatus.Uncovered, []);
        var names = roster.ToHashSet(StringComparer.Ordinal);
        var matches = names.Count == 0 ? [] : (from id in levels
                       let level = _data.Levels[id]
                       from candidate in level.Groups
                       where !rules.Inactive.Contains(candidate.Key) && names.All(candidate.Units.ContainsKey)
                       select (Id: id, Board: level.Maps[map], Group: candidate)).ToList();
        if (matches.Count == 0)
        {
            // These shapes are referenced by spawn actions. An enemy name can only be
            // attached after a roster matches, particularly on rounds with several bosses.
            var boards = levels.Select(id => _data.Levels[id].Maps[map]).ToArray();
            var paths = boards.SelectMany(board => board.Routes.Values).Distinct()
                .Select(shape => new GarrisonRoundRoute(_data.Paths[shape], true, [])).ToArray();
            return new(names.Count == 0 ? GarrisonRouteStatus.WaitingForEnemies : GarrisonRouteStatus.Unmatched,
                paths, boards.FirstOrDefault()?.Grid is int baseGrid ? _data.Grids[baseGrid] : null);
        }
        bool exact = matches.Any(m => m.Group.Units.Count == names.Count);
        if (rosterComplete && exact) matches.RemoveAll(m => m.Group.Units.Count != names.Count);

        var sources = new Dictionary<int, Dictionary<GarrisonRouteSource, int>>();
        var occurrences = new Dictionary<int, int>();
        var scenarios = new List<Dictionary<(int Shape, string Enemy), int>>();
        var gateTotals = new List<Dictionary<string, int>>();
        foreach (var (level, board, group) in matches)
        {
            var counts = new Dictionary<(int, string), int>();
            var gates = new Dictionary<string, int>();
            var seen = new HashSet<int>();
            foreach (var (enemy, refs) in group.Units)
                foreach (var (route, count) in refs)
                {
                    int shape = board.Routes[route];
                    counts[(shape, enemy)] = counts.GetValueOrDefault((shape, enemy)) + count;
                    string gate = _data.Paths[shape].Lane;
                    gates[gate] = gates.GetValueOrDefault(gate) + count;
                    seen.Add(shape);
                    if (!sources.TryGetValue(shape, out var entries)) sources[shape] = entries = [];
                    var source = new GarrisonRouteSource(level, route, enemy);
                    entries[source] = entries.GetValueOrDefault(source) + 1;
                }
            foreach (int shape in seen) occurrences[shape] = occurrences.GetValueOrDefault(shape) + 1;
            scenarios.Add(counts);
            gateTotals.Add(gates);
        }

        // If the reader reports a complete list but no group has exactly that list,
        // its supersets can still suggest candidates, never a confirmed route.
        var lines = sources.Select(kv => new GarrisonRoundRoute(_data.Paths[kv.Key],
            (rosterComplete && !exact) || occurrences[kv.Key] != matches.Count || _data.Paths[kv.Key].Approx,
            kv.Value.Select(s => s.Key with { Candidate = s.Value != matches.Count || (rosterComplete && !exact) })
                .OrderBy(s => s.Enemy).ThenBy(s => s.Level).ThenBy(s => s.Route).ToArray())
            {
                Enemies = kv.Value.Keys.Select(s => s.Enemy).Distinct().ToDictionary(name => name,
                    name => Range(scenarios, (kv.Key, name))),
                Overlaps = sources.Keys.Any(other => other != kv.Key && SharesSegment(_data.Paths[kv.Key], _data.Paths[other]))
            }).OrderBy(l => l.Path.Lane == "upper" ? 0 : 1).ThenBy(l => l.Path.Flying)
              .ThenBy(l => l.Sources.Min(s => int.Parse(s.Route.Split('/')[1]))).ToArray();
        return new(lines.Any(l => l.Candidate) ? GarrisonRouteStatus.Candidates : GarrisonRouteStatus.Confirmed,
            lines, matches[0].Board.Grid is int grid ? _data.Grids[grid] : null)
        {
            GateTotals = gateTotals.SelectMany(g => g.Keys).Distinct().ToDictionary(g => g, g => Range(gateTotals, g)),
            RosterMismatch = rosterComplete && !exact
        };
    }

    private static SpawnCount Range<TKey>(List<Dictionary<TKey, int>> scenarios, TKey key) where TKey : notnull
    {
        int min = int.MaxValue, max = 0;
        foreach (var scenario in scenarios)
        {
            int count = scenario.GetValueOrDefault(key);
            min = Math.Min(min, count);
            max = Math.Max(max, count);
        }
        return new(min, max);
    }

    internal static bool SharesSegment(GarrisonRoundPath first, GarrisonRoundPath second)
    {
        foreach (var part in first.Segments)
        for (int i = 1; i < part.Length; i++)
        {
            var a = part[i - 1];
            var b = part[i];
            if (a == b) continue;
            int dx = b.Col - a.Col, dy = b.Row - a.Row;
            foreach (var other in second.Segments)
            for (int j = 1; j < other.Length; j++)
            {
                var c = other[j - 1];
                var d = other[j];
                if (dx * (c.Row - a.Row) != dy * (c.Col - a.Col) ||
                    dx * (d.Row - a.Row) != dy * (d.Col - a.Col)) continue;
                int u = dx == 0 ? a.Row : a.Col, v = dx == 0 ? b.Row : b.Col;
                int w = dx == 0 ? c.Row : c.Col, z = dx == 0 ? d.Row : d.Col;
                if (Math.Max(Math.Min(u, v), Math.Min(w, z)) < Math.Min(Math.Max(u, v), Math.Max(w, z))) return true;
            }
        }
        return false;
    }
}
