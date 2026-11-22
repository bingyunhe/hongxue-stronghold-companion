using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using FluentAssertions;

namespace ASPAssistant.Tests.Services;

public class PlacementPlannerTests
{
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "TestData", "Regulation");

    private static readonly string[] UrsusPool =
        ["塔露拉", "Mon3tr", "机械师", "乌啾", "维伊", "冻河", "折桠", "奥尔佳", "烈夏", "夏栎", "角峰", "霜叶"];

    private static async Task<(PlacementPlanner Planner, GarrisonBoardData Board)> LoadAsync()
    {
        var store = new JsonDataStore(DataDir);
        var ops = (await store.LoadOperatorsAsync()).ToDictionary(o => o.Name);
        var (covenants, _) = await store.LoadCovenantsAsync();
        var board = (await GarrisonBoardStore.LoadAsync(DataDir))!;
        return (new PlacementPlanner(ops, covenants), board);
    }

    private static PlacementOptions Fast(int seed = 1) => new() { Iterations = 3000, Restarts = 3, Seed = seed };

    [Theory]
    [InlineData(PlacementMode.Combat)]
    [InlineData(PlacementMode.Stacking)]
    public async Task WaveComparisonKeepsRosterPinnedEliteAndManualScore(PlacementMode mode)
    {
        var (planner, board) = await LoadAsync();
        var map = board.Find("palace_arrows")!;
        var pool = UrsusPool.Select(n => new PlacementCandidate(n, false)).Append(new("冻河", true, 1)).ToList();
        var pin = new PlacementPin(new BoardCell(2, 3), Facing.Up);
        var options = Fast() with { Mode = mode, FieldCount = 9, WaveRound = 6,
            Pins = new Dictionary<string, PlacementPin> { ["冻河#2"] = pin } };
        var normal = planner.Plan(map, pool, options);
        var boss = planner.Plan(map, pool, options with { WaveRound = 14 });
        boss.Placed.Select(p => p.Key).Should().BeEquivalentTo(normal.Placed.Select(p => p.Key));
        boss.Placed.Should().HaveCount(9);
        boss.Find("冻河#2")!.Cell.Should().Be(pin.Cell);
        boss.Find("冻河#2")!.Facing.Should().Be(pin.Facing);
        var placed = normal.Placed.ToDictionary(p => p.Key, p => (p.Cell, p.Facing));
        planner.Evaluate(map, pool, placed, options).Total.Should().Be(normal.Total);
        var key = normal.Placed.First(p => !p.IsPinned).Key;
        var alternative = planner.Alternatives(map, pool, placed, key, options).First();
        placed[key] = (alternative.Cell, alternative.Facing);
        planner.Evaluate(map, pool, placed, options).Total.Should().BeApproximately(normal.Total + alternative.Delta, 0.15);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Evaluate_BalancedWeightsDamageInRangeUsingTheOperatorsForm(bool elite)
    {
        var (planner, board) = await LoadAsync();
        var map = board.Find("desert_laser")!;
        var placement = new Dictionary<string, (BoardCell, Facing)> { ["水灯心"] = (new(3, 2), Facing.Right) };
        PlacementCandidate[] pool = [new("水灯心", elite)];

        double Score(PlacementMode mode) => planner.Evaluate(map, pool, placement, new() { Mode = mode }).Combat;

        Score(PlacementMode.Balanced).Should().BeGreaterThan(Score(PlacementMode.Combat));
        Score(PlacementMode.Stacking).Should().Be(Score(PlacementMode.Combat));
    }

    [Fact]
    public async Task HiddenCoreCombatKeepsTheOriginalLayoutWhileFinalAssaultChangesIt()
    {
        var (planner, board) = await LoadAsync();
        var map = board.Find("snow")!;
        var unsampled = new GarrisonMap("reference", map.Name, map.FirstColumn, map.Cells, map.Routes);
        string[] names = ["涤火杰西卡", "塞雷娅", "白铁", "酒神", "慕斯", "罗比菈塔", "凯瑟琳", "哈洛德", "水灯心", "烛煌"];
        var pool = names.Select(n => new PlacementCandidate(n, false)).ToArray();
        var options = new PlacementOptions { Mode = PlacementMode.Combat, FieldCount = 8 };
        var original = planner.Plan(unsampled, pool, options);
        var hidden = planner.Plan(map, pool, options with { WaveRound = 15 });
        var boss = planner.Plan(map, pool, options with { WaveRound = 14 });
        var layout = original.Placed.Select(p => (p.Key, p.Cell, p.Facing)).ToList();
        hidden.Placed.Select(p => (p.Key, p.Cell, p.Facing)).Should().BeEquivalentTo(layout);
        boss.Placed.Select(p => (p.Key, p.Cell, p.Facing)).Should().NotBeEquivalentTo(layout);
        hidden.Checks.Should().Contain(c => c.IsHint && c.Text.Contains("隐秘核心"));
    }

    [Fact]
    public async Task Plan_PicksEightFromTwelveAndHoldsBothRoutes()
    {
        var (planner, board) = await LoadAsync();
        var map = board.Find("palace_arrows")!;
        var plan = planner.Plan(map, UrsusPool.Select(n => new PlacementCandidate(n, false)).ToList(), Fast());

        plan.Placed.Should().HaveCount(8);
        plan.Bench.Should().HaveCount(4);
        plan.Placed.Select(p => p.Cell).Should().OnlyHaveUniqueItems();
        foreach (var p in plan.Placed)
            GarrisonMap.CanDeploy(map.At(p.Cell), planner.IsMelee(p.Name)).Should().BeTrue(p.Name);
        plan.ActiveCovenants.Should().Contain("乌萨斯");
        // Both routes have someone blocking.
        foreach (var route in map.Routes)
            plan.Placed.Should().Contain(p => route.Contains(p.Cell));
        plan.Checks.Should().Contain(c => c.Text.StartsWith("下路阻挡"));
    }

    [Fact]
    public async Task Plan_KeepsPinnedOperators()
    {
        var (planner, board) = await LoadAsync();
        var map = board.Find("palace_arrows")!;
        var pin = new PlacementPin(new BoardCell(2, 3), Facing.Up);
        var options = Fast() with { Pins = new Dictionary<string, PlacementPin> { ["塔露拉"] = pin } };
        var plan = planner.Plan(map, UrsusPool.Select(n => new PlacementCandidate(n, false)).ToList(), options);

        var t = plan.Find("塔露拉");
        t.Should().NotBeNull();
        t!.Cell.Should().Be(pin.Cell);
        t.Facing.Should().Be(Facing.Up);
        t.IsPinned.Should().BeTrue();
        plan.Placed.Should().HaveCount(8);
    }

    [Fact]
    public async Task Evaluate_AndAlternatives_ScoreAGivenArrangement()
    {
        var (planner, board) = await LoadAsync();
        var map = board.Find("palace_arrows")!;
        var pool = UrsusPool.Select(n => new PlacementCandidate(n, false)).ToList();
        var plan = planner.Plan(map, pool, Fast());
        var placement = plan.Placed.ToDictionary(p => p.Name, p => (p.Cell, p.Facing));

        planner.Evaluate(map, pool, placement, Fast()).Total.Should().BeApproximately(plan.Total, 0.2);
        var alts = planner.Alternatives(map, pool, placement, plan.Placed[0].Name, Fast());
        alts.Should().NotBeEmpty();
        alts.Should().OnlyContain(a => !placement.Values.Select(v => v.Cell).Contains(a.Cell));
    }

    [Fact]
    public async Task Plan_WorksOnEveryMap()
    {
        var (planner, board) = await LoadAsync();
        var pool = UrsusPool.Select(n => new PlacementCandidate(n, false)).ToList();
        foreach (var map in board.Maps)
        {
            var plan = planner.Plan(map, pool, new PlacementOptions { Iterations = 800, Restarts = 1 });
            plan.Placed.Should().HaveCount(8, map.Id);
        }
    }

    [Fact]
    public async Task PoolBuilder_GroupsTrackedExtensionAndOther()
    {
        var ops = (await new JsonDataStore(DataDir).LoadOperatorsAsync()).ToDictionary(o => o.Name);
        var tracked = new List<TrackingEntry>
        {
            new() { Name = "塔露拉", Type = TrackingType.Operator },
            new() { Name = "冻河", Type = TrackingType.Operator },
            new() { Name = "折桠", Type = TrackingType.Operator, Role = TrackingRole.Transition, Stages = "前期", ExpiresAt = 3 },
        };
        var state = new DeployPoolState { Owned = ["塔露拉", "夏栎"], Extra = ["夏栎"] };
        var groups = DeployPoolBuilder.Build(tracked, ops, state);

        groups.Select(g => g.Kind).Should().Equal(DeployPoolGroupKind.Final, DeployPoolGroupKind.Transition,
            DeployPoolGroupKind.Extension, DeployPoolGroupKind.Other);
        groups[0].Items.Should().Contain(i => i.Name == "塔露拉" && i.Owned);
        groups[0].Items.Should().Contain(i => i.Name == "冻河" && !i.Owned);
        groups[2].Items.Should().Contain(i => i.Name == "维伊");          // 乌萨斯, not tracked
        groups[2].Items.Should().NotContain(i => i.Name == "折桠");
        groups[2].Items.Select(i => i.Operator!.Tier).Should().BeInAscendingOrder();   // banded by tier in the UI
        groups[3].Items.Should().ContainSingle(i => i.Name == "夏栎" && i.Removable);
    }

    [Fact]
    public async Task Plan_KeepsAHealerOnTheField()
    {
        var (planner, board) = await LoadAsync();
        // Damage-heavy pool with a single healer: the healer still goes on and reaches a block point.
        string[] pool = ["塔露拉", "机械师", "维伊", "冻河", "折桠", "奥尔佳", "烈夏", "角峰", "霜叶", "赫拉格", "乌啾"];
        foreach (var id in new[] { "palace_arrows", "desert_crates", "city" })
        {
            var plan = planner.Plan(board.Find(id)!, pool.Select(n => new PlacementCandidate(n, false)).ToList(), Fast());
            plan.Placed.Should().Contain(p => p.Name == "乌啾", id);
            plan.Checks.Should().Contain(c => c.Text.StartsWith("奶 ") && c.Ok, id);
        }
    }

    private static readonly string[] VictoriaPool =
        ["涤火杰西卡", "塞雷娅", "维娜·维多利亚", "白铁", "凯瑟琳", "哈洛德", "酒神", "慕斯", "罗比菈塔", "云迹", "空弦", "星源"];

    [Fact]
    public async Task Plan_PutsJessicaInFrontOfSaria()
    {
        var (planner, board) = await LoadAsync();
        // On 沙漠（中央木箱） both routes meet at one cell and 杰西卡 has to hold it, so 塞雷娅 copies 罗比菈塔
        // there instead; the palace has room for 塞雷娅 → 杰西卡 on a line.
        var crates = planner.Plan(board.Find("desert_crates")!, VictoriaPool.Select(n => new PlacementCandidate(n, false)).ToList(), Fast(2));
        crates.Find("塞雷娅")!.Notes.Should().Contain(n => n.StartsWith("复制身前"));
        foreach (var id in new[] { "palace" })
        {
            var plan = planner.Plan(board.Find(id)!, VictoriaPool.Select(n => new PlacementCandidate(n, false)).ToList(), Fast(2));
            var saria = plan.Find("塞雷娅");
            var jessica = plan.Find("涤火杰西卡");
            saria.Should().NotBeNull(id);
            jessica.Should().NotBeNull(id);
            var (fx, fy) = PlacementCells.Vector(saria!.Facing);
            jessica!.Cell.Should().Be(new BoardCell(saria.Cell.Col + fx, saria.Cell.Row + fy), $"{id}: 塞雷娅复制身前杰西卡的特质");
            saria.Notes.Should().Contain(n => n.StartsWith("复制身前 涤火杰西卡"));
        }
    }

    [Fact]
    public async Task Plan_PlacesDuplicateCopiesButCountsCovenantsOnce()
    {
        var (planner, board) = await LoadAsync();
        var map = board.Find("palace_arrows")!;
        // Two 冻河 and two 维伊: both copies can go on the field, but 乌萨斯 counts different operators.
        var pool = new List<PlacementCandidate>
        {
            new("塔露拉", false), new("机械师", false), new("Mon3tr", false), new("折桠", false),
            new("冻河", false), new("冻河", false, 1), new("维伊", false), new("维伊", false, 1),
        };
        var plan = planner.Plan(map, pool, Fast());
        plan.Placed.Should().HaveCount(8);
        plan.Placed.Select(p => p.Key).Should().OnlyHaveUniqueItems().And.Contain(["冻河", "冻河#2", "维伊#2"]);
        plan.Placed.Where(p => p.Name == "冻河").Should().HaveCount(2);
        // 塔露拉, 冻河, 维伊, 折桠 → 4 different 乌萨斯 operators: not 6, so no "乌萨斯 6" check.
        plan.Checks.Should().NotContain(c => c.Text == "乌萨斯 6");
        plan.Checks.Should().Contain(c => c.Text.StartsWith("乌萨斯 4"));
    }

    [Fact]
    public void PoolState_TracksCopies()
    {
        var state = new DeployPoolState();
        state.SetCount("冻河", 2);
        state.CountOf("冻河").Should().Be(2);
        state.IsOwned("冻河").Should().BeTrue();
        state.SetCount("冻河", 1);
        state.Counts.Should().NotContainKey("冻河");
        state.SetCount("冻河", 0);
        state.IsOwned("冻河").Should().BeFalse();
    }
}
