// Added for Hongxue Stronghold Companion on 2026-11-17; see NOTICE.
using System.Text.Json;
using System.Diagnostics;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.GameState;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.ViewModels;

public class TrackingStagesTests
{
    private static Lineup Example(string id = "a") => new() { Id = id, Name = "阶段阵容", SklandGuide = new() { Stages =
    [
        new() { Id = "final", Name = "成型阵容", IsFinal = true, Slots = [new() { OperatorName = "共用", IsElite = true, Equipments = ["成型装备"] }, new() { OperatorName = "成型干员" }] },
        new() { Id = "early", Name = "第1–3回合", StartRound = 1, EndRound = 3, Slots = [new() { OperatorName = "共用", Equipments = ["前期装备"] }, new() { OperatorName = "前期干员" }, new() { OperatorName = "备选", IsReserve = true }] },
        new() { Id = "mid", Name = "第5–8回合", StartRound = 5, EndRound = 8, Slots = [new() { OperatorName = "中期干员" }] },
        new() { Id = "custom", Name = "无回合阶段", Slots = [] }
    ] } };
    private static void Select(TrackingViewModel vm, string stage, string lineup = "a") => vm.SelectStage(vm.StageChoices.Single(c => c.LineupId == lineup && c.StageId == stage));
    private static ShopEconomy Round(int? round, int money = 10) => new(round, money, 3, false, null, true);

    [Fact]
    public void SelectedStageUsesItsOwnRequirementsAndPreservesOwnershipAndOtherStages()
    {
        var source = Example(); var json = JsonSerializer.Serialize(source);
        var pool = new DeployPoolState(); pool.SetCount("共用", 2);
        var vm = new TrackingViewModel(); vm.SetDeployPool(pool); vm.TrackLineup(source);
        vm.TrackedOperators.Count(e => e.Name == "共用").Should().Be(1);
        vm.TrackedOperators.Should().Contain(e => e.Name == "备选");
        vm.ActiveEntries.Where(e => e.Type == TrackingType.Operator).Select(e => e.Name).Should().Equal("共用", "成型干员");
        vm.TrackedOperators.Single(e => e.Name == "共用").IsAcquired.Should().BeFalse();
        Select(vm, "early");
        var shared = vm.TrackedOperators.Single(e => e.Name == "共用");
        shared.IsElite.Should().BeFalse(); shared.IsAcquired.Should().BeTrue(); shared.Equipments.Should().Equal("前期装备");
        vm.IsTracked("中期干员").Should().BeFalse(); vm.IsTracked("前期干员").Should().BeTrue();
        vm.ToggleAcquired("前期装备");
        Select(vm, "mid");
        vm.ActiveEntries.Should().ContainSingle().Which.Name.Should().Be("中期干员");
        Select(vm, "early");
        vm.TrackedEquipment.Single(e => e.Name == "前期装备").IsAcquired.Should().BeTrue();
        Select(vm, "final");
        vm.TrackedOperators.Single(e => e.Name == "共用").IsAcquired.Should().BeFalse();
        pool.IsOwned("共用").Should().BeTrue(); pool.IsElite("共用").Should().BeFalse(); pool.CountOf("共用").Should().Be(2);
        pool.SetElite("共用", true); vm.SyncDeployPool();
        Select(vm, "early"); Select(vm, "final");
        vm.TrackedOperators.Single(e => e.Name == "共用").IsAcquired.Should().BeTrue();
        JsonSerializer.Serialize(source).Should().Be(json);
    }

    [Fact]
    public void AutoSwitchHandlesUnknownGapsOverlapsFinalAndManualOverrideWithoutRepeatedRefresh()
    {
        var source = Example();
        source.SklandGuide!.Stages.Add(new() { Id = "overlap", Name = "重叠", StartRound = 2, EndRound = 3 });
        source.SklandGuide.Stages.Add(new() { Id = "copy", Name = "重叠副本", StartRound = 2, EndRound = 3 });
        var vm = new TrackingViewModel(); vm.TrackLineup(source); vm.SetAutomaticStageSwitch(true);
        vm.SelectedStageChoice!.StageId.Should().Be("final"); vm.StageSwitchHint.Should().Contain("等待");
        vm.Economy = Round(1); vm.SelectedStageChoice.StageId.Should().Be("early");
        vm.Economy = Round(2); vm.SelectedStageChoice.StageId.Should().Be("copy");
        int refresh = 0; vm.StageStateChanged += () => refresh++;
        var baseline = new TrackingViewModel { Economy = Round(3) };
        for (int i = 0; i < 100; i++) { baseline.Economy = Round(3, i); vm.Economy = Round(3, i); }
        var watch = Stopwatch.StartNew(); long bytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) baseline.Economy = Round(3, i);
        var baselineMs = watch.Elapsed.TotalMilliseconds; long baselineBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        watch.Restart(); bytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) vm.Economy = Round(3, i);
        var stageMs = watch.Elapsed.TotalMilliseconds; long stageBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        File.WriteAllText(Path.Combine(TestOutput.Directory, "tracking-stage-performance.json"), JsonSerializer.Serialize(new { Updates = 10000, BaselineMs = baselineMs,
            StagesMs = stageMs, BaselineBytes = baselineBytes, StagesBytes = stageBytes, StagePublications = refresh }));
        refresh.Should().Be(0); // Same selected phase, even when the HUD money changes.
        vm.Economy = Round(4); vm.SelectedStageChoice.StageId.Should().Be("copy");
        vm.Economy = Round(5); vm.SelectedStageChoice.StageId.Should().Be("mid");
        vm.Economy = Round(null); vm.SelectedStageChoice.StageId.Should().Be("mid");
        vm.Economy = Round(8); vm.SelectedStageChoice.StageId.Should().Be("mid");
        vm.Economy = Round(9); vm.SelectedStageChoice.StageId.Should().Be("final");
        Select(vm, "custom"); vm.AutomaticStageSwitch.Should().BeFalse();
        vm.Economy = Round(1); vm.SelectedStageChoice.StageId.Should().Be("custom");
        vm.SetAutomaticStageSwitch(true); vm.SelectedStageChoice.StageId.Should().Be("early");
        vm.StartNewRun(); vm.Economy.Should().BeNull();
        vm.Economy = Round(1); vm.SelectedStageChoice.StageId.Should().Be("early");
    }

    [Fact]
    public void ManualSourcesOtherLineupsAndRemovedTargetsRemainIndependent()
    {
        var vm = new TrackingViewModel { TrackMultipleLineups = true };
        vm.AddTracking("手动", TrackingType.Operator); vm.AddTracking("手动装备", TrackingType.Equipment);
        vm.TrackLineup(Example()); vm.TrackLineup(Example("b"));
        Select(vm, "mid", "b"); Select(vm, "early");
        vm.ActiveEntries.Where(e => e.Type == TrackingType.Operator).Select(e => e.Name).Should().BeEquivalentTo("手动", "共用", "前期干员", "备选", "中期干员");
        vm.RemoveTracking("前期干员"); Select(vm, "mid"); Select(vm, "early");
        vm.TrackedOperators.Should().NotContain(e => e.Name == "前期干员");
        vm.ActiveEntries.Should().Contain(e => e.Name == "手动装备");
        Select(vm, "custom"); vm.IsLineupTracked("a").Should().BeTrue();
    }

    [Fact]
    public async Task StageChoiceAutoModeAndSeparateOriginsSurviveTheTrackingFile()
    {
        string folder = Path.Combine(Path.GetTempPath(), "tracking-stages-" + Guid.NewGuid().ToString("N"));
        var settings = new SettingsManager(folder);
        var vm = new TrackingViewModel(); vm.TrackLineup(Example()); Select(vm, "early"); vm.SetAutomaticStageSwitch(true);
        await settings.SaveTrackingStateAsync(vm.CaptureState());
        var saved = await settings.LoadTrackingStateAsync(EquipmentTestData.Rules);
        var reopened = new TrackingViewModel(); reopened.LoadState(saved);
        reopened.SelectedStageChoice!.StageId.Should().Be("early"); reopened.AutomaticStageSwitch.Should().BeTrue();
        reopened.Economy = Round(6); reopened.SelectedStageChoice.StageId.Should().Be("mid");
        reopened.TrackedOperators.Single(e => e.Name == "共用").Origins.Should().HaveCount(2);
        saved.Lineups[0] = saved.Lineups[0] with { SelectedStageId = "missing" };
        await settings.SaveTrackingStateAsync(saved);
        await Assert.ThrowsAsync<JsonException>(() => settings.LoadTrackingStateAsync(EquipmentTestData.Rules));
    }

    [Fact]
    public void LegacyAssociatedTargetsCanAdoptSavedStagesWithoutLosingCompletedStateOrManualTargets()
    {
        var pool = new DeployPoolState(); pool.SetOwned("共用", true); pool.SetElite("共用", true);
        var vm = new TrackingViewModel(); vm.SetDeployPool(pool);
        vm.LoadState(new TrackingState { Lineups = [new("a", "旧阵容")], Entries =
            [new() { Name = "共用", IsAcquired = true, IsElite = true, Origins = [new() { LineupId = "a", IsElite = true }] },
             new() { Name = "手动保留" }] });
        vm.StageChoices.Should().BeEmpty();
        vm.ApplyTrackedLineup(Example());
        vm.StageChoices.Should().HaveCount(4);
        vm.SelectedStageChoice!.StageId.Should().Be("final");
        vm.TrackedOperators.Single(e => e.Name == "共用").IsAcquired.Should().BeTrue();
        vm.ActiveEntries.Should().Contain(e => e.Name == "手动保留");
    }

    [Fact]
    public void OverviewUnifiesStageNeedsWithoutChangingTheCurrentStage()
    {
        var pool = new DeployPoolState(); pool.SetOwned("共用", true);
        var vm = new TrackingViewModel(); vm.SetDeployPool(pool); vm.TrackLineup(Example());
        Select(vm, "early"); vm.Economy = Round(2); vm.SetAutomaticStageSwitch(true);
        var before = JsonSerializer.Serialize(vm.ActiveEntries);
        var stages = JsonSerializer.Serialize(vm.CaptureState().Lineups);
        vm.ShowAllStages = true;
        vm.DisplayOperators.Select(e => e.Name).Should().BeEquivalentTo("共用", "成型干员", "前期干员", "备选", "中期干员");
        vm.DisplayOperators.Single(e => e.Name == "共用").IsAcquired.Should().BeFalse("the full plan still requires elite");
        vm.DisplayEquipment.Select(e => e.Name).Should().BeEquivalentTo("成型装备", "前期装备");
        JsonSerializer.Serialize(vm.ActiveEntries).Should().Be(before);
        JsonSerializer.Serialize(vm.CaptureState().Lineups).Should().Be(stages);
        vm.AutomaticStageSwitch.Should().BeTrue(); vm.SelectedStageChoice!.StageId.Should().Be("early");
        vm.RecordEquipmentPurchase("成型装备");
        vm.DisplayEquipment.Single(e => e.Name == "成型装备").IsAcquired.Should().BeTrue();
        vm.TrackedEquipment.Should().NotContain(e => e.Name == "成型装备");
        vm.ToggleAcquired("成型装备");
        vm.DisplayEquipment.Single(e => e.Name == "成型装备").IsAcquired.Should().BeFalse();
        vm.RemoveTracking("成型装备");
        vm.DisplayEquipment.Should().NotContain(e => e.Name == "成型装备");
        vm.ActiveEntries.Should().Contain(e => e.Name == "前期装备");
        vm.ShowAllStages = false;
        vm.DisplayOperators.Select(e => e.Name).Should().BeEquivalentTo("共用", "前期干员", "备选");
    }

    [Fact]
    public void AllStageShopHighlightsSurviveSelectionAndStopAtAcquisition()
    {
        var vm = new TrackingViewModel();
        var pool = new DeployPoolState();
        vm.SetDeployPool(pool);
        vm.TrackLineup(Example());
        Select(vm, "early");
        var items = new[] { "前期干员", "中期干员", "成型干员", "前期装备", "成型装备", "共用" }
            .Select(name => new ShopItem { Name = name }).ToList();
        string[] Highlight()
        {
            GameStateUpdater.UpdateShopTracking(items, vm.ShopTrackingEntries);
            return items.Where(i => i.IsTracked).Select(i => i.Name).ToArray();
        }
        pool.SetOwned("共用", true);
        vm.SyncDeployPool();
        Highlight().Should().Equal("前期干员", "前期装备");
        vm.ShowAllStages = true;
        Highlight().Should().Equal("前期干员", "中期干员", "成型干员", "前期装备", "共用");
        Select(vm, "mid");
        Highlight().Should().Equal("前期干员", "中期干员", "成型干员", "共用");
        pool.SetOwned("前期干员", true);
        pool.SetElite("共用", true);
        vm.SyncDeployPool();
        Highlight().Should().Equal("中期干员", "成型干员");
        vm.ShowAllStages = false;
        Highlight().Should().Equal("中期干员");
    }

    [Fact]
    public async Task UntrackedPurchasesAndOverviewSurviveClearingTargetsAndReloadUntilANewRun()
    {
        var vm = new TrackingViewModel(); int saves = 0; vm.SourcesChanged += () => saves++;
        vm.RecordEquipmentPurchase("成型装备"); vm.RecordEquipmentPurchase("成型装备");
        saves.Should().Be(1); vm.AllEntries.Should().BeEmpty();
        vm.TrackLineup(Example());
        vm.TrackedEquipment.Single(e => e.Name == "成型装备").IsAcquired.Should().BeTrue();
        vm.ClearAllTracking(); vm.ShowAllStages = true;
        var folder = Path.Combine(Path.GetTempPath(), "all-stage-purchases-" + Guid.NewGuid().ToString("N"));
        var settings = new SettingsManager(folder);
        await settings.SaveTrackingStateAsync(vm.CaptureState());
        var restored = new TrackingViewModel(); restored.LoadState(await settings.LoadTrackingStateAsync(EquipmentTestData.Rules));
        restored.ShowAllStages.Should().BeTrue(); restored.TrackLineup(Example());
        restored.TrackedEquipment.Single().IsAcquired.Should().BeTrue();
        restored.StartNewRun();
        restored.CaptureState().PurchasedEquipment.Should().BeEmpty();
        restored.TrackedEquipment.Single().IsAcquired.Should().BeFalse();
    }

    [Fact]
    public void ManuallyTrackingAnUntrackedPurchaseUsesItsRecordAndExplicitRetrackingCanRequestItAgain()
    {
        var vm = new TrackingViewModel();
        vm.RecordEquipmentPurchase("已购装备");
        vm.AddTracking("已购装备", TrackingType.Equipment);
        vm.TrackedEquipment.Single().IsAcquired.Should().BeTrue();
        vm.AddTracking("已购装备", TrackingType.Equipment);
        vm.TrackedEquipment.Single().IsAcquired.Should().BeFalse();
        vm.CaptureState().PurchasedEquipment.Should().BeEmpty();
        vm.RecordEquipmentPurchase("已购装备");
        vm.TrackedEquipment.Single().IsAcquired.Should().BeTrue();
    }

    [Fact]
    public async Task CachedFullPurchaseCatalogueAvoidsPerScanConstruction()
    {
        var data = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "data"));
        var operators = await data.LoadOperatorsAsync(); var (equipment, _) = await data.LoadEquipmentAsync();
        var twoTargets = equipment.Take(2).Select(e => e.Name).ToList();
        Func<IReadOnlySet<string>> previous = () => operators.Select(o => o.Name).Concat(twoTargets).ToHashSet(StringComparer.Ordinal);
        var catalog = operators.Where(o => o.RequiredStrategyId.Length == 0).Select(o => o.Name).Concat(equipment.Select(e => e.Name)).ToHashSet(StringComparer.Ordinal);
        Func<IReadOnlySet<string>> cached = () => catalog;
        for (int i = 0; i < 20; i++) { _ = previous(); _ = cached(); }
        var timer = Stopwatch.StartNew(); long bytes = GC.GetAllocatedBytesForCurrentThread(); int oldCount = 0;
        for (int i = 0; i < 1000; i++) oldCount += previous().Count;
        double previousMs = timer.Elapsed.TotalMilliseconds; long previousBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        timer.Restart(); bytes = GC.GetAllocatedBytesForCurrentThread(); int fullCount = 0;
        for (int i = 0; i < 1000; i++) fullCount += cached().Count;
        double cachedMs = timer.Elapsed.TotalMilliseconds; long cachedBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        fullCount.Should().Be(catalog.Count * 1000); oldCount.Should().BePositive(); cached().Should().BeSameAs(catalog);
        File.WriteAllText(Path.Combine(TestOutput.Directory, "purchase-catalog-performance.json"), JsonSerializer.Serialize(new
        { Reads = 1000, PreviousMs = previousMs, CachedMs = cachedMs, PreviousBytes = previousBytes, CachedBytes = cachedBytes, FullCatalogNames = catalog.Count,
            Note = "Construction/retrieval only, without Dispatcher, OCR or screenshot time." }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnassignedStageRecommendationsAreDeduplicatedAndKeepPurchaseStateAcrossSwitchSaveAndEdit(bool imported)
    {
        var source = Example();
        source.SklandGuide!.Stages[0].RecommendedEquipments = ["成型装备", "共用推荐", "成型推荐"];
        source.SklandGuide.Stages[1].RecommendedEquipments = ["共用推荐", "前期推荐"];
        source.SklandGuide.Stages[2].RecommendedEquipments = ["中期推荐"];
        if (!imported)
        {
            source.EditorStages = source.SklandGuide.Stages;
            source.Slots = source.EditorStages[0].Slots; source.EarlySlots = source.EditorStages[1].Slots; source.MidSlots = source.EditorStages[2].Slots;
            source.SklandGuide = null;
        }
        var original = JsonSerializer.Serialize(source);
        var vm = new TrackingViewModel(); vm.RecordEquipmentPurchase("前期推荐"); vm.TrackLineup(source);
        vm.DisplayEquipment.Select(e => e.Name).Should().BeEquivalentTo("成型装备", "共用推荐", "成型推荐");
        vm.TrackedEquipment.Single(e => e.Name == "成型装备").EquipmentKind.Should().Be(TrackedEquipmentKind.Planned);
        vm.TrackedEquipment.Single(e => e.Name == "共用推荐").EquipmentKind.Should().Be(TrackedEquipmentKind.Candidate);
        vm.ToggleAcquired("共用推荐"); Select(vm, "early");
        vm.DisplayEquipment.Select(e => e.Name).Should().BeEquivalentTo("前期装备", "共用推荐", "前期推荐");
        vm.DisplayEquipment.Single(e => e.Name == "共用推荐").IsAcquired.Should().BeTrue();
        vm.DisplayEquipment.Single(e => e.Name == "前期推荐").IsAcquired.Should().BeTrue();
        vm.ShowAllStages = true;
        vm.DisplayEquipment.Select(e => e.Name).Should().BeEquivalentTo("成型装备", "共用推荐", "成型推荐", "前期装备", "前期推荐", "中期推荐");
        var directory = Path.Combine(Path.GetTempPath(), "tracking-recommendations-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new SettingsManager(directory);
            await settings.SaveTrackingStateAsync(vm.CaptureState());
            var reopened = new TrackingViewModel(); reopened.LoadState(await settings.LoadTrackingStateAsync(EquipmentTestData.Rules));
            reopened.DisplayEquipment.Select(e => e.Name).Should().BeEquivalentTo(vm.DisplayEquipment.Select(e => e.Name));
            reopened.DisplayEquipment.Single(e => e.Name == "共用推荐").IsAcquired.Should().BeTrue();
            reopened.ShowAllStages = false; Select(reopened, "mid");
            reopened.DisplayEquipment.Should().ContainSingle().Which.Name.Should().Be("中期推荐");
            reopened.RemoveTracking("中期推荐"); Select(reopened, "final"); Select(reopened, "mid");
            reopened.DisplayEquipment.Should().BeEmpty();
            reopened.ApplyTrackedLineup(source); Select(reopened, "early");
            reopened.DisplayEquipment.Single(e => e.Name == "前期推荐").IsAcquired.Should().BeTrue();
            JsonSerializer.Serialize(source).Should().Be(original);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void RecommendationsWithoutOperatorsSurviveDeduplicationSourceRemovalAndNewRun()
    {
        var a = Example(); foreach (var stage in a.SklandGuide!.Stages) stage.Slots.Clear();
        a.SklandGuide.Stages[0].RecommendedEquipments = ["独立推荐"];
        var b = Example("b"); b.SklandGuide!.Stages[0].RecommendedEquipments = ["独立推荐"];
        var vm = new TrackingViewModel { TrackMultipleLineups = true }; vm.TrackLineup(a); vm.TrackLineup(b);
        vm.IsLineupTracked(a.Id).Should().BeTrue();
        vm.TrackedEquipment.Count(e => e.Name == "独立推荐").Should().Be(1);
        vm.ToggleAcquired("独立推荐"); vm.StartNewRun();
        vm.DisplayEquipment.Single(e => e.Name == "独立推荐").IsAcquired.Should().BeFalse();
        int notifications = 0; vm.SourcesChanged += () => notifications++;
        vm.RemoveTracking("独立推荐");
        notifications.Should().Be(1); vm.IsLineupTracked(a.Id).Should().BeFalse();
        vm.ShowAllStages = true; vm.DisplayEquipment.Should().NotContain(e => e.Name == "独立推荐");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyReserveFlagsNoLongerPreventTracking(bool multiple)
    {
        var vm = new TrackingViewModel { TrackMultipleLineups = multiple };
        vm.AddTracking("原有目标", TrackingType.Operator);
        var source = Example();
        foreach (var slot in source.SklandGuide!.Stages.SelectMany(stage => stage.Slots)) slot.IsReserve = true;
        var before = JsonSerializer.Serialize(source);
        vm.TrackLineup(source);
        JsonSerializer.Serialize(source).Should().Be(before);
        vm.TrackedOperators.Select(e => e.Name).Should().Contain("共用", "成型干员", "前期干员", "备选", "中期干员");
        vm.IsTracked("原有目标").Should().Be(multiple);
        vm.IsLineupTracked(source.Id).Should().BeTrue();
        vm.ActiveEntries.Should().Contain(e => e.Name == "共用");
    }

    [Fact]
    public void NativeCanonicalSlotsRemainAuthoritativeAfterEditorMetadataAndNoRoundsRemainManual()
    {
        var lineup = new Lineup { Id = "a", Slots = [new() { OperatorName = "修补后的成型" }], EarlySlots = [new() { OperatorName = "修补后的前期" }], EditorStages =
        [new() { Id = "formed", Name = "成型阵容", IsFinal = true, Slots = [new() { OperatorName = "旧成型" }] },
         new() { Id = "early", Name = "前期", Slots = [new() { OperatorName = "旧前期" }] }] };
        var vm = new TrackingViewModel(); vm.TrackLineup(lineup);
        vm.SelectedStageChoice!.StageId.Should().Be("formed");
        vm.ActiveEntries.Should().ContainSingle().Which.Name.Should().Be("修补后的成型");
        Select(vm, "early"); vm.SetAutomaticStageSwitch(true);
        vm.Economy = Round(15);
        vm.SelectedStageChoice!.StageId.Should().Be("early"); vm.StageSwitchHint.Should().Contain("未设置");
        vm.ActiveEntries.Should().ContainSingle().Which.Name.Should().Be("修补后的前期");
        vm.TrackedOperators.Should().NotContain(e => e.Name.StartsWith("旧"));
    }
}
