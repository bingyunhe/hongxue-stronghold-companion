using System.Text.Json;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.ViewModels;

public class CustomOperatorTests
{
    [Fact]
    public void PlannersAndExtendedPoolOnlyUseSelectedCustomOperators()
    {
        var fixedOp = new Operator { Name = "默认", CoreCovenants = ["炎"] };
        var custom = new Operator { Name = "自选", IsCustom = true, CoreCovenants = ["炎"], Tier = 5 };
        var operators = new[] { fixedOp, custom };
        var planner = new LineupPlanner(operators, [new() { Name = "炎", BondType = "SEASON", ActivateCount = 1 }]);
        var request = new PlannerRequest { Goals = [new("炎", 1, 1, false, true)], PeopleOptions = [1], TransformOptions = [0] };
        planner.Plan(request).CandidateCount.Should().Be(1);
        var tracked = new[] { new TrackingEntry { Name = fixedOp.Name } };
        DeployPoolBuilder.Build(tracked, operators.ToDictionary(o => o.Name), new DeployPoolState())
            .SelectMany(g => g.Items).Should().NotContain(i => i.Name == custom.Name);
        custom.IsSelectedCustom = true;
        planner.Plan(request).CandidateCount.Should().Be(2);
        DeployPoolBuilder.Build(tracked, operators.ToDictionary(o => o.Name), new DeployPoolState())
            .SelectMany(g => g.Items).Should().Contain(i => i.Name == custom.Name);
    }

    [Fact]
    public async Task PrtsCatalogueIsUniqueSeparateFromDefaultsAndHasPortraitsAndProfiles()
    {
        var store = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "data"));
        var defaults = await store.LoadOperatorsAsync();
        var custom = await store.LoadCustomOperatorsAsync();
        custom.Should().HaveCountGreaterThan(12);
        custom.Select(o => o.Name).Should().OnlyHaveUniqueItems();
        custom.Select(o => o.Name).Intersect(defaults.Select(o => o.Name)).Should().BeEmpty();
        custom.Should().OnlyContain(o => o.IsCustom && !o.IsAvailableForSelection && o.Tier == 5
            && o.Normal.Traits.Count == 0 && o.Elite.Traits.Count == 0 && o.Profile != null
            && o.CoreCovenants.Count + o.AdditionalCovenants.Count > 0);
        foreach (var op in custom)
        {
            var image = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "data", op.IconPath));
            image.Take(8).Should().Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        }
    }

    [Fact]
    public void SourceSwitchesOnlyShowUserSelectedCustomOperatorsAndKeepBanFiltering()
    {
        var selected = new Operator { Name = "自选甲", IsCustom = true, IsSelectedCustom = true, Tier = 5, CoreCovenants = ["炎"] };
        var unselected = new Operator { Name = "自选乙", IsCustom = true, Tier = 5, CoreCovenants = ["炎"] };
        var fixedOp = new Operator { Name = "默认", Tier = 1, CoreCovenants = ["炎"] };
        var vm = new OperatorBrowseViewModel { SelectedOperatorSource = OperatorSourceFilter.Default };
        vm.LoadOperators([fixedOp, selected, unselected]);
        vm.FilteredOperators.Should().Equal(fixedOp);
        vm.SelectedOperatorSource = OperatorSourceFilter.Custom;
        vm.FilteredOperators.Should().Equal(selected);
        vm.CoreChips.Single(c => c.Value == "炎").Count.Should().Be(1);
        vm.SetBanChecker(name => name == selected.Name);
        vm.SelectedAvailabilityFilter = AvailabilityFilter.Available;
        vm.FilteredOperators.Should().BeEmpty();
        vm.SelectedOperatorSource = OperatorSourceFilter.All;
        vm.FilteredOperators.Should().Equal(fixedOp, selected);
        unselected.IsSelectedCustom = true;
        vm.LoadOperators([fixedOp, selected, unselected]);
        vm.CustomSelectionCount.Should().Be(2);
        vm.FilteredOperators.Should().Equal(fixedOp, selected, unselected);
    }

    [Fact]
    public async Task LegacyRosterGetsAnExplicitTierAndBothSkillsSurviveRestart()
    {
        var settings = new SettingsManager(Directory.CreateTempSubdirectory("custom-roster-").FullName);
        var store = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "data"));
        var op = (await store.LoadCustomOperatorsAsync()).Single(o => o.Name == "丰川祥子");
        await settings.SaveCustomOperatorSelectionAsync([op.Name]);
        op.SetRosterEntry((await settings.LoadCustomRosterAsync()).Single());
        op.IsTierPending.Should().BeTrue();
        op.IsAvailableForPlanning.Should().BeFalse();
        var skill = op.SkillOptions.Single(s => s.Id == "skchr_oblvns_3");
        var configured = new CustomRosterEntry(op.Name, 6, skill.Id, 1);
        await settings.SaveCustomRosterAsync([configured]);
        op.SetRosterEntry((await settings.LoadCustomRosterAsync()).Single());
        op.Normal.Skill!.Description.Should().NotBeNullOrWhiteSpace();
        op.Elite.Skill!.Description.Should().Be(skill.Elite.Description);
        op.IsAvailableForPlanning.Should().BeTrue();
        op.SetRunEntry(false);
        op.IsAvailableForPlanning.Should().BeFalse();
        var carried = configured with { Tier = 5 };
        await settings.SaveDeployPoolAsync(new DeployPoolState { CarriedCustom = [carried] });
        op.SetRunEntry(true, (await settings.LoadDeployPoolAsync()).CarriedCustom!.Single());
        op.Tier.Should().Be(5);
        op.SetRunEntry(null);
        op.Tier.Should().Be(6);
        Action duplicate = () => settings.SaveCustomRosterAsync([configured, carried]).GetAwaiter().GetResult();
        duplicate.Should().Throw<JsonException>();
        (await settings.LoadCustomRosterAsync()).Should().Equal(configured);
    }

    [Fact]
    public void ManualPlanningEnforcesTwoPerTierForLocksAndSearch()
    {
        var ops = Enumerable.Range(0,6).Select(i => new Operator {Name = $"自选{i}", IsCustom = true, CoreCovenants = ["炎"]}).ToList();
        foreach (var (op,i) in ops.Select((o,i) => (o,i))) op.SetRosterEntry(new(op.Name, i < 3 ? 5 : 6));
        var planner = new LineupPlanner(ops, [new() { Name = "炎", BondType = "SEASON", ActivateCount = 1 }]);
        PlannerRequest Request(params int[] locks) => new() { Goals = [new("炎", 4, 1, false, true)], PeopleOptions = [4], TransformOptions = [0],
            LockedOperators = locks.Select(i => ops[i].Name).ToHashSet() };
        planner.Plan(Request(0,1,2)).Error.Should().Contain("最多携带 2 人");
        var results = planner.Plan(Request(0,3)).AllLineups.ToList();
        results.Should().NotBeEmpty();
        foreach (var team in results) team.Operators.Count(o => ops.Take(3).Any(candidate => candidate.Name == o.Name)).Should().Be(2);
        foreach (var op in ops) op.SetRunEntry(false);
        planner.Plan(Request()).AllLineups.Should().BeEmpty();
    }

    [Fact]
    public async Task TwelveSelectionsRoundTripIndependentlyAndRetainABackup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aspa-custom-" + Guid.NewGuid().ToString("N"));
        var settings = new SettingsManager(directory);
        try
        {
            (await settings.LoadCustomOperatorSelectionAsync()).Should().BeEmpty();
            var names = Enumerable.Range(1, 12).Select(i => "干员" + i).ToList();
            await settings.SaveCustomOperatorSelectionAsync(names);
            (await settings.LoadCustomOperatorSelectionAsync()).Should().Equal(names);
            await settings.SaveCustomOperatorSelectionAsync([]);
            (await settings.LoadCustomOperatorSelectionAsync()).Should().BeEmpty();
            JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(Path.Combine(directory, "custom-operators.json.bak")))
                .Should().Equal(names);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[\"a\",\"a\"]")]
    [InlineData("[\"a\",\"b\",\"c\",\"d\",\"e\",\"f\",\"g\",\"h\",\"i\",\"j\",\"k\",\"l\",\"m\"]")]
    public async Task InvalidSelectionFilesAreRejectedAtTheReadBoundary(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), "aspa-custom-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "custom-operators.json"), json);
            await Assert.ThrowsAsync<JsonException>(() => new SettingsManager(directory).LoadCustomOperatorSelectionAsync());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
