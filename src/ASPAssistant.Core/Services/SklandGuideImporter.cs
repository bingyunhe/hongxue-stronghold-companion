using System.Net.Http;
using System.Text.Json;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.ViewModels;

namespace ASPAssistant.Core.Services;

public sealed record SklandGuideImportResult(Lineup Lineup, string Notice);

public sealed class SklandGuideImporter(HttpClient http, IReadOnlyList<Operator> operators, IReadOnlyList<Strategy> strategies)
{
    // Official equipment descriptions start with the activation clause, optionally inside rich-text tags.
    private static readonly System.Text.RegularExpressions.Regex DestroysItself = new(
        @"^\s*(?:<[^>]+>)*(?:装备时销毁|使用后销毁|装备后下个回合开始时销毁|装备时目标干员和本装备销毁)(?=<|[，,。；;\s]|$)");

    internal static bool KeepEquipmentOnImport(JsonElement item) =>
        item.GetProperty("name").GetString() == "人事部文档" || !DestroysItself.IsMatch(item.GetProperty("effectDesc").GetString()!);

    public static bool IsOfficialGuideUrl(string input) => Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.IdnHost.Equals("game.skland.com", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.AbsolutePath.TrimEnd('/') == "/arknights/stronghold-guide/detail";

    public async Task<SklandGuideImportResult> ImportAsync(string input, CancellationToken ct = default)
    {
        if (!IsOfficialGuideUrl(input))
            throw new ArgumentException("请粘贴森空岛卫戍协议的具体攻略详情链接。");
        var uri = new Uri(input.Trim());
        var guideId = uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
            .Where(part => part.Length == 2 && part[0] == "guideId")
            .Select(part => Uri.UnescapeDataString(part[1])).FirstOrDefault();
        if (string.IsNullOrEmpty(guideId) || !guideId.All(char.IsAsciiDigit))
            throw new ArgumentException("攻略链接缺少有效的 guideId，请复制具体攻略详情链接。");

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://zonai.skland.com/web/v1/game/garrison/guide/info?guideId={guideId}&hostNeedEditing=true");
        request.Headers.Referrer = new Uri("https://game.skland.com/");
        request.Headers.Add("Origin", "https://game.skland.com");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("code").GetInt32() != 0)
                throw new InvalidDataException($"官方攻略读取失败：{root.GetProperty("message").GetString()}");
            var data = root.GetProperty("data");
            var item = data.GetProperty("guide");
            if (item.GetProperty("invalid").GetInt32() != 0)
                throw new InvalidDataException("这篇官方攻略已失效，未导入阵容。");
            var guide = item.GetProperty("guide");
            if (guide.GetProperty("guideId").GetString() != guideId)
                throw new InvalidDataException("官方返回的攻略与链接不匹配，未导入阵容。");
            var resource = SklandGuideCatalogue.ResourceFor(data, guide);
            return ParseLineup(item, resource, operators, strategies);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            throw new InvalidDataException("官方攻略数据格式无法识别，未导入阵容。", ex);
        }
    }

    public static SklandGuideImportResult ParseLineup(JsonElement item, JsonElement resource,
        IReadOnlyList<Operator> operators, IReadOnlyList<Strategy> strategies)
    {
        try
        {
            var guide = item.GetProperty("guide");
            var guideId = guide.GetProperty("guideId").GetString()!;
            var tabs = guide.GetProperty("tabs").EnumerateArray().ToArray();
            var formations = tabs.Where(tab => tab.GetProperty("tabType").GetInt32() == 1).ToArray();
            if (formations.Length != 1)
                throw new InvalidDataException("当前支持含一个成型阵容的攻略；这篇攻略没有成型阵容或包含多个方案，未导入。");
            var formation = formations[0];
            var chars = formation.GetProperty("recChars").EnumerateArray().ToArray();
            if (chars.Length == 0)
                throw new InvalidDataException("攻略没有推荐干员，未导入阵容。");

            var lineup = new Lineup
            {
                Name = guide.GetProperty("title").GetString()!,
                SourceUrl = $"https://game.skland.com/arknights/stronghold-guide/detail?header=0&guideId={guideId}"
            };
            var unselectedCustom = new HashSet<string>();
            List<LineupSlot> ReadSlots(JsonElement tab)
            {
                var slots = new List<LineupSlot>();
                foreach (var entry in tab.GetProperty("recChars").EnumerateArray())
                {
                    var charId = entry.GetProperty("charId").GetString()!;
                    var info = resource.GetProperty("chars").GetProperty(charId);
                    var name = info.GetProperty("name").GetString()!;
                    var op = operators.FirstOrDefault(op => op.Name == name);
                    if (op is null)
                        throw new InvalidDataException($"本地尚未收录干员“{name}”，请更新干员数据后导入；未保存部分阵容。");
                    if (!op.IsAvailableForSelection) unselectedCustom.Add(name);
                    if (!op.IsCustom && info.GetProperty("shopLevel").GetInt32() != op.Tier)
                        throw new InvalidDataException($"攻略中的“{name}”与本地等阶不同，请更新数据或选择与当前版本匹配的攻略；未导入阵容。");
                    var elite = entry.GetProperty("mergeLevel").GetInt32();
                    if (elite is not (0 or 1)) throw new InvalidDataException($"干员“{name}”的精锐等级暂不支持，未导入阵容。");
                    var loadout = new SklandLoadout { CharId = charId,
                        SkillId = entry.TryGetProperty("skillId", out var skill) ? skill.GetString()! : "",
                        ModuleId = entry.TryGetProperty("moduleId", out var module) ? module.GetString()! : "" };
                    if (info.TryGetProperty("skills", out var skills))
                        loadout.Skills = skills.EnumerateArray().Select(x => new SklandChoice(x.GetProperty("skillId").GetString()!, x.GetProperty("name").GetString()!)).ToList();
                    if (info.TryGetProperty("modules", out var modules))
                        loadout.Modules = modules.EnumerateArray().Select(x => new SklandChoice(x.GetProperty("moduleId").GetString()!, x.GetProperty("name").GetString()!)).ToList();
                    if (loadout.SkillId.Length > 0 && !loadout.Skills.Any(s => s.Id == loadout.SkillId)
                        || loadout.ModuleId.Length > 0 && !loadout.Modules.Any(m => m.Id == loadout.ModuleId))
                        throw new InvalidDataException($"攻略中“{name}”的技能或模组资源不完整，未导入阵容。");
                    slots.Add(new LineupSlot { OperatorName = name, IsElite = elite == 1, GuideLoadout = loadout });
                }
                if (slots.Select(s => s.OperatorName).Distinct().Count() != slots.Count)
                    throw new InvalidDataException("攻略同一阶段包含重复干员，未导入阵容。");
                return slots;
            }
            var content = new SklandGuideContent { GuideId = guideId,
                SeasonId = guide.GetProperty("seasonId").GetString()!, VersionId = guide.GetProperty("versionId").GetString()!,
                Author = item.TryGetProperty("poster", out var poster) ? poster.GetProperty("nickname").GetString()! : "",
                ApplicableScope = guide.TryGetProperty("applicableScope", out var scope) ? scope.GetString()! : "",
                SeasonName = item.TryGetProperty("seasonTag", out var seasonTag) ? seasonTag.GetProperty("name").GetString() + " · " + seasonTag.GetProperty("phaseName").GetString() : "",
                OriginalGuide = item.Clone(), Resources = resource.Clone() };
            foreach (var (tab, index) in tabs.Select((tab, index) => (tab, index)))
            {
                int type = tab.GetProperty("tabType").GetInt32();
                if (type is not (1 or 2)) throw new InvalidDataException($"攻略包含暂不支持的页面类型 {type}，未保存部分内容。");
                bool final = type == 1;
                var stage = new SklandGuideStage { Id = final ? "final" : $"round-{index}", IsFinal = final,
                    StartRound = final ? null : tab.GetProperty("startRound").GetInt32(),
                    EndRound = final ? null : tab.GetProperty("endRound").GetInt32(), Slots = ReadSlots(tab),
                    RecommendedEquipments = tab.GetProperty("recItems").EnumerateArray()
                        .Select(x => resource.GetProperty("items").GetProperty(x.GetProperty("itemId").GetString()!))
                        .Where(KeepEquipmentOnImport)
                        .Select(x => x.GetProperty("name").GetString()!).ToList(),
                    OperationIdea = tab.GetProperty("operationIdea").GetString()! };
                stage.Name = stage.RangeText;
                var activeBonds = tab.GetProperty("activeBonds");
                if (activeBonds.ValueKind != JsonValueKind.Null)
                    foreach (var ids in activeBonds.EnumerateObject())
                        foreach (var id in ids.Value.EnumerateArray())
                            stage.ActiveCovenants.Add(resource.GetProperty("bonds").GetProperty(id.GetString()!).GetProperty("name").GetString()!);
                content.Stages.Add(stage);
            }
            content.Validate();
            lineup.SklandGuide = content;
            lineup.Slots = content.Stages.Single(s => s.IsFinal).Slots.ToList();
            foreach (var entry in guide.GetProperty("strategyIds").EnumerateArray())
            {
                var id = entry.GetString()!;
                if (!strategies.Any(strategy => strategy.Id == id))
                    throw new InvalidDataException($"本地尚未收录攻略策略“{resource.GetProperty("strategies").GetProperty(id).GetProperty("name").GetString()}”，请更新数据后导入。");
                lineup.Strategies.Add(new LineupStrategySlot
                {
                    StrategyId = id,
                    Tags = lineup.Strategies.Count == 0 ? [StrategyTag.阵容核心] : []
                });
            }
            var finalStage = content.Stages.Single(s => s.IsFinal);
            var customNotice = unselectedCustom.Count > 0
                ? $"尚未选入个人自选：{string.Join("、", unselectedCustom)}。本次不会修改自选名单。" : "";
            return new(lineup, $"已完整保留成型阵容候选 {finalStage.Slots.Count} 人、回合讲解 {content.Stages.Count - 1} 段、成型阵容推荐装备 {finalStage.RecommendedEquipments.Count} 件和策略 {lineup.Strategies.Count} 项，以及各阶段技能、模组与讲解。{customNotice}每阶段提供 15 个干员格，填入的干员均计入阶段需求；超过 15 人时请在编辑器移除多余干员后保存。列表显示成型阵容人选；追踪页可选择阶段，或开启按回合自动切换。保存后才加入我的阵容，不自动追踪。");
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            throw new InvalidDataException("官方攻略数据格式无法识别，未导入阵容。", ex);
        }
    }
}
