using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.ViewModels;

/// <summary>The 自动 page of the 规划 tab: candidates per core covenant and the route of one of them.</summary>
public class LineupPlannerAutoTests
{
    private static Operator Op(string name, int tier, string[]? core = null, string[]? extra = null) => new()
    {
        Name = name, Tier = tier, CoreCovenants = [.. core ?? []], AdditionalCovenants = [.. extra ?? []],
        IconPath = $"icons/operators/{name}.png"
    };

    private static (LineupPlannerViewModel Vm, BanViewModel Bans) Create()
    {
        var operators = new List<Operator>();
        for (var i = 1; i <= 8; i++) operators.Add(Op($"炎{i}", (i - 1) % 6 + 1, ["炎"]));
        for (var i = 1; i <= 8; i++) operators.Add(Op($"维{i}", (i - 1) % 6 + 1, ["维多利亚"]));
        operators.Add(Op("炎突", 2, ["炎"], ["突袭"]));
        operators.Add(Op("维突", 3, ["维多利亚"], ["突袭"]));
        operators.Add(Op("突1", 1, extra: ["突袭"]));
        operators.Add(Op("精1", 1, extra: ["精准"]));
        operators.Add(Op("精2", 2, extra: ["精准"]));
        operators.Add(Op("精3", 4, extra: ["精准"]));
        var covenants = new[]
        {
            new CovenantInfo { Name = "炎", BondType = "SEASON", ActivateCount = 3 },
            new CovenantInfo { Name = "维多利亚", BondType = "SEASON", ActivateCount = 3 },
            new CovenantInfo { Name = "突袭", BondType = "REGULAR", ActivateCount = 2 },
            new CovenantInfo { Name = "精准", BondType = "REGULAR", ActivateCount = 2 },
        };
        var bans = new BanViewModel();
        var vm = new LineupPlannerViewModel(new LineupPlanner(operators, covenants), operators, bans, EquipmentTestData.Rules) { DebounceMilliseconds = 0 };
        return (vm, bans);
    }

    private static async Task<LineupPlannerViewModel> CreateOnAutoPageAsync()
    {
        var (vm, _) = Create();
        vm.IsActive = true;
        vm.ShowAutoPageCommand.Execute(null);
        await vm.PendingAutoPlan!;
        return vm;
    }

    [Fact]
    public async Task AutoPage_PlansOneCandidatePerCoreCovenantOnceItIsShown()
    {
        var (vm, _) = Create();
        vm.IsActive = true;
        vm.PendingAutoPlan.Should().BeNull("nothing is planned before the page is opened");

        vm.ShowAutoPageCommand.Execute(null);
        await vm.PendingAutoPlan!;

        vm.AutoPlans.Select(p => p.MainCovenant).Should().BeEquivalentTo("炎", "维多利亚");
        vm.AutoPlans.Select(p => p.Rank).Should().Equal(1, 2);
        vm.HasAutoPlans.Should().BeTrue();
        vm.IsAutoBusy.Should().BeFalse();
        vm.AutoStatusText.Should().Contain("2 个主盟约");
        foreach (var plan in vm.AutoPlans)
        {
            plan.Title.Should().Be($"{plan.MainCovenant} 6");
            plan.Operators.Should().HaveCount(8).And.OnlyContain(o => o.IconPath.Length > 0);
            plan.Covenants.Should().Contain(c => c.IsGoal && c.Text.StartsWith(plan.MainCovenant));
            plan.Styles.Select(s => s.Style).Should().BeEquivalentTo([AutoPlanStyle.SmallFirst, AutoPlanStyle.CoreFirst]);
            plan.Styles[0].IsRecommended.Should().BeTrue();
            plan.Styles[0].Style.Should().Be(plan.Source.RecommendedStyle);
            plan.Styles[1].IsRecommended.Should().BeFalse();
            plan.StyleReason.Should().NotBeEmpty();
        }
    }

    [Fact]
    public void Pages_AreMutuallyExclusive()
    {
        var (vm, _) = Create();
        vm.ShowGoals.Should().BeTrue();

        vm.ShowAutoPageCommand.Execute(null);
        (vm.ShowAuto, vm.ShowResults, vm.ShowGoals, vm.ShowAutoList, vm.ShowResultsPanel).Should().Be((true, false, false, true, false));

        vm.ShowResultPageCommand.Execute(null);
        (vm.ShowAuto, vm.ShowResults, vm.ShowGoals, vm.ShowAutoList, vm.ShowResultList).Should().Be((false, true, false, false, true));

        vm.ShowAutoPageCommand.Execute(null);
        vm.ShowGoalPageCommand.Execute(null);
        (vm.ShowAuto, vm.ShowResults, vm.ShowGoals).Should().Be((false, false, true));
    }

    [Fact]
    public async Task BossEvidenceRefreshesWithLevelAndClearsWithoutRunningAnotherRosterSearch()
    {
        var vm = await CreateOnAutoPageAsync();
        var original = vm.AutoPlans.Select(p => p.MainCovenant).ToList();
        var search = vm.PendingAutoPlan;
        vm.SetBossContext("假想敌：茧", 8);
        vm.AutoPlans.Single(p => p.MainCovenant == "维多利亚").Note.Should().Contain("81 局中 46 局");
        vm.SetBossContext("假想敌：茧", 16);
        vm.AutoPlans.Single(p => p.MainCovenant == "维多利亚").Note.Should().Contain("116 局中 15 局");
        vm.SetBossContext("", null);
        vm.AutoPlans.Should().OnlyContain(p => p.BossNote.Length == 0);
        vm.AutoPlans.Select(p => p.MainCovenant).Should().Equal(original);
        vm.PendingAutoPlan.Should().BeSameAs(search);
    }

    [Fact]
    public void AutoPage_WaitsUntilTheTabIsOnScreen()
    {
        var (vm, _) = Create();

        vm.ShowAutoPageCommand.Execute(null);
        vm.PendingAutoPlan.Should().BeNull();

        vm.IsActive = true;
        vm.PendingAutoPlan.Should().NotBeNull();
    }

    [Fact]
    public async Task TheTwoStylesOfOnePlan_DifferInTheEarlyStages()
    {
        var vm = await CreateOnAutoPageAsync();
        var plan = vm.AutoPlans[0];
        string[] EarlyFocus() => vm.RouteStages.Take(3).Select(s => s.FocusText).ToArray();

        await vm.PlanAutoRouteAsync(plan.Styles.Single(s => s.Style == AutoPlanStyle.CoreFirst));
        EarlyFocus().Should().OnlyContain(f => f.StartsWith("凑本家"));
        vm.RouteTitle.Should().EndWith("先大后小");

        await vm.PlanAutoRouteAsync(plan.Styles.Single(s => s.Style == AutoPlanStyle.SmallFirst));
        EarlyFocus().Should().OnlyContain(f => f.StartsWith("小盟约过渡"));
        vm.RouteStages.Should().HaveCount(5, "the second route replaces the first");
    }

    [Fact]
    public async Task BanChange_ReplansTheCandidatesAndClosesTheirRoute()
    {
        var (vm, bans) = Create();
        vm.IsActive = true;
        vm.ShowAutoPageCommand.Execute(null);
        await vm.PendingAutoPlan!;
        await vm.PlanAutoRouteAsync(vm.AutoPlans[0].Styles[0]);

        bans.SetCovenantBans(["炎"]);
        await vm.PendingAutoPlan!;

        vm.AutoPlans.Select(p => p.MainCovenant).Should().Equal("维多利亚");
        vm.ShowRoute.Should().BeFalse();
        vm.ShowAutoList.Should().BeTrue();
    }

    [Fact]
    public async Task ChangesTheCandidatesDoNotDependOn_KeepThemAndTheirRoute()
    {
        var vm = await CreateOnAutoPageAsync();
        var planned = vm.PendingAutoPlan;
        var first = vm.AutoPlans[0];
        await vm.PlanAutoRouteAsync(first.Styles[0]);

        // Toggling a target starts a target search; its results must not close the auto route.
        vm.AllCovenants.Single(c => c.Name == "炎").ToggleLevel1Command.Execute(null);
        vm.PendingSearch.Should().BeNull();

        vm.PendingAutoPlan.Should().BeSameAs(planned);
        vm.AutoPlans[0].Should().BeSameAs(first);
        vm.ShowRoute.Should().BeTrue();
        vm.RouteFromAuto.Should().BeTrue();
        vm.TotalResults.Should().Be(0);
    }

    [Fact]
    public async Task RunStateRefreshesOnlyAdvice_DefersWhileHiddenAndClearsOnNewRun()
    {
        var vm = await CreateOnAutoPageAsync();
        await vm.PlanAutoRouteAsync(vm.AutoPlans[0].Styles[0]);
        var candidateTask = vm.PendingAutoPlan;
        var stage = vm.RouteStages[2];
        var operators = stage.Operators;
        var state = new AutoPlanRunState(5, new(7, 10, 3, false, null, true), new HashSet<string>());
        vm.CurrentTerminalLevel = 5;
        vm.SetAutoRunState(state);
        stage.AdviceLevels.Single(l => l.TerminalLevel == 5).EconomyText.Should().Contain("升级后剩 7");
        vm.PendingAutoPlan.Should().BeSameAs(candidateTask);
        stage.Operators.Should().BeSameAs(operators);
        var unchanged = stage.Advice;
        vm.SetAutoRunState(state with { Owned = new HashSet<string>() });
        stage.Advice.Should().BeSameAs(unchanged, "the same semantic state produces no UI notifications");
        vm.IsActive = false;
        vm.SetAutoRunState(state with { Economy = new(7, 1, 3, false, null, true) });
        stage.Advice.Should().BeSameAs(unchanged, "hidden views defer advice updates");
        vm.IsActive = true;
        stage.AdviceLevels.Single(l => l.TerminalLevel == 5).EconomyText.Should().Contain("还差 2");
        vm.SetAutoRunState(new(0, null, new HashSet<string>()));
        stage.AdviceLevels.Should().OnlyContain(l => l.EconomyText.Contains("待确认"));
        vm.PendingAutoPlan.Should().BeSameAs(candidateTask);
        vm.CloseRouteCommand.Execute(null);
        vm.SetAutoRunState(state);
        vm.RouteStages.Should().BeEmpty();
    }

    [Fact]
    public async Task NinePeopleAndLockedOperators_ArePartOfTheRequest()
    {
        var vm = await CreateOnAutoPageAsync();

        vm.AutoNinePeople = true;
        await vm.PendingAutoPlan!;
        vm.AutoPlans.Should().NotBeEmpty().And.OnlyContain(p => p.Source.Final.People == 9 && p.Subtitle.StartsWith("9 人"));

        vm.AddLocked("精3").Should().BeTrue();
        await vm.PendingAutoPlan!;
        vm.AutoPlans.Should().NotBeEmpty().And.OnlyContain(p => p.Operators.Any(o => o.Name == "精3" && o.IsLocked));
        vm.AutoStatusText.Should().Contain("必带 1 人");
    }

    [Fact]
    public async Task TrackedAutoRouteUsesTheSameFiveStageModelAsSaving()
    {
        var vm = await CreateOnAutoPageAsync();
        Lineup? tracked = null; Lineup? saved = null;
        vm.TrackRequested += value => tracked = value;
        vm.SaveRequested += value => saved = value;
        await vm.PlanAutoRouteAsync(vm.AutoPlans[0].Styles[0]);
        vm.TrackRouteCommand.Execute(null); vm.SaveRouteCommand.Execute(null);
        tracked.Should().BeEquivalentTo(saved, options => options.Excluding(l => l!.CreatedAt).Excluding(l => l!.UpdatedAt));
        tracked!.EditorStages.Should().HaveCount(5);
        var tracking = new TrackingViewModel(); tracking.TrackLineup(tracked); tracking.SetAutomaticStageSwitch(true);
        tracking.Economy = new ShopEconomy(4, 10, 3, false, null, true);
        tracking.SelectedStageChoice!.Label.Should().Be("前期");
    }

    [Fact]
    public async Task RepairAndAutomaticInputsAreIndependentAndAutomaticCannotOverwriteTheRepairSource()
    {
        var (vm, bans) = Create();
        var source = new Lineup { Id = "repair-original", Name = "修补原阵容",
            Slots = [.. Enumerable.Range(1, 6).Select(i => new LineupSlot { OperatorName = $"炎{i}" })],
            TargetCovenants = [new() { Name = "炎", Tier = 2 }] };
        source.Slots[0].Equipments = ["坚守盾牌"];
        var original = System.Text.Json.JsonSerializer.Serialize(source);
        bans.SetBans(["炎6"]);
        vm.BeginRepair(source, source.TargetCovenants, ["炎6"]);
        vm.IsActive = true; await vm.PendingSearch!;
        var kept = vm.LockedOperators.Select(l => l.Name).ToArray();
        kept.Should().HaveCount(5);
        int overwrites = 0; vm.OverwriteRequested += _ => overwrites++;
        Lineup? saved = null; vm.SaveRequested += value => saved = value;
        vm.ShowAutoPageCommand.Execute(null); await vm.PendingAutoPlan!;
        vm.IsRepairing.Should().BeFalse();
        vm.BuildAutoRequest().LockedOperators.Should().BeEmpty();
        vm.AddLocked("精3"); await vm.PendingAutoPlan!;
        var auto = vm.AutoPlans[0];
        vm.SaveLineupCommand.Execute(auto.Lineup);
        saved!.Id.Should().NotBe(source.Id); saved.SourceUrl.Should().BeNull();
        saved.Slots.SelectMany(s => s.Equipments).Should().NotContain("坚守盾牌");
        vm.OverwriteLineupCommand.Execute(auto.Lineup); overwrites.Should().Be(0);
        vm.ShowResultPageCommand.Execute(null); await vm.PendingSearch!;
        vm.IsRepairing.Should().BeTrue(); vm.RepairSource.Should().BeSameAs(source);
        vm.LockedOperators.Select(l => l.Name).Should().Equal(kept);
        vm.BuildRequest().LockedOperators.Should().NotContain("精3");
        vm.OverwriteLineupCommand.Execute(vm.VisibleLineups[0]); overwrites.Should().Be(1);
        vm.ShowAutoPageCommand.Execute(null); await vm.PendingAutoPlan!;
        vm.LockedOperators.Select(l => l.Name).Should().Equal("精3");
        System.Text.Json.JsonSerializer.Serialize(source).Should().Be(original);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BannedLockedOperatorStaysSelectedButIsOmittedAndReturnsWhenUnbanned(bool covenantBan)
    {
        var (vm, bans) = Create(); vm.IsActive = true; vm.ShowAutoPageCommand.Execute(null); await vm.PendingAutoPlan!;
        vm.AddLocked("炎1"); await vm.PendingAutoPlan!;
        if (covenantBan) bans.SetCovenantBans(["炎"]); else bans.SetBans(["炎1"]);
        await vm.PendingAutoPlan!;
        vm.LockedOperators.Should().ContainSingle().Which.IsUnavailable.Should().BeTrue();
        vm.BuildAutoRequest().LockedOperators.Should().BeEmpty();
        vm.AutoPlans.SelectMany(p => p.Operators).Should().NotContain(o => o.Name == "炎1");
        bans.ClearBans(); await vm.PendingAutoPlan!;
        vm.LockedOperators.Should().ContainSingle().Which.IsUnavailable.Should().BeFalse();
        vm.AutoPlans.Should().NotBeEmpty().And.OnlyContain(p => p.Operators.Any(o => o.Name == "炎1" && o.IsLocked));
    }

    [Fact]
    public async Task CancelledCandidateAndRouteCannotPublishAfterPageSwitchOrNewerBan()
    {
        var (vm, bans) = Create(); vm.IsActive = true; vm.DebounceMilliseconds = 80;
        vm.ShowAutoPageCommand.Execute(null); var first = vm.PendingAutoPlan!;
        bans.SetCovenantBans(["炎"]); var second = vm.PendingAutoPlan!;
        await first; await second;
        vm.AutoPlans.Select(p => p.MainCovenant).Should().Equal("维多利亚");
        var route = vm.PlanAutoRouteAsync(vm.AutoPlans[0].Styles[0]);
        vm.ShowGoalPageCommand.Execute(null); await route;
        vm.ShowRoute.Should().BeFalse(); vm.RouteStages.Should().BeEmpty(); vm.RouteFromAuto.Should().BeFalse();
        vm.ShowAutoPageCommand.Execute(null);
        vm.AutoNinePeople = true; var pending = vm.PendingAutoPlan!;
        vm.IsActive = false; await pending;
        vm.IsAutoBusy.Should().BeFalse();
        vm.IsActive = true; await vm.PendingAutoPlan!;
        vm.AutoPlans.Should().NotBeEmpty().And.OnlyContain(p => p.Source.Final.People == 9);
        var cached = vm.PendingAutoPlan;
        vm.IsActive = false; bans.SetCovenantBans(["维多利亚"]);
        vm.PendingAutoPlan.Should().BeSameAs(cached);
        vm.IsActive = true; await vm.PendingAutoPlan!;
        vm.AutoPlans.Select(p => p.MainCovenant).Should().Equal("炎");
    }

    [Fact]
    public async Task SavedAutoRouteRestoresWithoutSearchingAndRefreshesOnlyCurrentRunState()
    {
        var vm = await CreateOnAutoPageAsync();
        await vm.PlanAutoRouteAsync(vm.AutoPlans[0].Styles[0]);
        vm.SetAutoRunState(new(5, new(7, 987, 3, false, null, true), new HashSet<string>()));
        Lineup? saved = null; vm.SaveRequested += lineup => saved = lineup; vm.SaveRouteCommand.Execute(null);
        var json = System.Text.Json.JsonSerializer.Serialize(saved);
        json.Should().NotContain("IconPath");
        var restored = System.Text.Json.JsonSerializer.Deserialize<Lineup>(json)!;
        restored.AutoRoute!.Error.Should().BeEmpty();
        restored.EditorStages.Should().HaveCount(5).And.OnlyContain(s => s.OperationIdea == "");
        restored.AutoRoute.Plan.Advice[2].Levels[1].EconomyText.Should().NotContain("升级后剩 984");
        var (fresh, bans) = Create(); fresh.IsActive = true;
        fresh.SetAutoRunState(new(5, new(8, 10, 3, false, null, true), new HashSet<string>()));
        fresh.OpenSavedAutoRoute(restored).Should().BeNull();
        fresh.PendingAutoPlan.Should().BeNull("opening an archive must not start candidate search");
        fresh.PendingSearch.Should().BeNull();
        fresh.RouteStages.Should().HaveCount(5);
        fresh.RouteStages[2].AdviceLevels[1].EconomyText.Should().Contain("升级后剩 7");
        fresh.AutoRouteContext.Should().Contain(restored.AutoRoute.Plan.Candidate.StyleReason);
        fresh.TrackRouteCommand.CanExecute(null).Should().BeTrue();
        Lineup? tracked = null; fresh.TrackRequested += lineup => tracked = lineup;
        fresh.TrackRouteCommand.Execute(null); tracked!.Id.Should().Be(restored.Id);
        var originalBans = restored.AutoRoute.Conditions.BannedCovenants.ToArray();
        bans.SetCovenantBans([restored.AutoRoute.Plan.Candidate.MainCovenant]);
        fresh.AutoRouteContext.Should().Be(vm.AutoRouteContext);
        fresh.ShowRoute.Should().BeTrue(); fresh.AutoRouteNotice.Should().Contain("本局禁用主盟约");
        fresh.TrackRouteCommand.CanExecute(null).Should().BeFalse(); fresh.SaveRouteCommand.CanExecute(null).Should().BeFalse();
        fresh.PendingAutoPlan.Should().BeNull();
        restored.AutoRoute.Conditions.BannedCovenants.Should().Equal(originalBans);
        bans.ClearBans(); fresh.TrackRouteCommand.CanExecute(null).Should().BeTrue();
        bans.SetCovenantBans([], new HashSet<string> { restored.AutoRoute!.Plan.Candidate.MainCovenant });
        fresh.AutoRouteNotice.Should().Contain("本局未出现");
        fresh.TrackRouteCommand.CanExecute(null).Should().BeFalse();
        bans.SetCovenantBans([]);
        fresh.TrackRouteCommand.CanExecute(null).Should().BeTrue();
        restored.EditorStages![1].Name = "已修改阶段";
        fresh.NotifySavedAutoRouteSourceChanged(restored);
        fresh.AutoRouteNotice.Should().Contain("阵容已修改");
        fresh.TrackRouteCommand.CanExecute(null).Should().BeFalse();
    }

    [Theory]
    [InlineData("version")]
    [InlineData("scalar")]
    [InlineData("plan-null")]
    [InlineData("goals-null")]
    [InlineData("final-goal-null")]
    [InlineData("operators-null")]
    [InlineData("required-null")]
    [InlineData("people")]
    [InlineData("levels-null")]
    [InlineData("reserve-null")]
    public async Task DamagedArchiveDoesNotHideOrRewriteTheUnderlyingLineup(string damage)
    {
        var vm = await CreateOnAutoPageAsync(); await vm.PlanAutoRouteAsync(vm.AutoPlans[0].Styles[0]);
        Lineup? saved = null; vm.SaveRequested += lineup => saved = lineup; vm.SaveRouteCommand.Execute(null);
        var root = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(saved))!;
        var data = root["autoRoute"]!;
        switch (damage)
        {
            case "version": data["Version"] = 99; break;
            case "scalar": root["autoRoute"] = "broken-route"; break;
            case "plan-null": data["Plan"] = null; break;
            case "goals-null": data["Plan"]!["Candidate"]!["Goals"] = null; break;
            case "final-goal-null": data["Plan"]!["Candidate"]!["Final"]!["Goals"] = new System.Text.Json.Nodes.JsonArray((System.Text.Json.Nodes.JsonNode?)null); break;
            case "operators-null": data["Plan"]!["Route"]!["Stages"]![0]!["Operators"] = null; break;
            case "required-null": data["Plan"]!["Route"]!["Stages"]![0]!["Definition"]!["Required"] = null; break;
            case "people": data["Conditions"]!["People"] = 99; break;
            case "levels-null": data["Plan"]!["Advice"]![0]!["Levels"] = null; break;
            case "reserve-null": data["Plan"]!["Advice"]![0]!["Levels"]![0]!["Reserve"] = new System.Text.Json.Nodes.JsonArray((System.Text.Json.Nodes.JsonNode?)null); break;
        }
        var json = root.ToJsonString();
        var folder = Path.Combine(Path.GetTempPath(), "damaged-auto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, saved!.Id + ".json"); await File.WriteAllTextAsync(path, json);
            var store = new ASPAssistant.Core.Data.LineupStore(folder, EquipmentTestData.Rules);
            var loaded = (await store.LoadAllAsync()).Single();
            loaded.Name.Should().Be(saved.Name); loaded.Slots.Should().BeEquivalentTo(saved.Slots);
            loaded.AutoRoute!.Error.Should().NotBeEmpty();
            var (fresh, _) = Create(); fresh.OpenSavedAutoRoute(loaded).Should().Contain("阵容仍可编辑和追踪");
            fresh.PendingAutoPlan.Should().BeNull();
            (await File.ReadAllTextAsync(path)).Should().Be(json, "loading must not rewrite malformed additional data");
            var roundTrip = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(loaded))!;
            System.Text.Json.Nodes.JsonNode.DeepEquals(root["autoRoute"], roundTrip["autoRoute"]).Should().BeTrue();
            var imported = await new LineupShareService(EquipmentTestData.Rules, folder).ImportAsync(json);
            imported.AutoRoute!.Error.Should().NotBeEmpty();
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task ArchiveSurvivesRenameCopyAndEditorButChangedStagesAndCatalogAreMarkedAsReference()
    {
        var vm = await CreateOnAutoPageAsync(); await vm.PlanAutoRouteAsync(vm.AutoPlans[0].Styles[0]);
        Lineup? saved = null; vm.SaveRequested += lineup => saved = lineup; vm.SaveRouteCommand.Execute(null);
        var operators = vm.RouteStages.SelectMany(s => s.Operators).Select(s => s.Operator).OfType<Operator>().DistinctBy(o => o.Name).ToList();
        var editor = new LineupEditorViewModel(saved, operators, [], new CovenantCalculator(operators, [], new Dictionary<string, string>()));
        editor.Name = "用户改名";
        var renamed = editor.ToLineup();
        AutoRouteArchive.Fingerprint(renamed).Should().Be(saved!.AutoRoute!.LineupFingerprint);
        var (fresh, _) = Create(); fresh.IsActive = true; fresh.OpenSavedAutoRoute(renamed).Should().BeNull();
        fresh.TrackRouteCommand.CanExecute(null).Should().BeTrue();
        Lineup? copied = null; fresh.SaveRequested += lineup => copied = lineup; fresh.SaveRouteCommand.Execute(null);
        copied!.Id.Should().NotBe(renamed.Id); copied.Name.Should().Be("用户改名 副本");
        copied.AutoRoute.Should().NotBeNull();
        copied.EditorStages.Should().HaveCount(5).And.OnlyContain(s => s.OperationIdea == "");
        AutoRouteArchive.Fingerprint(copied).Should().Be(copied.AutoRoute!.LineupFingerprint);
        var originalJson = System.Text.Json.JsonSerializer.Serialize(saved);
        var legacy = System.Text.Json.JsonSerializer.Deserialize<Lineup>(originalJson)!;
        legacy.EditorStages![0].OperationIdea = "旧版保存的笔记";
        var oldHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { legacy.Slots, legacy.EditorStages, legacy.Strategies, legacy.TargetCovenants })));
        legacy.AutoRoute = legacy.AutoRoute! with { LineupFingerprint = oldHash };
        var oldEditor = new LineupEditorViewModel(legacy, operators, [], new CovenantCalculator(operators, [], new Dictionary<string, string>()));
        oldEditor.SetStageNotes("新版手写笔记");
        var upgraded = oldEditor.ToLineup();
        upgraded.AutoRoute!.Matches(upgraded).Should().BeTrue();
        upgraded.EditorStages![0].Name += "结构变化";
        upgraded.AutoRoute.Matches(upgraded).Should().BeFalse();
        editor.SetStageNotes("用户修改了阶段讲解");
        var edited = editor.ToLineup(); fresh.NotifySavedAutoRouteSourceChanged(edited);
        fresh.TrackRouteCommand.CanExecute(null).Should().BeTrue();
        fresh.SaveRouteCommand.CanExecute(null).Should().BeTrue();
        System.Text.Json.JsonSerializer.Serialize(saved).Should().Be(originalJson);
        var root = System.Text.Json.Nodes.JsonNode.Parse(originalJson)!;
        root["autoRoute"]!["CatalogFingerprint"] = "changed-catalog";
        var different = System.Text.Json.JsonSerializer.Deserialize<Lineup>(root.ToJsonString())!;
        fresh.OpenSavedAutoRoute(different).Should().BeNull();
        fresh.AutoRouteNotice.Should().Contain("资料已变化"); fresh.SaveRouteCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task ArchiveStoreBacksUpOverwriteAndMeasuresPayloadAndRestoreCost()
    {
        var vm = await CreateOnAutoPageAsync(); await vm.PlanAutoRouteAsync(vm.AutoPlans[0].Styles[0]);
        Lineup? saved = null; vm.SaveRequested += lineup => saved = lineup; vm.SaveRouteCommand.Execute(null);
        var json = System.Text.Json.JsonSerializer.Serialize(saved);
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!; root.AsObject().Remove("autoRoute");
        var baseline = System.Text.Json.JsonSerializer.Deserialize<Lineup>(root.ToJsonString())!;
        var folder = Path.Combine(Path.GetTempPath(), "auto-archive-cost-" + Guid.NewGuid().ToString("N"));
        var samples = new List<object>();
        try
        {
            foreach (var (name, lineup) in new[] { ("baseline", baseline), ("archive", saved!) })
            {
                var directory = Path.Combine(folder, name);
                var store = new ASPAssistant.Core.Data.LineupStore(directory, EquipmentTestData.Rules);
                await store.SaveAsync(lineup);
                var bytes = new FileInfo(Path.Combine(directory, lineup.Id + ".json")).Length;
                var times = new List<object>();
                for (var i = 0; i < 5; i++)
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew(); await store.SaveAsync(lineup);
                    var saveMs = watch.Elapsed.TotalMilliseconds; watch.Restart();
                    var loaded = (await store.LoadAllAsync()).Single(); var loadMs = watch.Elapsed.TotalMilliseconds;
                    times.Add(new { SaveMs = saveMs, LoadMs = loadMs });
                    loaded.Slots.Should().BeEquivalentTo(lineup.Slots);
                }
                samples.Add(new { Kind = name, Bytes = bytes, Samples = times });
            }
            var backupFiles = Directory.GetFiles(Path.Combine(folder, "archive"), "*.pre-auto-route-*.bak");
            backupFiles.Should().HaveCount(5);
            foreach (var backup in backupFiles)
                System.Text.Json.JsonSerializer.Deserialize<Lineup>(await File.ReadAllTextAsync(backup))!.AutoRoute!.Error.Should().BeEmpty();
            var (fresh, _) = Create(); fresh.IsActive = true;
            var reopened = System.Text.Json.JsonSerializer.Deserialize<Lineup>(json)!;
            fresh.OpenSavedAutoRoute(reopened); var timesOpen = new List<double>();
            for (var i = 0; i < 10; i++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew(); fresh.OpenSavedAutoRoute(reopened).Should().BeNull();
                timesOpen.Add(watch.Elapsed.TotalMilliseconds);
            }
            fresh.PendingAutoPlan.Should().BeNull();
            Directory.CreateDirectory(TestOutput.Directory);
            await File.WriteAllTextAsync(Path.Combine(TestOutput.Directory, "auto-archive-performance.json"),
                System.Text.Json.JsonSerializer.Serialize(new { Storage = samples, OpenMs = timesOpen, CandidateSearches = 0 }));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task SavedAutomaticRouteHasFiveIndependentStagesAndDefaultRoundsAfterReopenAndTracking()
    {
        var vm = await CreateOnAutoPageAsync();
        Lineup? saved = null; vm.SaveRequested += value => saved = value;
        await vm.PlanAutoRouteAsync(vm.AutoPlans[0].Styles[0]); vm.SaveRouteCommand.Execute(null);
        var stages = saved!.EditorStages!;
        stages.OrderBy(s => s.StartRound).Select(s => (s.StartRound, s.EndRound)).Should()
            .Equal((1, 3), (4, 6), (7, 9), (10, 12), (13, 15));
        stages.Select(s => s.Name).Should().Equal("成型阵容", "开局", "前期", "中期", "后期");
        var folder = Path.Combine(Path.GetTempPath(), "auto-route-" + Guid.NewGuid().ToString("N"));
        var operators = vm.RouteStages.SelectMany(s => s.Operators).Select(o => o.Operator).OfType<Operator>().DistinctBy(o => o.Name).ToList();
        try
        {
            var store = new ASPAssistant.Core.Data.LineupStore(folder, EquipmentTestData.Rules);
            await store.SaveAsync(saved);
            var shared = await new LineupShareService(EquipmentTestData.Rules, folder).ImportAsync(
                System.Text.Json.JsonSerializer.Serialize((await store.LoadAllAsync()).Single()));
            var editor = new LineupEditorViewModel(shared, operators, [], new CovenantCalculator(operators, [], new Dictionary<string, string>()));
            var reopened = editor.ToLineup();
            reopened.AutoRoute.Should().NotBeNull();
            reopened.AutoRoute!.Error.Should().BeEmpty();
            AutoRouteArchive.Fingerprint(reopened).Should().Be(reopened.AutoRoute.LineupFingerprint);
            reopened.EditorStages.Should().BeEquivalentTo(stages, options => options.WithStrictOrdering());
            var tracking = new TrackingViewModel(); tracking.TrackLineup(reopened); tracking.SetAutomaticStageSwitch(true);
            var timeline = stages.OrderBy(s => s.StartRound).ToList();
            for (int round = 1; round <= 15; round++)
            {
                tracking.Economy = new ShopEconomy(round, 10, 3, false, null, true);
                tracking.SelectedStageChoice!.StageId.Should().Be(timeline[(round - 1) / 3].Id);
                tracking.ActiveEntries.Where(e => e.Type == TrackingType.Operator).Select(e => e.Name).Should()
                    .BeEquivalentTo(timeline[(round - 1) / 3].Slots.Select(s => s.OperatorName));
            }
        }
        finally { Directory.Delete(folder, true); }
    }
}
