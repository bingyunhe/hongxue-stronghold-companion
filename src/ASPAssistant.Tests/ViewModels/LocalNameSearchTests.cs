using System.Text.Json;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.ViewModels;

[Collection("Update environment")]
public class LocalNameSearchTests
{
    private static readonly string Data = Path.Combine(AppContext.BaseDirectory, "data");

    [Fact]
    public void PackagedIndexCoversEveryFixedSearchableName()
    {
        SearchMatch.LoadFile(Path.Combine(Data, "search_keys.json"));
        foreach (var (file, list, field) in new[] {
            ("operators", "operators", "name"), ("custom_operators", "operators", "name"),
            ("equipment", "equipment", "name"), ("strategies", "strategies", "name"), ("strategies", "strategies", "initiator"),
            ("covenants", "covenants", "name"), ("bosses", "bosses", "name"), ("bounties", "entries", "title") })
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Data, file + ".json")));
            foreach (var item in doc.RootElement.GetProperty(list).EnumerateArray())
            {
                var name = item.GetProperty(field).GetString()!;
                SearchMatch.InitialsOf(name).Should().NotBeNullOrEmpty(name);
                if (file == "bosses")
                    foreach (var alias in item.GetProperty("aliases").EnumerateArray())
                        SearchMatch.InitialsOf(alias.GetString()!).Should().NotBeNullOrEmpty();
            }
        }
        SearchMatch.Matches("dg yh", "帝国余晖").Should().BeTrue();
        SearchMatch.Matches("myfzgy", "盟约·辅助干员").Should().BeTrue();
        SearchMatch.Matches("yjzc", "以己之长").Should().BeTrue();
    }

    [Fact]
    public void ShortcutsWorkOnEquipmentNamesAndIndividualPoolCovenants()
    {
        SearchMatch.LoadFile(Path.Combine(Data, "search_keys.json"));
        var pool = new DeployPoolItem("测试", new Operator { CoreCovenants = ["炎"], AdditionalCovenants = ["调和", "精准"] },
            "成型 · 炎 · 调和 · 精准", false, false, false);
        pool.MatchesSearch("JZ").Should().BeTrue();
        pool.MatchesSearch("成型").Should().BeTrue();
        var equipment = new EquipmentBrowseViewModel();
        equipment.LoadEquipment([new() { Name = "人事部文档", Normal = new() { EffectDescription = "精准" } }], [], [], new Dictionary<string, string>());
        equipment.SearchText = "RSBWD";
        equipment.FilteredEquipment.Should().ContainSingle();
        equipment.SearchText = "jz";
        equipment.FilteredEquipment.Should().BeEmpty();
        equipment.SearchText = "精准";
        equipment.FilteredEquipment.Should().ContainSingle();
    }

    [Fact]
    public void SavedLineupsUseTheCurrentNameAndEveryStage()
    {
        SearchMatch.LoadFile(Path.Combine(Data, "search_keys.json"));
        var (vm, _) = LineupStateFilterTestData.Create();
        var lineup = new Lineup { Name = "我的·阵容A2", EditorStages = [new() { Slots = [new() { OperatorName = "冻河" }] }] };
        vm.Lineups.Add(lineup);
        vm.SearchText = "WD ZRA2";
        vm.FilteredLineups.Should().Contain(lineup);
        lineup.Name = "深海计划";
        vm.SearchText = "shjh";
        vm.FilteredLineups.Should().Contain(lineup);
        vm.SearchText = "wdzr";
        vm.FilteredLineups.Should().NotContain(lineup);
        vm.SearchText = "DH";
        vm.FilteredLineups.Should().Contain(lineup);
        lineup.EditorStages = null;
        lineup.SklandGuide = new() { Stages = [new() { Slots = [new() { OperatorName = "折桠" }] }] };
        vm.SearchText = "zy";
        vm.FilteredLineups.Should().Contain(lineup);
        vm.SearchText = "dh";
        vm.FilteredLineups.Should().NotContain(lineup);
    }
}
