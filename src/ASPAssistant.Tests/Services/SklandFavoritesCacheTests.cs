using System.Text.Json;
using System.Text.Json.Nodes;
using ASPAssistant.Core.Services;

namespace ASPAssistant.Tests.Services;

public class SklandFavoritesCacheTests
{
    [Fact]
    public void SnapshotRestoresDetailsAndReactionsAndCanReplaceWithAnEmptySeason()
    {
        var directory = Directory.CreateTempSubdirectory("skland-favorites-");
        try
        {
            var path = Path.Combine(directory.FullName, "snapshot.json");
            var cache = new SklandFavoritesCache(path);
            Assert.Null(cache.Read());
            File.WriteAllText(path, "{");
            Assert.Null(cache.Read());
            var data = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "skland-guide-import.json")))!["data"]!;
            var post = data["guide"]!;
            post["rts"] = new JsonObject { ["collected"] = "1", ["liked"] = "2" };
            var item = JsonSerializer.SerializeToElement(post);
            var resource = SklandGuideCatalogue.ResourceFor(JsonSerializer.SerializeToElement(data), item.GetProperty("guide"));
            var guide = SklandGuideCatalogue.ReadGuide(item, resource) with { IsLiked = true };
            var snapshot = new SklandFavoritesSnapshot("first", "season-one", DateTimeOffset.Now, [guide]);
            cache.Save(snapshot);
            var saved = cache.Read()!;
            Assert.Equal((snapshot.Account, snapshot.Season, snapshot.Updated), (saved.Account, saved.Season, saved.Updated));
            var card = Assert.Single(saved.Guides);
            Assert.True(card.IsCollected && card.IsLiked);
            Assert.Equal(guide.Id, card.Id);
            Assert.Equal(guide.Formation!.Operators, card.Formation!.Operators);
            Assert.Equal(guide.Resources.GetRawText(), card.Resources.GetRawText());
            cache.Save(new("second", "season-two", snapshot.Updated, []));
            Assert.Empty(cache.Read()!.Guides);
            Assert.Equal("second", cache.Read()!.Account);
            cache.Clear();
            Assert.Null(cache.Read());
        }
        finally { directory.Delete(true); }
    }
}
