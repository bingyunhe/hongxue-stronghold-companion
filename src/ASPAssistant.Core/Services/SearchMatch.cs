// Added for ASP Assistant Arknights on 2026-11-13; see NOTICE.
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Text;
using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Services;

/// <summary>
/// Search by name or pinyin initials (冻河 ← "dh", 集结指示 ← "jjzs"). The initials come from data/search_keys.json,
/// built by data/build_search_keys.py. Player-written lineup names use the bundled character initials.
/// </summary>
public static class SearchMatch
{
    private static IReadOnlyDictionary<string, string> _initials = new Dictionary<string, string>();
    private static string _hanInitials = "";
    private static readonly ConditionalWeakTable<string, string> LineupKeys = new();

    public static void Load(IReadOnlyDictionary<string, string> initials) => _initials = initials;

    public static void LoadFile(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("initials", out var map)) return;
            _initials = map.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
            _hanInitials = doc.RootElement.TryGetProperty("hanInitials", out var han) ? han.GetString()! : "";
            LineupKeys.Clear();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLogger.Warn("SearchMatch", $"Failed to load {path}: {ex.Message}");
        }
    }

    public static string InitialsOf(string text) => _initials.GetValueOrDefault(text, "");

    public static Operator? ExactOrOnlyOperator(IEnumerable<Operator> visible, string query)
    {
        query = query.Trim();
        Operator? single = null;
        int count = 0;
        foreach (var op in visible)
        {
            if (string.Equals(query, op.Name, StringComparison.OrdinalIgnoreCase)) return op;
            single = op;
            count++;
        }
        return count == 1 ? single : null;
    }

    public static bool MatchesLineupName(string query, string name)
    {
        if (Matches(query, name)) return true;
        var prefix = query.Replace(" ", "").Trim();
        if (prefix.Length == 0 || !prefix.All(char.IsAsciiLetterOrDigit)) return false;
        var key = LineupKeys.GetValue(name, static value =>
        {
            var initials = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsAsciiLetterOrDigit(c)) initials.Append(c);
                else if ((uint)(c - 0x3400) < _hanInitials.Length && _hanInitials[c - 0x3400] != '.')
                    initials.Append(_hanInitials[c - 0x3400]);
            }
            return initials.ToString();
        });
        return key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="query"/> is empty, part of any text, or the start of any text's initials.</summary>
    public static bool Matches(string query, params string?[] texts)
    {
        query = query.Trim();
        if (query.Length == 0) return true;
        var compactQuery = query.Replace("·", "");
        var prefix = query.Replace(" ", "");
        var latin = prefix.Length > 0 && prefix.All(char.IsAsciiLetterOrDigit);
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text)) continue;
            if (text.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Contains('·') && text.Replace("·", "").Contains(compactQuery, StringComparison.OrdinalIgnoreCase)) return true;
            if (latin && InitialsOf(text).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
