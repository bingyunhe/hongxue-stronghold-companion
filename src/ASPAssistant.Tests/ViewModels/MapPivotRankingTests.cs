using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;

namespace ASPAssistant.Tests.ViewModels;

public class MapPivotRankingTests
{
    [Fact]
    public async Task ReadyFrontlineAndHealingOutrankSevenHeldWithAMissingSoleTank()
    {
        string[] common = ["治疗", "一", "二", "三", "四"];
        var names = common.Concat(["五", "六", "持有盾", "缺盾", "维伊", "灰喉", "禁用"]).ToArray();
        var operators = names.Select(n => new Operator { Name = n, Tier = n == "缺盾" ? 5 : 3,
            Profile = new() { Roles = n == "治疗" ? ["奶位"] : n.EndsWith("盾") ? ["盾位"] : [] } }).ToArray();
        var calc = new CovenantCalculator(operators, [], new Dictionary<string, string>());
        var library = new LineupViewModel(new LineupStore(Path.GetTempPath(), calc.EquipmentRules), calc);
        Lineup Team(string name, params string[] others) => new() { Name = name,
            Slots = common.Concat(others).Select(n => new LineupSlot { OperatorName = n }).ToList() };
        var ready = Team("可直接转", "持有盾", "维伊", "灰喉");
        library.LoadLineups([Team("七缺一", "五", "六", "缺盾"), ready, Team("禁用队", "禁用")]);
        var bans = new BanViewModel(); bans.SetBans(["禁用"]);
        var vm = new LineupPlannerViewModel(new(operators, []), operators, bans, calc.EquipmentRules);
        var held = common.Concat(["持有盾", "五", "六"]).ToDictionary(n => n, _ => 1);
        var ranked = await vm.FindMapPivotsAsync(library, held, new HashSet<string>(), 8, 4, default);
        Assert.Equal(new[] { "可直接转", "七缺一" }, ranked.Select(c => c.Lineup.Name));
        Assert.Equal(6, ranked[0].Held.Length);
        Assert.Equal(7, ranked[1].Held.Length);
        Assert.Contains("前排（待抓缺盾）", ranked[1].Reason);
        Assert.Contains("超出当前终端：缺盾", ranked[1].Reason);
        Assert.Contains("SP3", ranked[0].Replay);
        Assert.NotNull(ranked[0].PurchaseRate);
        Assert.Null(ranked[1].PurchaseRate);
    }

    [Fact]
    public async Task SavedPivotAcceptsGreyTransferButSkipsAbsentTransfer()
    {
        Operator[] ops = [new() { Name = "角峰", CoreCovenants = ["谢拉格"] }];
        CovenantInfo[] covenants = [new() { Name = "精准", BondType = "REGULAR", ActivateCount = 2 }];
        var calculator = new CovenantCalculator(ops, covenants, new Dictionary<string, string> { ["精准狙击镜"] = "精准" });
        var library = new LineupViewModel(new LineupStore(Path.GetTempPath(), calculator.EquipmentRules), calculator);
        library.LoadLineups([new() { Name = "已存转职", Slots = [new() { OperatorName = "角峰", Equipments = ["变形同构体", "精准狙击镜"] }] }]);
        var bans = new BanViewModel();
        var vm = new LineupPlannerViewModel(new(ops, covenants), ops, bans, calculator.EquipmentRules);
        var held = new Dictionary<string, int> { ["角峰"] = 1 };
        bans.SetCovenantBans(["精准"]);
        Assert.Contains(await vm.FindMapPivotsAsync(library, held, new HashSet<string>(), 8, 4, default), c => c.Lineup.Name == "已存转职");
        bans.SetCovenantBans([], new HashSet<string> { "精准" });
        Assert.DoesNotContain(await vm.FindMapPivotsAsync(library, held, new HashSet<string>(), 8, 4, default), c => c.Lineup.Name == "已存转职");
    }

    [Fact]
    public async Task ManualGoalsAndSavedCoreCardsAffectPivotChoices()
    {
        var names = Enumerable.Range(1, 9).Select(i => "干员" + i).ToArray();
        var ops = names.Select(n => new Operator { Name = n, CoreCovenants = ["炎"] }).ToArray();
        CovenantInfo[] covenants = [new() { Name = "炎", BondType = "SEASON", ActivateCount = 3 }];
        var calc = new CovenantCalculator(ops, covenants, new Dictionary<string, string>());
        var library = new LineupViewModel(new LineupStore(Path.GetTempPath(), calc.EquipmentRules), calc);
        var core = new Lineup { Name = "缺主力", Slots = names.Take(8).Select(n => new LineupSlot { OperatorName = n }).ToList() };
        core.Slots[7].Tags.Add(LineupTag.阵容核心);
        var alternate = new Lineup { Name = "主力齐", Slots = names.Take(7).Append(names[8]).Select(n => new LineupSlot { OperatorName = n }).ToList() };
        alternate.Slots.Add(new() { OperatorName = names[7], IsReserve = true });
        library.LoadLineups([core, alternate]);
        var vm = new LineupPlannerViewModel(new(ops, covenants), ops, new(), calc.EquipmentRules);
        vm.AllCovenants.Single(g => g.Name == "炎").ToggleLevel1Command.Execute(null);
        var ranked = await vm.FindMapPivotsAsync(library, names.Take(7).ToDictionary(n => n, _ => 1), new HashSet<string>(), 8, 4, default);
        var saved = ranked.Where(c => c.Source == "我的阵容").ToArray();
        Assert.Equal(new[] { "主力齐", "缺主力" }, saved.Select(c => c.Lineup.Name));
        Assert.Contains("核心或必带缺卡：干员8", saved[1].Reason);
        Assert.Contains(ranked, c => c.Source == "手动规划" && c.Lineup.TargetCovenants.Any(g => g.Name == "炎"));
    }

    [Fact]
    public async Task ComparableHandsUseRecordedPurchaseRatesAndCountCopiesOnce()
    {
        string[] names = ["罗比菈塔", "维伊", "灰喉", "持有"];
        var ops = names.Select(n => new Operator { Name = n, Tier = 2 }).ToArray();
        var calc = new CovenantCalculator(ops, [], new Dictionary<string, string>());
        var library = new LineupViewModel(new LineupStore(Path.GetTempPath(), calc.EquipmentRules), calc);
        library.LoadLineups(names.Take(3).Select(n => new Lineup { Name = n, Slots =
            [new() { OperatorName = "持有" }, new() { OperatorName = n }] }).Append(new Lineup { Name = "两份",
            Slots = [new() { OperatorName = "持有" }, new() { OperatorName = "持有" }] }));
        var vm = new LineupPlannerViewModel(new(ops, []), ops, new(), calc.EquipmentRules);
        var ranked = await vm.FindMapPivotsAsync(library, new Dictionary<string, int> { ["持有"] = 1 }, new HashSet<string>(), 8, 4, default);
        var measured = ranked.Where(c => c.PurchaseRate.HasValue).ToArray();
        Assert.Equal(3, measured.Length);
        Assert.True(measured[0].PurchaseRate > measured[^1].PurchaseRate);
        Assert.Equal(measured.Select(c => c.PurchaseRate).OrderDescending(), measured.Select(c => c.PurchaseRate));
        var doubled = ranked.Single(c => c.Lineup.Name == "两份");
        Assert.Single(doubled.Held); Assert.Single(doubled.Missing);
    }
}
