using System.Text.Json;
using System.Diagnostics;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using FluentAssertions;

namespace ASPAssistant.Tests.Services;

public class LineupStageRepairerTests
{
    private static Operator Op(string name, int tier, params string[] covenants) => new() { Name = name, Tier = tier, CoreCovenants = [.. covenants] };
    private static (LineupStageRepairer Repairer, Lineup Source, Lineup Final) Create()
    {
        Operator[] ops = [Op("保留", 1, "炎"), Op("禁用", 2, "炎"), Op("成型替补", 2, "炎"), Op("高阶", 5, "炎"),
            Op("过渡", 2, "维"), Op("同阶", 2, "维"), Op("低阶", 1, "炎")];
        CovenantInfo[] covenants = [new() { Name = "炎", BondType = "SEASON", ActivateCount = 2 }, new() { Name = "维", BondType = "SEASON", ActivateCount = 2 }];
        var source = new Lineup { Slots = [new() { OperatorName = "保留" }, new() { OperatorName = "禁用" }], SklandGuide = new() { Stages =
        [new() { Id = "s", Name = "自定义阶段", StartRound = 8, EndRound = 10, Slots =
            [new() { OperatorName = "保留", IsElite = true, Equipments = ["原装备"], CandidateEquipments = [new() { Name = "候选" }], Tags = [LineupTag.阵容核心],
                GuideLoadout = new() { CharId = "keep", SkillId = "skill", ModuleId = "module" } },
             new() { OperatorName = "禁用", IsElite = true, Equipments = ["不能转交"], GuideLoadout = new() { CharId = "banned" } }],
            RecommendedEquipments = ["阶段推荐"], OperationIdea = "原讲解" },
         new() { Id = "final-id", Name = "最后一战", IsFinal = true, Slots = [new() { OperatorName = "保留" }, new() { OperatorName = "禁用" }] },
         new() { Id = "empty", Name = "自定义空阶段", StartRound = 11, EndRound = 12 }] } };
        var final = JsonSerializer.Deserialize<Lineup>(JsonSerializer.Serialize(source))!;
        final.Slots = [new() { OperatorName = "保留" }, new() { OperatorName = "成型替补" }];
        return (new(ops, new(ops, covenants, new Dictionary<string, string> { ["精准狙击镜"] = "精准" })), source, final);
    }

    [Fact]
    public void RetainedStageWithAbsentTransferCannotBeSaved()
    {
        var (repairer, source, final) = Create();
        var original = source.SklandGuide!.Stages[0].Slots[0];
        original.Equipments = ["变形同构体", "精准狙击镜"];
        var result = repairer.Repair(source, final, new HashSet<string>(), new HashSet<string>(), transformAllowed: new HashSet<string>());
        result.CanSave.Should().BeFalse();
        result.Stages[0].WarningText.Should().Contain("本局不可转入：精准");
        original.Equipments.Should().Equal("变形同构体", "精准狙击镜");
    }

    [Fact]
    public void PreservesStructureSurvivorLoadoutAndTierBoundWithoutTransferringBannedGear()
    {
        var (repairer, source, final) = Create();
        var before = JsonSerializer.Serialize(source);
        var preview = repairer.Repair(source, final, new HashSet<string> { "禁用" }, new HashSet<string>());
        preview.CanSave.Should().BeTrue();
        var stages = preview.Lineup.SklandGuide!.Stages;
        stages.Select(s => (s.Id, s.Name, s.StartRound, s.EndRound)).Should().Equal(source.SklandGuide!.Stages.Select(s => (s.Id, s.Name, s.StartRound, s.EndRound)));
        stages[0].Slots.Select(s => s.OperatorName).Should().Equal("保留", "成型替补");
        stages[0].Slots[0].Should().BeEquivalentTo(source.SklandGuide.Stages[0].Slots[0]);
        stages[0].Slots[0].Should().NotBeSameAs(source.SklandGuide.Stages[0].Slots[0]);
        stages[0].Slots[1].Should().BeEquivalentTo(new LineupSlot { OperatorName = "成型替补" });
        stages[0].OperationIdea.Should().Be("原讲解"); stages[0].RecommendedEquipments.Should().Equal("阶段推荐");
        stages[1].Slots.Should().BeEquivalentTo(final.Slots); stages[2].Slots.Should().BeEmpty();
        JsonSerializer.Serialize(source).Should().Be(before);
        preview.Stages[0].BoundsText.Should().Contain("2 人").And.Contain("2 阶");
        stages[0].Slots[0].Equipments.Clear(); source.SklandGuide.Stages[0].Slots[0].Equipments.Should().Equal("原装备");
    }

    [Fact]
    public void InsufficientEligiblePoolShowsMissingAndCannotSave()
    {
        var (repairer, source, final) = Create();
        var preview = repairer.Repair(source, final, new HashSet<string> { "禁用", "成型替补", "过渡", "同阶", "低阶" }, new HashSet<string>());
        preview.CanSave.Should().BeFalse();
        preview.Stages[0].Missing.Should().Be(1);
        preview.Stages[0].CovenantComparison.Should().Contain("炎 2 → 1").And.Contain("I 档 → 未激活，距 I 档差 1 人");
        preview.Stages[0].WarningText.Should().Contain("缺 1 人");
        preview.Lineup.SklandGuide!.Stages[0].Slots.Select(s => s.OperatorName).Should().Equal("保留");
    }

    [Fact]
    public void CovenantBansAndUnselectedCustomOperatorsAreExcluded()
    {
        Operator[] ops = [Op("旧", 2, "禁盟约"), Op("安全", 1, "炎"), Op("自选", 1, "炎")];
        ops[2].IsCustom = true;
        var repairer = new LineupStageRepairer(ops, new(ops, [], new Dictionary<string, string>()));
        var source = new Lineup { EarlySlots = [new() { OperatorName = "旧" }], Slots = [new() { OperatorName = "安全" }] };
        var preview = repairer.Repair(source, source, new HashSet<string>(), new HashSet<string> { "禁盟约" });
        preview.Lineup.EarlySlots.Select(s => s.OperatorName).Should().Equal("安全");
    }

    [Fact]
    public void DoesNotInventTierWhenAnImportedOperatorIsMissingFromLocalData()
    {
        var (repairer, source, final) = Create();
        source.SklandGuide!.Stages[0].Slots[1].OperatorName = "缺失资料";
        var preview = repairer.Repair(source, final, new HashSet<string>(), new HashSet<string>());
        preview.CanSave.Should().BeFalse(); preview.Stages[0].WarningText.Should().Contain("资料缺失");
    }

    [Fact]
    public void RouteStageAliasesStayConsistentAndCountsCanExceedNine()
    {
        var ops = Enumerable.Range(0, 30).Select(i => Op($"人{i:D2}", 1, "炎")).ToList();
        var source = new Lineup { Slots = [new() { OperatorName = "人29" }],
            EarlySlots = [.. ops.Take(15).Select(o => new LineupSlot { OperatorName = o.Name })],
            EditorStages = [new() { Id = "early", Name = "自定义开局", StartRound = 1, EndRound = 3 }, new() { Id = "final", Name = "自定义成型", IsFinal = true }] };
        var repairer = new LineupStageRepairer(ops, new(ops, [], new Dictionary<string, string>()));
        var banned = ops.Take(10).Select(o => o.Name).ToHashSet();
        var preview = repairer.Repair(source, source, banned, new HashSet<string>());
        preview.CanSave.Should().BeTrue();
        preview.Lineup.EarlySlots.Should().HaveCount(15).And.OnlyContain(s => !banned.Contains(s.OperatorName));
        preview.Lineup.EditorStages![0].Slots.Should().BeEquivalentTo(preview.Lineup.EarlySlots);
        preview.Lineup.EditorStages[1].Slots.Should().BeEquivalentTo(preview.Lineup.Slots);
    }

    [Fact]
    public void CancellationStopsStageSearch()
    {
        var (repairer, source, final) = Create();
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Action run = () => repairer.Repair(source, final, new HashSet<string>(), new HashSet<string>(), cts.Token);
        run.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public async Task RealSeasonDataPreservesBoundsAndRecordsOnDemandSearchCost()
    {
        var data = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "TestData", "Regulation"));
        var operators = await data.LoadOperatorsAsync(); var (covenants, _) = await data.LoadCovenantsAsync();
        var original = operators.Where(o => !o.IsCustom && o.RequiredStrategyId.Length == 0 && o.Tier <= 3).Take(15).ToList();
        original.Should().HaveCount(15);
        var banned = original.Take(8).Select(o => o.Name).ToHashSet();
        var source = new Lineup { Slots = [.. original.Take(8).Select(o => new LineupSlot { OperatorName = o.Name })],
            EditorStages = [.. Enumerable.Range(1, 3).Select(i => new SklandGuideStage { Id = $"s{i}", Name = $"阶段{i}", Slots = [.. original.Select(o => new LineupSlot { OperatorName = o.Name })] }),
                new() { Id = "f", Name = "成型", IsFinal = true }] };
        var final = JsonSerializer.Deserialize<Lineup>(JsonSerializer.Serialize(source))!;
        final.Slots = [.. operators.Where(o => !o.IsCustom && o.RequiredStrategyId.Length == 0 && !banned.Contains(o.Name)).Take(8).Select(o => new LineupSlot { OperatorName = o.Name })];
        var calculator = new CovenantCalculator(operators, covenants, EquipmentTestData.Rules.EquipmentToCovenant.ToDictionary(p => p.Key, p => p.Value));
        var repairer = new LineupStageRepairer(operators, calculator);
        var watch = Stopwatch.StartNew();
        JsonSerializer.Deserialize<Lineup>(JsonSerializer.Serialize(final));
        var copyMs = watch.Elapsed.TotalMilliseconds;
        var samples = new List<double>(); var allocations = new List<long>();
        string fingerprint = "";
        for (int i = 0; i < 3; i++)
        {
            var bytes = GC.GetAllocatedBytesForCurrentThread(); watch.Restart();
            var preview = repairer.Repair(source, final, banned, new HashSet<string>());
            samples.Add(watch.Elapsed.TotalMilliseconds); allocations.Add(GC.GetAllocatedBytesForCurrentThread() - bytes);
            fingerprint = string.Join("|", preview.Lineup.EditorStages!.Select(s => s.Id + ":" + string.Join(",", s.Slots.Select(o => o.OperatorName))));
            preview.CanSave.Should().BeTrue();
            foreach (var stage in preview.Lineup.EditorStages!.Where(s => !s.IsFinal))
            {
                stage.Slots.Should().HaveCount(15).And.OnlyContain(s => !banned.Contains(s.OperatorName));
                stage.Slots.Select(s => s.OperatorName).Distinct().Should().HaveCount(15);
                stage.Slots.Should().OnlyContain(s => operators.Single(o => o.Name == s.OperatorName).Tier <= original.Max(o => o.Tier));
            }
        }
        File.WriteAllText(Path.Combine(TestOutput.Directory, "stage-repair-real-data-performance.json"), JsonSerializer.Serialize(new
        { Stages = 3, PeoplePerStage = 15, MissingPerStage = 8, CopyOnlyMs = copyMs, RepairMs = samples, RepairBytes = allocations, BeamWidth = 24, Fingerprint = fingerprint }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CachedContributionsCountHarmonyAndRetainedTransformations(bool ownCore)
    {
        Operator[] ops = [Op("保留", 1, "维"), Op("禁用", 1, "炎"), Op("普通炎", 1, "炎"),
            new() { Name = "调和替补", Tier = 1, CoreCovenants = ownCore ? ["维"] : [], AdditionalCovenants = ["调和"] }];
        CovenantInfo[] covenants = [new() { Name = "炎", BondType = "SEASON", ActivateCount = 2 }, new() { Name = "维", BondType = "SEASON", ActivateCount = 2 }];
        var calculator = new CovenantCalculator(ops, covenants, new Dictionary<string, string> { ["炎转职"] = "炎" });
        var keep = new LineupSlot { OperatorName = "保留", Equipments = ["变形同构体", "炎转职"] };
        var source = new Lineup { Slots = [keep, new() { OperatorName = "禁用" }], EarlySlots = [keep, new() { OperatorName = "禁用" }] };
        var final = new Lineup { Slots = [keep, new() { OperatorName = "调和替补" }] };
        var preview = new LineupStageRepairer(ops, calculator).Repair(source, final, new HashSet<string> { "禁用" }, new HashSet<string>());
        preview.Lineup.EarlySlots.Select(s => s.OperatorName).Should().Equal("保留", "调和替补");
        var counts = calculator.Compute(new() { Slots = preview.Lineup.EarlySlots });
        counts.Single(c => c.Name == "炎").Count.Should().Be(2);
        counts.Single(c => c.Name == "维").Count.Should().Be(ownCore ? 3 : 2);
    }
}
