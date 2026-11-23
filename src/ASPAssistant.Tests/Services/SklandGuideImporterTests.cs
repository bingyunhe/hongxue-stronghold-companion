using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.Services;

public class SklandGuideImporterTests
{
    public const string Url = "https://game.skland.com/arknights/stronghold-guide/detail?header=0&guideId=2120392178422509756";
    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "skland-guide-import.json"));

    private static async Task<(List<Operator> Operators, List<Strategy> Strategies)> CatalogAsync()
    {
        var store = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "data"));
        return ((await store.LoadOperatorsAsync()).ToList(), (await store.LoadStrategiesAsync()).ToList());
    }

    [Fact]
    public async Task OfficialExamplePreservesCompleteStagesAndDoesNotAssignRecommendedEquipment()
    {
        var (operators, strategies) = await CatalogAsync();
        using var handler = new GuideHandler(Fixture);
        using var http = new HttpClient(handler);
        var importer = new SklandGuideImporter(http, operators, strategies);
        var result = await importer.ImportAsync(Url);
        result.Lineup.Name.Should().Be("2本伊比利亚能兜底能打boss流派");
        result.Lineup.Slots.Select(x => x.OperatorName).Should().Equal("蓝毒", "瑰盐", "霍尔海雅", "棘刺", "空弦", "流明", "旅骨", "海沫");
        result.Lineup.Slots.Should().OnlyContain(x => x.IsElite && x.Equipments.Count == 0 && x.Tags.Count == 0);
        result.Lineup.Strategies.Should().ContainSingle().Which.StrategyId.Should().Be("band_bonus");
        result.Lineup.Strategies[0].Tags.Should().Equal(StrategyTag.阵容核心);
        result.Lineup.EarlySlots.Should().BeEmpty();
        result.Lineup.MidSlots.Should().BeEmpty();
        result.Lineup.SupportSlots.Should().BeEmpty();
        result.Lineup.SourceUrl.Should().Be(Url);
        result.Notice.Should().Contain("10 件").And.Contain("2 段").And.Contain("不自动追踪");
        result.Lineup.SklandGuide!.Stages.Should().HaveCount(3);
        result.Lineup.SklandGuide.Stages.Select(s => s.Slots.Count).Should().Equal(8, 14, 14);
        result.Lineup.SklandGuide.Stages[0].Slots[0].GuideLoadout!.SkillId.Should().Be("skchr_bluep_1");
        result.Lineup.SklandGuide.Stages[0].OperationIdea.Should().NotBeEmpty();
        handler.Calls.Should().Be(1);
        handler.RequestUri!.Host.Should().Be("zonai.skland.com");
        handler.RequestUri.AbsolutePath.Should().Be("/web/v1/game/garrison/guide/info");
        handler.RequestUri.Query.Should().Contain("guideId=2120392178422509756");
        (await importer.ImportAsync(Url)).Lineup.Id.Should().NotBe(result.Lineup.Id);
    }

    [Fact]
    public async Task ConsumablesAreExcludedInEveryStageExceptThePersonnelDocument()
    {
        var (operators, strategies) = await CatalogAsync();
        var data = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "data"));
        var (equipment, _) = await data.LoadEquipmentAsync();
        var json = JsonNode.Parse(Fixture)!;
        var tabs = json["data"]!["guide"]!["guide"]!["tabs"]!.AsArray();
        var items = json["data"]!["seasonResourceMap"]!["act3autochess"]!["versionResourceMap"]!["V3_1"]!["items"]!.AsObject();
        foreach (var tab in tabs)
        {
            var recommended = tab!["recItems"]!.AsArray();
            recommended.Clear();
            for (int i = 0; i < equipment.Count; i++)
            {
                var id = "all-equipment-" + i;
                var item = equipment[i];
                items[id] = new JsonObject { ["name"] = item.Name, ["effectDesc"] = "<@ba.vdown>" + item.Normal.EffectDescription + "</>" };
                recommended.Add(new JsonObject { ["itemId"] = id });
            }
        }
        using var http = new HttpClient(new GuideHandler(json.ToJsonString()));
        var result = await new SklandGuideImporter(http, operators, strategies).ImportAsync(Url);
        string[] excluded = ["盟约之币", "随身身份牌", "紧急调度券", "精打细算玩偶", "简易通讯机", "见钱眼开玩偶",
            "驮兽储蓄罐", "定期存折", "寻呼模块", "一次性促销券", "信标", "拟态物质", "博士投影", "人事部记录", "投机骰子"];
        foreach (var stage in result.Lineup.SklandGuide!.Stages)
        {
            stage.RecommendedEquipments.Should().Equal(equipment.Select(e => e.Name).Except(excluded));
            stage.RecommendedEquipments.Should().Contain("人事部文档").And.Contain("卫戍推荐信").And.Contain("多功能补剂");
        }
        result.Lineup.SklandGuide.OriginalGuide!.Value.GetProperty("guide").GetProperty("tabs")[0]
            .GetProperty("recItems").GetArrayLength().Should().Be(equipment.Count, "the original guide snapshot remains intact");
        var editor = new LineupEditorViewModel(result.Lineup, operators, equipment, new CovenantCalculator(operators, [], new Dictionary<string, string>()));
        var saved = editor.ToLineup();
        var restored = JsonSerializer.Deserialize<Lineup>(JsonSerializer.Serialize(saved))!;
        restored.SklandGuide!.Stages.Should().OnlyContain(s => s.RecommendedEquipments.Contains("人事部文档")
            && !s.RecommendedEquipments.Intersect(excluded).Any());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwentyPersistentRecommendationsSurviveEditorSaveAndReopen(bool withConsumables)
    {
        var (operators, strategies) = await CatalogAsync();
        var store = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "data"));
        var (equipment, _) = await store.LoadEquipmentAsync();
        var expected = equipment.Where(e => !e.Normal.EffectDescription.Contains("销毁") && e.Name != "人事部文档")
            .Take(19).Select(e => e.Name).Append("人事部文档").ToList();
        expected.Should().HaveCount(20);
        var root = JsonNode.Parse(Fixture)!;
        var items = root["data"]!["seasonResourceMap"]!["act3autochess"]!["versionResourceMap"]!["V3_1"]!["items"]!;
        var list = new JsonArray();
        foreach (var (name, i) in expected.Select((name, i) => (name, i)))
        {
            string id = $"permanent-{i}";
            string effect = i == 0 ? "装备时销毁另一件装备，自身保留" : equipment.Single(e => e.Name == name).Normal.EffectDescription;
            items[id] = new JsonObject { ["name"] = name, ["effectDesc"] = effect };
            list.Add(new JsonObject { ["itemId"] = id });
        }
        if (withConsumables)
        {
            foreach (var name in new[] { "人事部记录", "博士投影" })
            {
                string id = items.AsObject().Single(kv => kv.Value!["name"]!.GetValue<string>() == name).Key;
                list.Insert(1, new JsonObject { ["itemId"] = id });
            }
        }
        foreach (var tab in root["data"]!["guide"]!["guide"]!["tabs"]!.AsArray()) tab!["recItems"] = list.DeepClone();
        using var http = new HttpClient(new GuideHandler(root.ToJsonString()));
        var imported = await new SklandGuideImporter(http, operators, strategies).ImportAsync(Url);
        var calc = new CovenantCalculator(operators, [], new Dictionary<string, string>());
        var editor = new LineupEditorViewModel(imported.Lineup, operators, equipment, calc);
        var folder = Directory.CreateTempSubdirectory("skland-20-");
        try
        {
            var lineups = new LineupStore(folder.FullName, calc.EquipmentRules);
            await lineups.SaveAsync(editor.ToLineup());
            var reopened = (await lineups.LoadAllAsync()).Single();
            foreach (var stage in reopened.SklandGuide!.Stages) stage.RecommendedEquipments.Should().Equal(expected);
        }
        finally { folder.Delete(true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NullBondAnnotationsPreserveTheWholeStageAndOtherPages(int index)
    {
        var (operators, strategies) = await CatalogAsync();
        using var originalHttp = new HttpClient(new GuideHandler(Fixture));
        var original = await new SklandGuideImporter(originalHttp, operators, strategies).ImportAsync(Url);
        var json = JsonNode.Parse(Fixture)!;
        json["data"]!["guide"]!["guide"]!["tabs"]![index]!["activeBonds"] = null;
        using var handler = new GuideHandler(json.ToJsonString());
        using var http = new HttpClient(handler);
        var result = await new SklandGuideImporter(http, operators, strategies).ImportAsync(Url);
        var stages = result.Lineup.SklandGuide!.Stages;
        stages.Should().HaveCount(original.Lineup.SklandGuide!.Stages.Count);
        stages[index].ActiveCovenants.Should().BeEmpty();
        stages[index].Slots.Should().BeEquivalentTo(original.Lineup.SklandGuide.Stages[index].Slots);
        stages[index].RecommendedEquipments.Should().Equal(original.Lineup.SklandGuide.Stages[index].RecommendedEquipments);
        stages[index].OperationIdea.Should().Be(original.Lineup.SklandGuide.Stages[index].OperationIdea);
        foreach (int other in Enumerable.Range(0, stages.Count).Where(i => i != index))
            stages[other].ActiveCovenants.Should().Equal(original.Lineup.SklandGuide.Stages[other].ActiveCovenants);
        handler.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData("https://evil.example/arknights/stronghold-guide/detail?guideId=1")]
    [InlineData("https://game.skland.com.evil.example/arknights/stronghold-guide/detail?guideId=1")]
    [InlineData("http://game.skland.com/arknights/stronghold-guide/detail?guideId=1")]
    [InlineData("https://name@game.skland.com/arknights/stronghold-guide/detail?guideId=1")]
    [InlineData("https://game.skland.com:8443/arknights/stronghold-guide/detail?guideId=1")]
    [InlineData("https://game.skland.com/arknights/stronghold-guide?header=0")]
    [InlineData("https://game.skland.com/arknights/stronghold-guide/detail")]
    [InlineData("https://game.skland.com/arknights/stronghold-guide/detail?guideId=abc")]
    [InlineData("https://game.skland.com/arknights/stronghold-guide/detail?guideId=1%26other%3D2")]
    public async Task InvalidLinksAreRejectedBeforeAnyRequest(string url)
    {
        using var handler = new GuideHandler(Fixture);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => new SklandGuideImporter(http, [], []).ImportAsync(url));
        handler.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("invalid")]
    [InlineData("mismatch")]
    [InlineData("missing")]
    [InlineData("noFormation")]
    [InlineData("multipleFormations")]
    [InlineData("emptyFormation")]
    [InlineData("duplicate")]
    [InlineData("unknownOperator")]
    [InlineData("tierMismatch")]
    [InlineData("unknownStrategy")]
    [InlineData("unsupportedElite")]
    [InlineData("invalidBonds")]
    public async Task UnsupportedResponsesNeverReturnAPartialDraft(string change)
    {
        var (operators, strategies) = await CatalogAsync();
        var json = JsonNode.Parse(Fixture)!;
        var guide = json["data"]!["guide"]!["guide"]!;
        var tabs = guide["tabs"]!.AsArray();
        var chars = tabs[0]!["recChars"]!.AsArray();
        var resource = json["data"]!["seasonResourceMap"]!["act3autochess"]!["versionResourceMap"]!["V3_1"]!;
        switch (change)
        {
            case "unavailable": json["code"] = 1; json["message"] = "攻略不存在"; break;
            case "invalid": json["data"]!["guide"]!["invalid"] = 1; break;
            case "mismatch": guide["guideId"] = "123"; break;
            case "missing": json.AsObject().Remove("data"); break;
            case "noFormation": tabs[0]!["tabType"] = 2; break;
            case "multipleFormations": tabs.Add(tabs[0]!.DeepClone()); break;
            case "emptyFormation": chars.Clear(); break;
            case "duplicate": chars[1] = chars[0]!.DeepClone(); break;
            case "unknownOperator": resource["chars"]!["char_129_bluep"]!["name"] = "未收录干员"; break;
            case "tierMismatch": resource["chars"]!["char_129_bluep"]!["shopLevel"] = 1; break;
            case "unknownStrategy": strategies.Clear(); break;
            case "unsupportedElite": chars[0]!["mergeLevel"] = 2; break;
            case "invalidBonds": tabs[1]!["activeBonds"] = "invalid"; break;
        }
        using var handler = new GuideHandler(json.ToJsonString());
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new SklandGuideImporter(http, operators, strategies).ImportAsync(Url));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(15)]
    [InlineData(20)]
    public async Task AllFinalMembersReachTrackingWithinFifteenSlotsAndOverflowRequiresRemoval(int count)
    {
        var (operators, strategies) = await CatalogAsync();
        var candidates = Enumerable.Range(0, 4).SelectMany(index => Enumerable.Range(1, 6)
            .Select(tier => operators.Where(x => x.Tier == tier && x.RequiredStrategyId.Length == 0 && !x.IsCustom)
                .ElementAt(index))).Take(count).ToArray();
        using var http = new HttpClient(new GuideHandler(CandidateGuide(candidates)));
        var result = await new SklandGuideImporter(http, operators, strategies).ImportAsync(Url);
        var final = result.Lineup.SklandGuide!.Stages.Single(s => s.IsFinal);
        final.Slots.Select(s => s.OperatorName).Should().Equal(candidates.Select(o => o.Name));
        final.Slots.Should().OnlyContain(s => !s.IsReserve);
        result.Lineup.EarlySlots.Should().BeEmpty(); result.Lineup.MidSlots.Should().BeEmpty();
        var vm = new LineupEditorViewModel(result.Lineup, operators, [], new CovenantCalculator(operators, [], new Dictionary<string, string>()));
        vm.Slots.Should().HaveCount(Math.Max(15, count));
        if (count > 15)
        {
            vm.ValidationError.Should().Contain("15");
            vm.ToLineup().Slots.Should().HaveCount(count, "overflow must never be silently truncated");
            for (int i = count - 1; i >= 15; i--) vm.SetSlotOperator(i, "");
        }
        vm.ValidationError.Should().BeEmpty();
        var saved = vm.ToLineup();
        saved.SklandGuide!.Stages.Single(s => s.IsFinal).Slots.Should().HaveCount(Math.Min(count, 15));
        saved.SklandGuide.Stages.Skip(1).Select(s => s.Slots.Count).Should().Equal(14, 14);
        var tracking = new TrackingViewModel(); tracking.TrackLineup(saved);
        tracking.ActiveEntries.Where(e => e.Type == TrackingType.Operator).Select(e => e.Name).Should().BeEquivalentTo(candidates.Take(15).Select(o => o.Name));
        vm.Reset();
        vm.Slots.Count(s => s.OperatorName.Length > 0).Should().Be(count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task UnselectedCustomOperatorIsKeptAsADraftWithoutChangingThePersonalSelection(int tier)
    {
        var (operators, strategies) = await CatalogAsync();
        var custom = new Operator { Name = "自选甲", Tier = 5, IsCustom = true };
        operators.Add(custom);
        var candidates = operators.Where(op => !op.IsCustom && op.RequiredStrategyId.Length == 0).Take(9).Append(custom).ToArray();
        var json = JsonNode.Parse(CandidateGuide(candidates))!;
        var info = json["data"]!["seasonResourceMap"]!["act3autochess"]!["versionResourceMap"]!["V3_1"]!["chars"]!["candidate-9"]!;
        info["shopLevel"] = tier;
        using var handler = new GuideHandler(json.ToJsonString());
        using var http = new HttpClient(handler);
        var result = await new SklandGuideImporter(http, operators, strategies).ImportAsync(Url);
        result.Lineup.SklandGuide!.Stages.Single(s => s.IsFinal).Slots.Should().Contain(x => x.OperatorName == custom.Name);
        result.Notice.Should().Contain(custom.Name).And.Contain("不会修改自选名单");
        custom.IsSelectedCustom.Should().BeFalse();
    }

    [Fact]
    public async Task NetworkErrorsAndCancellationPropagateWithoutReturningADraft()
    {
        using var handler = new GuideHandler(Fixture, HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(handler);
        var importer = new SklandGuideImporter(http, [], []);
        await Assert.ThrowsAsync<HttpRequestException>(() => importer.ImportAsync(Url));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importer.ImportAsync(Url, cancellation.Token));
    }

    [Fact]
    public async Task MalformedJsonHasAnExplicitImportError()
    {
        using var handler = new GuideHandler("<html>unavailable</html>");
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => new SklandGuideImporter(http, [], []).ImportAsync(Url));
        exception.Message.Should().Contain("数据格式");
    }

    [Fact]
    public async Task NormalEliteMixAndSourceSurviveEditorSaveStoreAndJsonSharing()
    {
        var (operators, strategies) = await CatalogAsync();
        var json = JsonNode.Parse(Fixture)!;
        json["data"]!["guide"]!["guide"]!["tabs"]![0]!["recChars"]![0]!["mergeLevel"] = 0;
        using var handler = new GuideHandler(json.ToJsonString());
        using var http = new HttpClient(handler);
        var folder = Path.Combine(Path.GetTempPath(), "skland-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var share = new LineupShareService(EquipmentTestData.Rules, Path.Combine(folder, "share"), http);
            var imported = await share.ImportOfficialGuideAsync(Url, operators, strategies);
            imported.Lineup.Slots[0].IsElite.Should().BeFalse();
            imported.Lineup.Slots.Skip(1).Should().OnlyContain(x => x.IsElite);
            var calculator = new CovenantCalculator(operators, [], new Dictionary<string, string>());
            var editor = new LineupEditorViewModel(imported.Lineup, operators, [], calculator);
            editor.Reset();
            var saved = editor.ToLineup();
            saved.SourceUrl.Should().Be(Url);
            var store = new LineupStore(Path.Combine(folder, "lineups"), calculator.EquipmentRules);
            await store.SaveAsync(saved);
            var reloaded = (await store.LoadAllAsync()).Single();
            reloaded.SourceUrl.Should().Be(Url);
            var shared = await share.ImportAsync(JsonSerializer.Serialize(reloaded));
            shared.SourceUrl.Should().Be(Url);
            shared.Slots[0].IsElite.Should().BeFalse();
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class GuideHandler(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? RequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            RequestUri = request.RequestUri;
            request.Method.Should().Be(HttpMethod.Get);
            request.Headers.Contains("cred").Should().BeFalse();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) });
        }
    }

    private static string CandidateGuide(IReadOnlyList<Operator> candidates)
    {
        var json = JsonNode.Parse(Fixture)!;
        var chars = json["data"]!["guide"]!["guide"]!["tabs"]![0]!["recChars"]!.AsArray();
        chars.Clear();
        var resource = json["data"]!["seasonResourceMap"]!["act3autochess"]!["versionResourceMap"]!["V3_1"]!["chars"]!;
        for (var i = 0; i < candidates.Count; i++)
        {
            var id = "candidate-" + i;
            chars.Add(new JsonObject { ["charId"] = id, ["mergeLevel"] = i % 2 });
            resource[id] = new JsonObject { ["name"] = candidates[i].Name, ["shopLevel"] = candidates[i].Tier };
        }
        return json.ToJsonString();
    }
}
