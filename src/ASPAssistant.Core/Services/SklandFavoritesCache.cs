using System.Text.Json;

namespace ASPAssistant.Core.Services;

public sealed record SklandFavoritesSnapshot(string Account, string Season, DateTimeOffset Updated,
    IReadOnlyList<SklandBrowseGuide> Guides);

public sealed class SklandFavoritesCache(string path)
{
    // Keep the latest complete account/season only. Store shared season resources once per source page.
    public SklandFavoritesSnapshot? Read()
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement.Clone();
            var resources = root.GetProperty("resources");
            var guides = root.GetProperty("entries").EnumerateArray().Select(entry =>
                SklandGuideCatalogue.ReadGuide(entry.GetProperty("item"), resources[entry.GetProperty("resource").GetInt32()]) with
                {
                    IsCollected = true,
                    IsLiked = entry.GetProperty("liked").GetBoolean()
                }).ToArray();
            return new(root.GetProperty("account").GetString()!, root.GetProperty("season").GetString()!,
                root.GetProperty("updated").GetDateTimeOffset(), guides);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
            or InvalidOperationException or IndexOutOfRangeException or FormatException or ArgumentOutOfRangeException or OverflowException)
        {
            // A missing, truncated or incompatible local snapshot can be rebuilt from the online list.
            return null;
        }
    }

    public void Save(SklandFavoritesSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var indices = new Dictionary<JsonElement, int>();
        var resources = new List<JsonElement>();
        var entries = snapshot.Guides.Select(guide =>
        {
            if (!indices.TryGetValue(guide.Resources, out int index))
            {
                indices.Add(guide.Resources, index = resources.Count);
                resources.Add(guide.Resources);
            }
            return new { item = guide.Item, resource = index, liked = guide.IsLiked };
        }).ToArray();
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = File.Create(temporary))
                JsonSerializer.Serialize(file, new { account = snapshot.Account, season = snapshot.Season,
                    updated = snapshot.Updated, resources, entries });
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }

    public void Clear() => File.Delete(path);
}
