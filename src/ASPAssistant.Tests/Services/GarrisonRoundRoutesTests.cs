using System.Diagnostics;
using System.Text.Json;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;

namespace ASPAssistant.Tests.Services;

public sealed class GarrisonRoundRoutesTests
{
    internal static string DataDirectory => Path.Combine(AppContext.BaseDirectory, "TestData", "Regulation");

    [Theory]
    [InlineData("规程等级7", 6, 6)]
    [InlineData("规程等级7", 14, 14)]
    [InlineData("规程等级7", 15, 15)]
    [InlineData("规程等级1", 15, 0)]
    [InlineData(null, 14, 0)]
    public async Task PlacementWaveUsesTheRecognizedModesRound(string? mode, int round, int expected)
    {
        var store = (await GarrisonRoundRouteStore.LoadAsync(DataDirectory))!;
        Assert.Equal(expected, store.PlacementWaveRound(mode, round));
    }

    [Fact]
    public async Task TwoMatchingGroupsWithTheSamePathProduceOnlyOneLowerGroundLine()
    {
        var store = (await GarrisonRoundRouteStore.LoadAsync(DataDirectory))!;
        var routes = store.Find("desert_crates", "规程等级7", 1, ["源石虫", "普通囚犯"]);
        Assert.Equal(GarrisonRouteStatus.Confirmed, routes.Status);
        var line = Assert.Single(routes.Routes);
        Assert.False(line.Candidate);
        Assert.False(line.Path.Flying);
        Assert.Equal("lower", line.Path.Lane);
        Assert.Equal(new BoardCell(7, 3), Assert.Single(line.Path.Segments)[0]);
        Assert.Equal(new BoardCell(-1, 3), Assert.Single(line.Path.Segments)[^1]);
        Assert.Contains(new BoardCell(6, 0), Assert.Single(line.Path.Segments));

        // Both names are present on all ten maps, with a shape selected for each map.
        var maps = (await GarrisonBoardStore.LoadAsync(DataDirectory))!.Maps;
        Assert.Equal(10, maps.Count);
        foreach (var map in maps)
            Assert.Single(store.Find(map.Id, "规程等级7", 1, ["普通囚犯", "源石虫"]).Routes);

        var timer = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
            _ = store.Find("desert_crates", "规程等级7", 1, ["源石虫", "普通囚犯"]);
        File.WriteAllText(Path.Combine(TestOutput.Directory, "round-route-lookup-performance.json"),
            JsonSerializer.Serialize(new { Lookups = 1000, ElapsedMs = timer.Elapsed.TotalMilliseconds }));
    }

    [Fact]
    public async Task IncompleteRosterAndSeveralPossibleGroupsStayCandidates()
    {
        var store = (await GarrisonRoundRouteStore.LoadAsync(DataDirectory))!;
        var waiting = store.Find("desert_crates", "规程等级7", 1, []);
        Assert.Equal(GarrisonRouteStatus.WaitingForEnemies, waiting.Status);
        var partial = store.Find("desert_crates", "规程等级7", 1, ["源石虫"]);
        Assert.Equal(GarrisonRouteStatus.Candidates, partial.Status);
        Assert.Equal(2, partial.Routes.Count);
        Assert.All(partial.Routes, route => Assert.True(route.Candidate));
        Assert.Contains(partial.Routes, route => route.Path.Flying);
        var unknown = store.Find("desert_crates", "规程等级7", 1, ["未匹配"]);
        Assert.Equal(GarrisonRouteStatus.Unmatched, unknown.Status);
        Assert.NotEmpty(waiting.Routes);
        Assert.Equal(waiting.Routes.Select(r => r.Path), unknown.Routes.Select(r => r.Path));
        Assert.Equal(GarrisonRouteStatus.Uncovered, store.Find("desert_crates", "未知难度", 1, []).Status);
    }
    [Fact]
    public async Task CityAndCactusRetainEnemySpecificOfficialIndices()
    {
        var store = (await GarrisonRoundRouteStore.LoadAsync(DataDirectory))!;
        var city = store.Find("city", "规程等级13", 1, ["源石虫", "猎狗pro"]);
        var lower = Assert.Single(city.Routes);
        Assert.False(lower.Candidate);
        Assert.Equal("lower", lower.Path.Lane);
        Assert.Contains(new GarrisonRouteSource("level_act3autochess_01", "routes/1", "猎狗pro"), lower.Sources);

        var band = store.Find("desert_cactus", "终极模拟", 6, ["残党乐团圆号手", "残党乐团鼓手", "残党乐团管乐家"]);
        Assert.Equal(GarrisonRouteStatus.Confirmed, band.Status);
        Assert.Equal(2, band.Routes.Count);
        var upper = Assert.Single(band.Routes.Where(r => r.Path.Lane == "upper"));
        Assert.All(upper.Sources, s => Assert.Equal("残党乐团圆号手", s.Enemy));
        Assert.Equal(new[] { "routes/4", "routes/5" }, upper.Sources.Select(s => s.Route));
        Assert.Equal(4, Assert.Single(band.Routes.Where(r => r.Path.Lane == "lower")).Sources.Count);

        var partial = store.Find("city", "规程等级13", 1, ["源石虫"], rosterComplete: false);
        Assert.Contains(partial.Routes, r => !r.Candidate && !r.Path.Flying);
        Assert.Contains(partial.Routes, r => r.Candidate && r.Path.Flying);
        Assert.NotEmpty(store.Find("city", "规程等级13", 6, []).Routes);
        Assert.Equal(GarrisonRouteStatus.Uncovered, store.Find("city", "标准模拟", 1, []).Status);
    }

    [Fact]
    public async Task RoundNinePreviewNarrowsToBothGroundLanesAndSurvivesMisread()
    {
        var store = (await GarrisonRoundRouteStore.LoadAsync(DataDirectory))!;
        var preview = store.Find("desert_cactus", "规程等级13", 9, []);
        var wrong = store.Find("desert_cactus", "规程等级13", 9, ["深池逐火战士", "深池漆小锐战"]);
        var confirmed = store.Find("desert_cactus", "规程等级13", 9, ["深池逐火战士", "深池逐火精锐战士", "深池逐火护卫"]);
        Assert.Equal(GarrisonRouteStatus.Unmatched, wrong.Status);
        Assert.Equal(preview.Routes.Select(r => r.Path), wrong.Routes.Select(r => r.Path));
        Assert.All(wrong.Routes, r => { Assert.True(r.Candidate); Assert.Empty(r.Sources); });
        Assert.Equal(GarrisonRouteStatus.Confirmed, confirmed.Status);
        Assert.Equal(new[] { "lower", "upper" }, confirmed.Routes.Select(r => r.Path.Lane).Order().ToArray());
        Assert.All(confirmed.Routes, r => Assert.Contains(preview.Routes, p => p.Path == r.Path));
        Assert.True(preview.Routes.Count > confirmed.Routes.Count);
        Assert.Equal(new SpawnCount(6, 6), confirmed.GateTotals["upper"]);
        Assert.Equal(new SpawnCount(14, 14), confirmed.GateTotals["lower"]);
        Assert.All(confirmed.Routes, r => Assert.True(r.Overlaps));
        var upper = confirmed.Routes[0].Enemies;
        var lower = confirmed.Routes[1].Enemies;
        Assert.Equal(new SpawnCount(4, 4), upper["深池逐火战士"]);
        Assert.Equal(new SpawnCount(2, 2), upper["深池逐火精锐战士"]);
        Assert.Equal(new SpawnCount(6, 6), lower["深池逐火战士"]);
        Assert.Equal(new SpawnCount(4, 4), lower["深池逐火精锐战士"]);
        Assert.Equal(new SpawnCount(4, 4), lower["深池逐火护卫"]);

        var fly = store.Find("desert_cactus", "规程等级13", 9, ["妖怪", "妖怪MKII", "威龙"]);
        Assert.Equal(new[] { 10, 2, 7 }, fly.Routes.Select(r => r.Enemies.Values.Sum(n => n.Max)));
        Assert.Equal(new SpawnCount(12, 12), fly.GateTotals["upper"]);
        Assert.Equal(new SpawnCount(2, 2), fly.Routes[1].Enemies["威龙"]);
        Assert.All(fly.Routes, r => Assert.True(r.Path.Flying && r.Overlaps));
        Assert.Empty(wrong.GateTotals);
    }

    [Fact]
    public void SharedLengthTriggersPlaybackButPointCrossingsAndTeleportsDoNot()
    {
        GarrisonRoundPath Path(params BoardCell[][] parts) => new("upper", true, false, parts);
        var first = Path([new BoardCell(0, 0), new BoardCell(4, 4)]);
        Assert.True(GarrisonRoundRouteStore.SharesSegment(first, Path([new BoardCell(5, 5), new BoardCell(2, 2)])));
        Assert.False(GarrisonRoundRouteStore.SharesSegment(first, Path([new BoardCell(0, 4), new BoardCell(4, 0)])));
        Assert.False(GarrisonRoundRouteStore.SharesSegment(first, Path([new BoardCell(4, 4), new BoardCell(6, 6)])));
        Assert.False(GarrisonRoundRouteStore.SharesSegment(first, Path([new BoardCell(0, 0)], [new BoardCell(4, 4)])));
    }
}
