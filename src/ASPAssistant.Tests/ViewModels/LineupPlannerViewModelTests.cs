using System.Diagnostics;
using System.Text.Json;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.ViewModels;

public class LineupPlannerViewModelTests
{
    [Fact]
    public async Task LimitedSearchDoesNotClaimUnsolvedVariantsAreImpossible()
    {
        var operators = Enumerable.Range(0, 49).Select(i => new Operator
        {
            Name = $"干员{i}", CoreCovenants = ["炎"], AdditionalCovenants = ["独行"]
        }).ToList();
        CovenantInfo[] covenants =
        [
            new() { Name = "炎", BondType = "SEASON", ActivateCount = 3 },
            new() { Name = "独行", BondType = "REGULAR", ActivateCount = 1 }
        ];
        var vm = new LineupPlannerViewModel(new LineupPlanner(operators, covenants), operators, new BanViewModel(), EquipmentTestData.Rules)
        { DebounceMilliseconds = 0 };
        Item(vm, "炎").ToggleLevel1Command.Execute(null);
        Item(vm, "独行").ToggleLevel1Command.Execute(null);
        vm.IsActive = true;
        await vm.PendingSearch!;

        vm.TotalResults.Should().Be(0);
        vm.VariantCells.Should().Contain(c => c.IsLimited);
        vm.StatusDetail.Should().Contain("搜索上限");
        vm.VariantCells.Should().Contain(c => !c.HasResults && c.SubText == "受限");
        vm.VariantCells.Should().OnlyContain(c => !c.ToolTip.Contains("无解"));
    }

    private static Operator Op(string name, params string[] core) => new() { Name = name, CoreCovenants = [.. core], IconPath = $"icons/operators/{name}.png" };

    private static (LineupPlannerViewModel Vm, BanViewModel Bans) Create()
    {
        var operators = new List<Operator>();
        for (var i = 1; i <= 6; i++) operators.Add(Op($"炎{i}", "炎"));
        for (var i = 1; i <= 6; i++) operators.Add(Op($"维{i}", "维多利亚"));
        operators.Add(new Operator { Name = "炎突", CoreCovenants = ["炎"], AdditionalCovenants = ["突袭"], IconPath = "icons/operators/炎突.png" });
        operators.Add(new Operator { Name = "维突", CoreCovenants = ["维多利亚"], AdditionalCovenants = ["突袭"], IconPath = "icons/operators/维突.png" });
        var covenants = new[]
        {
            new CovenantInfo { Name = "炎", BondType = "SEASON", ActivateCount = 3 },
            new CovenantInfo { Name = "维多利亚", BondType = "SEASON", ActivateCount = 3 },
            new CovenantInfo { Name = "独行", BondType = "REGULAR", ActivateCount = 1 },
            new CovenantInfo { Name = "突袭", BondType = "REGULAR", ActivateCount = 2 },
        };
        var bans = new BanViewModel();
        var vm = new LineupPlannerViewModel(new LineupPlanner(operators, covenants), operators, bans, EquipmentTestData.Rules) { DebounceMilliseconds = 0 };
        return (vm, bans);
    }

    private static PlannerCovenantItem Item(LineupPlannerViewModel vm, string name) => vm.AllCovenants.Single(c => c.Name == name);

    [Fact]
    public async Task ToggleLevel_SearchesAndSelectsFirstVariantWithResults()
    {
        var (vm, _) = Create();
        vm.IsActive = true;

        Item(vm, "炎").ToggleLevel1Command.Execute(null);
        await vm.PendingSearch!;

        vm.SelectedGoalCount.Should().Be(1);
        vm.TotalResults.Should().BeGreaterThan(0);
        vm.SelectedVariant!.HasResults.Should().BeTrue();
        vm.VisibleLineups.Should().NotBeEmpty();
        vm.VisibleLineups[0].Operators.Where(o => !o.IsFree).Should().OnlyContain(o => o.IconPath.Length > 0);
    }

    [Fact]
    public void ClickingActiveLevelAgainClearsIt()
    {
        var (vm, _) = Create();
        var fire = Item(vm, "炎");

        fire.ToggleLevel2Command.Execute(null);
        fire.Level.Should().Be(2);
        fire.ToggleLevel2Command.Execute(null);
        fire.Level.Should().Be(0);
    }

    [Fact]
    public void CovenantWithoutSecondLevelIgnoresLevelTwo()
    {
        var (vm, _) = Create();
        var solo = Item(vm, "独行");

        solo.HasSecond.Should().BeFalse();
        solo.ToggleLevel2Command.Execute(null);
        solo.Level.Should().Be(0);
    }

    [Fact]
    public void ChangesWhileHiddenAreDeferredUntilActive()
    {
        var (vm, _) = Create();

        Item(vm, "炎").ToggleLevel1Command.Execute(null);
        vm.PendingSearch.Should().BeNull();

        vm.IsActive = true;
        vm.PendingSearch.Should().NotBeNull();
    }

    [Fact]
    public async Task BanningASelectedGoalDropsIt()
    {
        var (vm, bans) = Create();
        vm.IsActive = true;
        Item(vm, "炎").ToggleLevel1Command.Execute(null);
        await vm.PendingSearch!;

        bans.SetCovenantBans(["炎"]);
        await vm.PendingSearch!;

        Item(vm, "炎").IsBanned.Should().BeTrue();
        Item(vm, "炎").Level.Should().Be(0);
        vm.SelectedGoalCount.Should().Be(0);
        vm.TotalResults.Should().Be(0);
    }

    [Fact]
    public async Task NewerSearchReplacesOlderOne()
    {
        var (vm, _) = Create();
        vm.DebounceMilliseconds = 50;
        vm.IsActive = true;

        Item(vm, "炎").ToggleLevel1Command.Execute(null);
        var first = vm.PendingSearch!;
        Item(vm, "维多利亚").ToggleLevel1Command.Execute(null);
        await first;
        await vm.PendingSearch!;

        vm.VisibleLineups.Should().NotBeEmpty();
        vm.VisibleLineups.Should().OnlyContain(l => l.Source.Goals.Count == 2);
    }

    [Fact]
    public async Task LockedOperatorsAreAddedFromSearchAndSaved()
    {
        var (vm, _) = Create();
        vm.IsActive = true;
        Item(vm, "炎").ToggleLevel1Command.Execute(null);

        vm.LockSearchText = "维";
        vm.LockSuggestions.Should().NotBeEmpty();
        vm.AddLocked("维3").Should().BeTrue();
        await vm.PendingSearch!;

        Lineup? saved = null;
        vm.SaveRequested += l => saved = l;
        vm.SaveLineupCommand.Execute(vm.VisibleLineups[0]);

        saved.Should().NotBeNull();
        saved!.Slots.Select(s => s.OperatorName).Should().Contain("维3");
        vm.VisibleLineups[0].Operators[0].IsLocked.Should().BeTrue();
        Lineup? tracked = null;
        vm.TrackRequested += l => tracked = l;
        vm.TrackLineupCommand.Execute(vm.VisibleLineups[0]);
        tracked!.Id.Should().Be(saved.Id);
        tracked.Slots.Should().NotBeSameAs(saved.Slots);
        tracked.Slots[0].Equipments.Add("独立修改");
        saved.Slots[0].Equipments.Should().NotContain("独立修改");
        var tracking = new TrackingViewModel();
        tracking.TrackLineup(tracked);
        vm.SaveLineupCommand.Execute(vm.VisibleLineups[0]);
        tracking.TrackedLineupIds.Should().Contain(saved.Id);
        saved.Slots = [new() { OperatorName = "修改后" }];
        tracking.ApplyTrackedLineup(saved);
        tracking.TrackedOperators.Should().ContainSingle().Which.Name.Should().Be("修改后");
    }

    [Fact]
    public void OnlyCurrentRunHidesUnavailableCovenants()
    {
        var (vm, _) = Create();
        vm.SetAvailableCovenants(["炎"]);

        vm.OnlyCurrentRun = true;

        Item(vm, "炎").IsVisible.Should().BeTrue();
        Item(vm, "维多利亚").IsVisible.Should().BeFalse();
        vm.HasRunInfo.Should().BeTrue();
    }

    [Fact]
    public async Task BannedAdditionalCovenantStaysSelectable()
    {
        var (vm, bans) = Create();
        vm.IsActive = true;
        bans.SetCovenantBans(["突袭"]);
        vm.SetAvailableCovenants(["炎", "维多利亚"]);
        vm.OnlyCurrentRun = true;

        var raid = Item(vm, "突袭");
        raid.IsBanned.Should().BeTrue();
        raid.IsBlocked.Should().BeFalse();
        raid.IsVisible.Should().BeTrue();

        raid.ToggleLevel1Command.Execute(null);
        await vm.PendingSearch!;

        raid.Level.Should().Be(1);
        vm.TotalResults.Should().BeGreaterThan(0);
    }

    [Fact]
    public void VisibleAdditionalWithOneHolderCanTransformUntilAbsent()
    {
        var (vm, bans) = Create();
        vm.IsActive = true;
        var raid = Item(vm, "突袭");
        raid.ToggleLevel1Command.Execute(null);
        raid.Level.Should().Be(1);

        // 炎 + 突袭 banned: 炎突 leaves (core + additional banned), only 维突 holds 突袭 — Ⅰ needs 2.
        bans.SetCovenantBans(["炎", "突袭"]);

        raid.AvailableMembers.Should().Be(1);
        raid.IsBlocked.Should().BeFalse();
        raid.Level.Should().Be(1);
        raid.BanNote.Should().Contain("至少需 1 名转职");
        vm.BuildRequest().TransformAllowed.Should().Contain("突袭");
        bans.SetCovenantBans(["炎"], new HashSet<string> { "突袭" });
        raid.IsBlocked.Should().BeTrue();
        raid.Level.Should().Be(0);
        vm.BuildRequest().TransformAllowed.Should().NotContain("突袭");
    }

    [Fact]
    public void CovenantDetailsListMembersAndMarkUnavailableOnes()
    {
        var (vm, bans) = Create();
        var fire = Item(vm, "炎");
        fire.Members.Select(m => m.Name).Should().Contain(["炎1", "炎突"]);
        fire.Members.Should().OnlyContain(m => !m.IsUnavailable);

        bans.SetBans(["炎1"]);

        Item(vm, "炎").Members.Single(m => m.Name == "炎1").IsUnavailable.Should().BeTrue();
        Item(vm, "炎").MemberSummary.Should().Contain("本局可用");
        Item(vm, "炎").FirstRequirement.Should().Be("3 人");
        Item(vm, "独行").SecondRequirement.Should().Be("没有 Ⅱ 级");
    }

    [Fact]
    public void ManualSecondLevelIsUnavailableToPlanner()
    {
        var (vm, _) = Create();
        var raid = Item(vm, "突袭");

        raid.SecondManual.Should().BeTrue();
        raid.CanPlanSecond.Should().BeFalse();
        raid.ToggleLevel2Command.Execute(null);
        raid.Level.Should().Be(0);
        raid.SecondRequirement.Should().Be("规划不提供 Ⅱ 级效果");
    }

    [Fact]
    public async Task ResultOperatorsCarryDetailsAndPlannerNotes()
    {
        var (vm, _) = Create();
        vm.IsActive = true;
        vm.AddLocked("维3");
        Item(vm, "炎").ToggleLevel1Command.Execute(null);
        await vm.PendingSearch!;

        var ops = vm.VisibleLineups[0].Operators;
        ops.Where(o => !o.IsFree).Should().OnlyContain(o => o.HasDetails && o.Operator!.Name == o.Name);
        ops.Where(o => o.IsFree).Should().OnlyContain(o => !o.HasDetails);
        ops.Single(o => o.Name == "维3").ExtraInfo.Should().Contain("必带");
        vm.LockedOperators.Single().Operator.Name.Should().Be("维3");
    }

    [Fact]
    public async Task ResultsShowAllActuallyActivatedCovenantLevels()
    {
        var operators = new List<Operator>
        {
            Op("炎1", "炎"), Op("炎2", "炎"), Op("炎3", "炎"),
            new() { Name = "精准1", AdditionalCovenants = ["精准"], IconPath = "icons/operators/精准1.png" },
            new() { Name = "精准2", AdditionalCovenants = ["精准"], IconPath = "icons/operators/精准2.png" },
            new() { Name = "精准3", AdditionalCovenants = ["精准"], IconPath = "icons/operators/精准3.png" },
            Op("空1"), Op("空2")
        };
        var covenants = new[]
        {
            new CovenantInfo { Name = "炎", BondType = "SEASON", ActivateCount = 3 },
            new CovenantInfo { Name = "精准", BondType = "REGULAR", ActivateCount = 2 }
        };
        var vm = new LineupPlannerViewModel(
            new LineupPlanner(operators, covenants), operators, new BanViewModel(), EquipmentTestData.Rules) { DebounceMilliseconds = 0 };
        vm.IsActive = true;
        vm.AllCovenants.Single(c => c.Name == "炎").ToggleLevel1Command.Execute(null);
        await vm.PendingSearch!;

        vm.VisibleLineups[0].Goals.Select(g => g.Text)
            .Should().Contain(["炎 3", "精准 3"]);
    }

    [Fact]
    public async Task RouteForACandidateListsStagesAndClosesOnNewResults()
    {
        var (vm, _) = Create();
        vm.IsActive = true;
        Item(vm, "炎").ToggleLevel1Command.Execute(null);
        await vm.PendingSearch!;

        await vm.PlanRouteAsync(vm.VisibleLineups[0]);

        vm.ShowRoute.Should().BeTrue();
        vm.RouteStages.Select(s => s.Title).Should().Equal("开局", "前期", "中期", "后期", "成型阵容");
        vm.RouteSteps.Select(s => s.Name).Should().Equal("开局", "前期", "中期", "后期", "成型阵容");
        // Terminal level unknown: 开局 is the current stage and the only one open
        vm.RouteStages.Count(s => s.IsExpanded).Should().Be(1);
        vm.RouteStages[0].IsCurrent.Should().BeTrue();
        vm.RouteStages.SelectMany(s => s.Roles).Should().NotBeEmpty();
        vm.RouteStages[^1].IsFinal.Should().BeTrue();
        vm.RouteStages.SelectMany(s => s.Operators).Should().OnlyContain(o => o.RoleText == "最终" || o.RoleText == "过渡");

        Item(vm, "维多利亚").ToggleLevel1Command.Execute(null);
        await vm.PendingSearch!;
        vm.ShowRoute.Should().BeFalse();
        vm.RouteStages.Should().BeEmpty();
    }

    [Fact]
    public async Task ReturningFromManualRouteKeepsGoalsAndReopensTheResultList()
    {
        var (vm, _) = Create();
        vm.IsActive = true;
        Item(vm, "炎").ToggleLevel1Command.Execute(null);
        await vm.PendingSearch!;
        vm.ShowResultPageCommand.Execute(null);
        await vm.PlanRouteAsync(vm.VisibleLineups[0]);
        vm.ShowRouteView.Should().BeTrue();

        vm.ShowGoalPageCommand.Execute(null);
        vm.ShowGoals.Should().BeTrue();
        Item(vm, "炎").Level.Should().Be(1);
        vm.ShowResultPageCommand.Execute(null);
        vm.ShowResultList.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedRouteKeepsTheStagesAsLineupPhases(bool renamed)
    {
        var (vm, _) = Create();
        Lineup? saved = null;
        vm.SaveRequested += l => saved = l;
        vm.IsActive = true;
        Item(vm, "炎").ToggleLevel1Command.Execute(null);
        await vm.PendingSearch!;
        await vm.PlanRouteAsync(vm.VisibleLineups[0]);

        if (renamed)
            for (int index = 0; index < vm.RouteStages.Count; index++)
            {
                var stage = vm.RouteStages[index];
                vm.RouteStages[index] = new RouteStageItem { Title = $"路线阶段{index + 1}", IsFinal = stage.IsFinal,
                    Operators = stage.Operators, IsStacking = stage.IsStacking, StrategyPiece = stage.StrategyPiece };
            }
        var expectedNames = vm.RouteStages.OrderByDescending(s => s.IsFinal).Select(s => s.Title).ToArray();
        vm.SaveRouteCommand.Execute(null);

        saved.Should().NotBeNull();
        saved!.Slots.Select(s => s.OperatorName).Should().BeEquivalentTo(vm.VisibleLineups[0].Source.Operators.Select(o => o.Name));
        saved.EarlySlots.Select(s => s.OperatorName).Should().Equal(vm.RouteStages[1].Operators.Select(o => o.Name));
        saved.MidSlots.Select(s => s.OperatorName).Should().Equal(vm.RouteStages[2].Operators.Select(o => o.Name));
        saved.SupportSlots.Should().BeEmpty("后期 is now its own complete page");
        saved.EditorStages!.Select(s => s.Name).Should().Equal(vm.RouteStages.OrderByDescending(s => s.IsFinal).Select(s => s.Title));
        saved.EditorStages.Select(s => s.Id).Should().OnlyHaveUniqueItems();
        saved.EditorStages.OrderBy(s => s.StartRound).Select(s => (s.StartRound, s.EndRound)).Should()
            .Equal((1, 3), (4, 6), (7, 9), (10, 12), (13, 15));
        foreach (var stage in saved.EditorStages)
        {
            var expected = vm.RouteStages.Single(s => s.Title == stage.Name).Operators.Select(o => o.Name);
            if (stage.IsFinal) stage.Slots.Select(s => s.OperatorName).Should().BeEquivalentTo(expected);
            else stage.Slots.Select(s => s.OperatorName).Should().Equal(expected);
        }
        var operators = vm.RouteStages.SelectMany(s => s.Operators).Select(o => o.Operator).OfType<Operator>().DistinctBy(o => o.Name).ToList();
        var calculator = new CovenantCalculator(operators, [], new Dictionary<string, string>());
        var folder = Path.Combine(Path.GetTempPath(), "saved-route-stages-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ASPAssistant.Core.Data.LineupStore(folder, EquipmentTestData.Rules);
            await store.SaveAsync(saved);
            var shared = await new LineupShareService(EquipmentTestData.Rules, folder).ImportAsync(
                System.Text.Json.JsonSerializer.Serialize((await store.LoadAllAsync()).Single()));
            var editor = new LineupEditorViewModel(shared, operators, [], calculator);
            editor.PhaseLabels.Values.Should().Equal(expectedNames);
            editor.ValidationError.Should().BeNullOrEmpty();
            foreach (var phase in editor.PhaseLabels.Keys)
            {
                editor.SelectPhase(phase);
                var savedStage = saved.EditorStages.Single(s => s.Name == editor.CurrentStage.Name);
                editor.CurrentStage.StartRound.Should().Be(savedStage.StartRound);
                editor.CurrentStage.EndRound.Should().Be(savedStage.EndRound);
                editor.Slots.Where(s => s.OperatorName.Length > 0).Select(s => s.OperatorName).Should()
                    .Equal(saved.EditorStages.Single(s => s.Name == editor.CurrentStage.Name).Slots.Select(s => s.OperatorName));
            }
            var tracking = new TrackingViewModel(); tracking.TrackLineup(editor.ToLineup());
            tracking.StageChoices.Select(c => c.Label).Should().Equal(expectedNames);
            tracking.AutomaticStageSwitch.Should().BeFalse();
            foreach (var choice in tracking.StageChoices)
            {
                tracking.SelectStage(choice);
                tracking.ActiveEntries.Where(e => e.Type == TrackingType.Operator).Select(e => e.Name).Should()
                    .BeEquivalentTo(saved.EditorStages.Single(s => s.Id == choice.StageId).Slots.Select(s => s.OperatorName));
            }
            tracking.SetAutomaticStageSwitch(true);
            for (int round = 1; round <= 15; round++)
            {
                tracking.Economy = new ShopEconomy(round, 10, 3, false, null, true);
                tracking.SelectedStageChoice!.Label.Should().Be(vm.RouteStages[(round - 1) / 3].Title);
            }
            tracking.Economy = new ShopEconomy(16, 10, 3, false, null, true);
            tracking.SelectedStageChoice!.Label.Should().Be(vm.RouteStages.Single(s => s.IsFinal).Title);
            tracking.SelectStage(tracking.StageChoices.Single(c => c.StageId == "early"));
            tracking.AutomaticStageSwitch.Should().BeFalse();
            tracking.Economy = new ShopEconomy(13, 10, 3, false, null, true);
            tracking.SelectedStageChoice!.StageId.Should().Be("early");
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task MechStrategyIsSavedWithItsTransformations()
    {
        var (vm, bans) = Create();
        Lineup? saved = null;
        vm.SaveRequested += l => saved = l;
        vm.IsActive = true;
        bans.SetBans(["炎3", "炎4", "炎5", "炎6", "炎突"]);
        vm.Strategy = PlannerStrategy.Mech;
        Item(vm, "炎").ToggleLevel1Command.Execute(null);
        await vm.PendingSearch!;

        vm.VariantCells.Select(c => c.Transforms).Distinct().Should().Equal(0, 1, 2, 3);
        vm.SelectVariantCommand.Execute(vm.VariantCells.First(c => c.Transforms == 1 && c.HasResults));
        var item = vm.VisibleLineups.FirstOrDefault(l => l.Source.MechCovenants.Count > 0)
            ?? throw new Xunit.Sdk.XunitException("no lineup uses the mech");
        item.HasStrategyPiece.Should().BeTrue();
        vm.SaveLineupCommand.Execute(item);

        saved!.Strategies.Should().ContainSingle(s => s.StrategyId == PlannerStrategies.MechId);
        var mech = saved.Slots.Single(s => s.OperatorName == LineupEquipmentService.MechName);
        mech.Equipments.Should().StartWith(LineupEquipmentService.Shapeshifter);
        mech.Equipments.Should().HaveCount(item.Source.MechCovenants.Count + 1);
        await vm.PlanRouteAsync(item); vm.SaveRouteCommand.Execute(null);
        saved.EditorStages!.Select(s => s.Name).Should().Equal(vm.RouteStages.OrderByDescending(s => s.IsFinal).Select(s => s.Title));
        foreach (var stage in saved.EditorStages)
        {
            var piece = stage.Slots.Single(s => s.OperatorName == LineupEquipmentService.MechName);
            piece.IsElite.Should().Be(item.Source.MechElite);
            if (stage.IsFinal || vm.RouteStages.Single(s => s.Title == stage.Name).IsStacking) piece.Equipments.Should().Equal(mech.Equipments);
            else piece.Equipments.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Repair_LocksSurvivorsAndKeepsTheirGear()
    {
        var (vm, bans) = Create();
        vm.IsActive = true;
        var source = new Lineup
        {
            Id = "src", Name = "炎 速攻",
            Slots = [.. new[] { "炎1", "炎2", "炎3", "炎4", "炎5", "炎6" }.Select(n => new LineupSlot { OperatorName = n })],
            EarlySlots = [new LineupSlot { OperatorName = "维1" }],
            TargetCovenants = [new TargetCovenant { Name = "炎", Tier = 2 }],
        };
        source.Slots[0].CandidateEquipments = [new CandidateEquipment { Name = "歌利亚头盔", IsPriority = true }];
        bans.SetBans(["炎5", "炎6"]);
        vm.BeginRepair(source, source.TargetCovenants, ["炎5", "炎6"]);
        await vm.PendingSearch!;

        vm.IsRepairing.Should().BeTrue();
        vm.LockedOperators.Select(l => l.Name).Should().Equal("炎1", "炎2", "炎3", "炎4");
        Item(vm, "炎").Level.Should().Be(2);
        vm.RepairSummary.Should().StartWith("固定没被禁的 4 人，替补 2 人");
        vm.VisibleLineups.Should().NotBeEmpty();
        var first = vm.VisibleLineups[0];
        await vm.PreviewStageRepairCommand.ExecuteAsync(first);
        first.RepairText.Should().StartWith("炎5、炎6 → ");
        first.LineupName.Should().Be("炎 速攻（修补）");

        Lineup? overwritten = null;
        vm.OverwriteRequested += l => overwritten = l;
        vm.OverwriteLineupCommand.Execute(first);
        overwritten!.Id.Should().Be("src");
        overwritten.EarlySlots.Single().OperatorName.Should().Be("维1");
        overwritten.Slots.Single(s => s.OperatorName == "炎1").CandidateEquipments.Single().Name.Should().Be("歌利亚头盔");
        overwritten.Slots.Should().NotContain(s => s.OperatorName == "炎5");

        vm.EndRepairCommand.Execute(null);
        vm.IsRepairing.Should().BeFalse();
    }

    [Fact]
    public async Task Repair_UsesSavedStrategyAndKeepsMechAndOtherPhases()
    {
        var (vm, bans) = Create();
        var source = new Lineup { Id = "saved", Name = "原阵容", SourceUrl = "https://example.test/guide",
            Slots = [.. Enumerable.Range(1, 5).Select(i => new LineupSlot { OperatorName = $"炎{i}" }),
                new() { OperatorName = LineupEquipmentService.MechName, IsElite = true,
                    CandidateEquipments = [new() { Name = "候选盾", IsPriority = true }] }],
            EarlySlots = [new() { OperatorName = "维1", Equipments = ["旧装备"], CandidateEquipments = [new() { Name = "前期候选" }] }],
            Strategies = [new() { StrategyId = PlannerStrategies.MechId, Tags = [StrategyTag.阵容核心] }] };
        var targets = new List<TargetCovenant> { new() { Name = "炎", Tier = 2 } };
        var original = System.Text.Json.JsonSerializer.Serialize(source);
        bans.SetBans(["炎5"]);
        vm.Strategy = PlannerStrategy.Pith;
        vm.BeginRepair(source, targets, ["炎5"]);
        vm.Strategy.Should().Be(PlannerStrategy.Mech);
        vm.MechElite.Should().BeTrue();
        vm.IsActive = true;
        await vm.PendingSearch!;
        vm.VisibleLineups.Should().NotBeEmpty();
        Lineup? saved = null;
        vm.OverwriteRequested += value => saved = value;
        await vm.PreviewStageRepairCommand.ExecuteAsync(vm.VisibleLineups[0]);
        vm.OverwriteLineupCommand.Execute(vm.VisibleLineups[0]);
        saved!.Slots.Single(s => s.OperatorName == LineupEquipmentService.MechName).CandidateEquipments.Single().Name.Should().Be("候选盾");
        saved.EarlySlots.Should().BeEquivalentTo(source.EarlySlots);
        saved.Strategies.Should().BeEquivalentTo(source.Strategies);
        saved.SourceUrl.Should().Be(source.SourceUrl);
        saved.TargetCovenants.Should().BeEquivalentTo(targets);
        System.Text.Json.JsonSerializer.Serialize(source).Should().Be(original);
    }

    [Fact]
    public async Task Repair_ResultsReachTargetsWithTheSurvivorsSavedEquipment()
    {
        var (vm, bans) = Create();
        var source = new Lineup { Slots = [.. new[] { "炎1", "炎2", "炎3", "维1", "炎5", "炎6" }.Select(n => new LineupSlot { OperatorName = n })],
            TargetCovenants = [new() { Name = "炎", Tier = 2 }] };
        source.Slots[3].Equipments = ["坚守盾牌"];
        bans.SetBans(["炎5", "炎6"]);
        vm.BeginRepair(source, source.TargetCovenants, ["炎5", "炎6"]);
        vm.IsActive = true;
        await vm.PendingSearch!;
        var operators = Enumerable.Range(1, 6).Select(i => Op($"炎{i}", "炎"))
            .Concat(Enumerable.Range(1, 6).Select(i => Op($"维{i}", "维多利亚")))
            .Concat([Op("炎突", "炎"), Op("维突", "维多利亚")]).ToList();
        var calculator = new CovenantCalculator(operators, vm.AllCovenants.Select(c => c.Info).ToList(),
            EquipmentTestData.Rules.EquipmentToCovenant.ToDictionary(p => p.Key, p => p.Value));
        Lineup? saved = null;
        vm.OverwriteRequested += value => saved = value;
        var variants = vm.VariantCells.Where(c => c.HasResults).ToList();
        variants.Should().NotBeEmpty();
        foreach (var variant in variants)
        {
            vm.SelectVariantCommand.Execute(variant);
            foreach (var item in vm.VisibleLineups)
            {
                vm.OverwriteLineupCommand.Execute(item);
                saved!.Slots.Single(s => s.OperatorName == "维1").Equipments.Should().Equal("坚守盾牌");
                calculator.Compute(saved).Single(s => s.Name == "炎").Count.Should().BeGreaterThanOrEqualTo(6);
            }
        }
    }

    [Fact]
    public async Task Repair_StagePreviewIsInvalidatedByBansAndLeavingRepair()
    {
        var (vm, bans) = Create();
        var source = new Lineup { Slots = [.. Enumerable.Range(1, 6).Select(i => new LineupSlot { OperatorName = $"炎{i}" })],
            EarlySlots = [new() { OperatorName = "炎6" }, new() { OperatorName = "维1" }], TargetCovenants = [new() { Name = "炎", Tier = 2 }] };
        bans.SetBans(["炎6"]); vm.BeginRepair(source, source.TargetCovenants, ["炎6"]); vm.IsActive = true; await vm.PendingSearch!;
        var item = vm.VisibleLineups[0];
        await vm.PreviewStageRepairCommand.ExecuteAsync(item);
        item.CanUseRepair.Should().BeTrue();
        Lineup? tracked = null; vm.TrackRequested += value => tracked = value;
        vm.TrackLineupCommand.Execute(item);
        tracked!.EarlySlots.Should().NotContain(s => s.OperatorName == "炎6");
        bans.SetBans(["炎6", "炎5"]);
        item.StageRepair.Should().BeNull(); item.CanUseRepair.Should().BeFalse();
        Lineup? saved = null; vm.SaveRequested += value => saved = value;
        vm.SaveLineupCommand.Execute(item); saved.Should().BeNull("a stale preview cannot save after input changes");
        await vm.PendingSearch!;
        item = vm.VisibleLineups[0];
        var running = vm.PreviewStageRepairCommand.ExecuteAsync(item);
        vm.EndRepairCommand.Execute(null); await running;
        item.StageRepair.Should().BeNull("a cancelled preview must not publish after leaving repair");
    }

    [Fact]
    public async Task Repair_UnknownStageMemberBlocksAllSaveAndTrackPaths()
    {
        var (vm, bans) = Create();
        var source = new Lineup { Slots = [.. Enumerable.Range(1, 6).Select(i => new LineupSlot { OperatorName = $"炎{i}" })],
            EarlySlots = [new() { OperatorName = "未知干员" }], TargetCovenants = [new() { Name = "炎", Tier = 2 }] };
        bans.SetBans(["炎6"]); vm.BeginRepair(source, source.TargetCovenants, ["炎6"]); vm.IsActive = true; await vm.PendingSearch!;
        var item = vm.VisibleLineups[0]; await vm.PreviewStageRepairCommand.ExecuteAsync(item);
        item.StageRepair!.CanSave.Should().BeFalse();
        int writes = 0; vm.SaveRequested += _ => writes++; vm.TrackRequested += _ => writes++; vm.OverwriteRequested += _ => writes++;
        vm.SaveLineupCommand.Execute(item); vm.TrackLineupCommand.Execute(item); vm.OverwriteLineupCommand.Execute(item);
        writes.Should().Be(0);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Repair_StageLineupsStayConsistentAfterSaveReopenAndTracking(bool imported, bool asCopy)
    {
        var (vm, bans) = Create();
        var source = new Lineup { Id = "stage-repair", Name = "阶段修补",
            Slots = [.. Enumerable.Range(1, 6).Select(i => new LineupSlot { OperatorName = $"炎{i}" })],
            TargetCovenants = [new() { Name = "炎", Tier = 2 }] };
        source.Slots[0].IsElite = true;
        source.Slots[0].Equipments = ["坚守盾牌"];
        source.Slots[0].CoreEquipments = ["坚守盾牌"];
        source.Slots[0].CandidateEquipments = [new() { Name = "歌利亚头盔", IsPriority = true }];
        source.Slots[0].Tags = [LineupTag.阵容核心];
        source.Slots[0].GuideLoadout = new() { CharId = "char_fire1", SkillId = "skill1", ModuleId = "module1" };
        var stages = new List<SklandGuideStage>
        {
            new() { Id = "opening", Name = "第 1–3 回合", StartRound = 1, EndRound = 3,
                Slots = [new() { OperatorName = "炎6", Equipments = ["可靠怀表"] }, new() { OperatorName = "维1" }],
                OperationIdea = "原开局讲解", RecommendedEquipments = ["可靠怀表"] },
            new() { Id = "formed-custom-id", Name = "成型自定义名称", IsFinal = true,
                Slots = JsonSerializer.Deserialize<List<LineupSlot>>(JsonSerializer.Serialize(source.Slots))!,
                ActiveCovenants = ["炎"], OperationIdea = "原成型讲解", RecommendedEquipments = ["歌利亚头盔"] },
            new() { Id = "empty", Name = "空阶段", StartRound = 4, EndRound = 5 }
        };
        if (imported)
            source.SklandGuide = new() { GuideId = "original-guide", Author = "原作者", ApplicableScope = "原适用范围", Stages = stages,
                OriginalGuide = JsonSerializer.SerializeToElement(new { text = "原攻略数据" }) };
        else source.EditorStages = stages;
        var original = JsonSerializer.Serialize(source);
        var pool = new DeployPoolState();
        pool.SetCount("炎1", 2); pool.SetElite("炎1", true);
        var tracking = new TrackingViewModel();
        tracking.SetDeployPool(pool); tracking.TrackLineup(source);
        tracking.ToggleAcquired("坚守盾牌");
        var acquired = JsonSerializer.Serialize(pool);
        bans.SetBans(["炎6"]);
        vm.BeginRepair(source, source.TargetCovenants, ["炎6"]);
        vm.IsActive = true;
        await vm.PendingSearch!;
        vm.VisibleLineups.Should().NotBeEmpty();
        var item = vm.VisibleLineups[0];
        Lineup? saved = null;
        vm.SaveRequested += value => saved = value;
        vm.OverwriteRequested += value => saved = value;
        var command = asCopy ? vm.SaveLineupCommand : vm.OverwriteLineupCommand;
        command.Execute(item);
        saved.Should().BeNull("stage repairs must be previewed before saving");
        await vm.PreviewStageRepairCommand.ExecuteAsync(item);
        item.StageRepair!.CanSave.Should().BeTrue();
        command.Execute(item);
        var timer = Stopwatch.StartNew();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) command.Execute(item);
        timer.Stop();
        File.WriteAllText(Path.Combine(TestOutput.Directory, $"repair-stage-{imported}-{asCopy}-performance.json"),
            JsonSerializer.Serialize(new { Saves = 100, Milliseconds = timer.Elapsed.TotalMilliseconds,
                Bytes = GC.GetAllocatedBytesForCurrentThread() - allocated }));
        saved.Should().NotBeNull();
        saved!.Slots.Should().NotContain(s => s.OperatorName == "炎6");
        saved.Slots.Should().Contain(s => s.OperatorName == "炎突");
        saved.Slots.Single(s => s.OperatorName == "炎1").Should().BeEquivalentTo(source.Slots[0]);
        JsonSerializer.Serialize(source).Should().Be(original);
        if (asCopy) saved.Id.Should().NotBe(source.Id);
        else saved.Id.Should().Be(source.Id);
        var repairedStages = saved.SklandGuide?.Stages ?? saved.EditorStages!;
        var final = repairedStages.Single(s => s.IsFinal);
        final.Should().BeEquivalentTo(stages.Single(s => s.IsFinal), options => options.Excluding(s => s.Slots));
        final.Slots.Should().BeEquivalentTo(saved.Slots, options => options.WithStrictOrdering());
        var opening = repairedStages.Single(s => s.Id == "opening");
        opening.Should().BeEquivalentTo(stages[0], options => options.Excluding(s => s.Slots));
        opening.Slots.Should().HaveCount(2).And.NotContain(s => s.OperatorName == "炎6");
        opening.Slots.Single(s => s.OperatorName == "维1").Should().BeEquivalentTo(stages[0].Slots[1]);
        repairedStages.Single(s => s.Id == "empty").Slots.Should().BeEmpty();
        var operators = Enumerable.Range(1, 6).Select(i => Op($"炎{i}", "炎"))
            .Concat([Op("炎突", "炎"), Op("维1", "维多利亚")]).ToList();
        var calculator = new CovenantCalculator(operators, vm.AllCovenants.Select(c => c.Info).ToList(),
            EquipmentTestData.Rules.EquipmentToCovenant.ToDictionary(p => p.Key, p => p.Value));
        var folder = Path.Combine(Path.GetTempPath(), "repair-stages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new LineupStore(folder, EquipmentTestData.Rules);
            await store.SaveAsync(source);
            var storedOriginal = JsonSerializer.Serialize(source);
            await store.SaveAsync(saved);
            var loaded = (await store.LoadAllAsync()).Single(l => l.Id == saved.Id);
            var reopened = new LineupEditorViewModel(loaded, operators, [], calculator);
            reopened.SelectPhase(LineupPhase.后期);
            reopened.Slots.Where(s => s.OperatorName.Length > 0).Should().BeEquivalentTo(saved.Slots, options => options.WithStrictOrdering());
            var resaved = reopened.ToLineup();
            resaved.Slots.Should().BeEquivalentTo(saved.Slots, options => options.WithStrictOrdering());
            var shared = await new LineupShareService(EquipmentTestData.Rules, folder).ImportAsync(JsonSerializer.Serialize(resaved));
            shared.Slots.Should().BeEquivalentTo(saved.Slots, options => options.WithStrictOrdering());
            tracking.SelectStage(tracking.StageChoices.Single(c => c.StageId == "opening"));
            tracking.SetAutomaticStageSwitch(true);
            if (asCopy)
            {
                tracking.TrackMultipleLineups = true;
                tracking.TrackLineup(shared);
                tracking.TrackedLineupIds.Should().Contain(source.Id);
            }
            else
            {
                tracking.ApplyTrackedLineup(shared);
                tracking.SelectedStageChoice!.StageId.Should().Be("opening");
                tracking.AutomaticStageSwitch.Should().BeTrue();
            }
            tracking.SelectStage(tracking.StageChoices.Single(c => c.LineupId == shared.Id && c.StageId == final.Id));
            tracking.ActiveEntries.Where(e => e.Type == TrackingType.Operator && e.Origins.Any(o => o.LineupId == shared.Id && o.StageId == final.Id)).Select(e => e.Name)
                .Should().BeEquivalentTo(saved.Slots.Select(s => s.OperatorName));
            tracking.TrackedOperators.Single(e => e.Name == "炎1").IsAcquired.Should().BeTrue();
            tracking.TrackedEquipment.Single(e => e.Name == "坚守盾牌").IsAcquired.Should().BeTrue();
            tracking.SelectStage(tracking.StageChoices.Single(c => c.LineupId == shared.Id && c.StageId == "opening"));
            tracking.ActiveEntries.Where(e => e.Type == TrackingType.Operator && e.Origins.Any(o => o.LineupId == shared.Id && o.StageId == "opening"))
                .Select(e => e.Name).Should().BeEquivalentTo(opening.Slots.Select(s => s.OperatorName));
            JsonSerializer.Serialize(pool).Should().Be(acquired);
            JsonSerializer.Serialize(source).Should().Be(storedOriginal);
        }
        finally { Directory.Delete(folder, true); }
    }
}
