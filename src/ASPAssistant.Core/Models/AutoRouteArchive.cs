using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace ASPAssistant.Core.Models;

public sealed record AutoRouteConditions(string[] BannedCovenants, string[] BannedOperators, string[] LockedOperators,
    string[] TransformAllowed, int People, PlannerStrategy Strategy, bool MechElite, bool ExcludeMixedBannedOperators);

/// <summary>Portable route snapshot. Run money and ownership are never part of this archive.</summary>
public sealed record AutoRouteArchive
{
    public int Version { get; init; } = 1;
    public required AutoPlanRoute Plan { get; init; }
    public required AutoRouteConditions Conditions { get; init; }
    public required string LineupFingerprint { get; init; }
    public required string CatalogFingerprint { get; init; }
    [JsonIgnore] public string Error { get; init; } = "";
    [JsonIgnore] public JsonElement? InvalidData { get; init; }

    // A legacy hash includes notes. Upgrade it only while the original still matches.
    public AutoRouteArchive ForEditing(Lineup source) =>
        !LineupFingerprint.StartsWith("2:") && LineupFingerprint == LegacyFingerprint(source)
            ? this with { LineupFingerprint = Fingerprint(source) } : this;

    public bool Matches(Lineup source) => LineupFingerprint == (LineupFingerprint.StartsWith("2:")
        ? Fingerprint(source) : LegacyFingerprint(source));

    public static string Fingerprint(Lineup lineup) => "2:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        lineup.Slots, EditorStages = lineup.EditorStages?.Select(s => new { s.Id, s.Name, s.IsFinal, s.StartRound,
            s.EndRound, s.Slots, s.RecommendedEquipments, s.ActiveCovenants }), lineup.Strategies, lineup.TargetCovenants
    })));

    private static string LegacyFingerprint(Lineup lineup) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        lineup.Slots, lineup.EditorStages, lineup.Strategies, lineup.TargetCovenants
    })));

    public static string CatalogStamp(IEnumerable<Operator> operators, IEnumerable<CovenantInfo> covenants)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Operators = operators.OrderBy(o => o.Name, StringComparer.Ordinal).Select(o => new
            {
                o.Name, o.Tier, o.RequiredStrategyId, o.CoreCovenants, o.AdditionalCovenants, o.Profile
            }),
            Covenants = covenants.OrderBy(c => c.Name, StringComparer.Ordinal)
        })));
}

/// <summary>A damaged or future route must not hide the player's otherwise usable lineup.</summary>
public sealed class AutoRouteArchiveConverter : JsonConverter<AutoRouteArchive>
{
    public override AutoRouteArchive Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        try
        {
            var archive = document.RootElement.Deserialize<AutoRouteArchive>(options)
                ?? throw new JsonException("自动路线为空");
            if (archive.Version != 1) throw new JsonException("自动路线版本不受支持");
            if (archive.Plan is null || archive.Conditions is null || string.IsNullOrWhiteSpace(archive.LineupFingerprint)
                || string.IsNullOrWhiteSpace(archive.CatalogFingerprint)) throw new JsonException("自动路线缺少保存信息");
            var plan = archive.Plan;
            var candidate = plan.Candidate;
            if (!Enum.IsDefined(plan.Style) || candidate is null || candidate.Final is null || candidate.MainGoal is null
                || string.IsNullOrWhiteSpace(candidate.MainCovenant) || candidate.SmallGoals is null || candidate.Goals is null
                || candidate.Stackers is null || candidate.Notes is null || candidate.StyleReason is null
                || candidate.Final.Operators is null || candidate.Final.Goals is null || candidate.Final.Counts is null
                || candidate.Final.AdditionalCovenants is null || candidate.Final.MechCovenants is null
                || candidate.Final.HarmonyBonus is null || plan.Route?.Stages is not { Count: 5 } stages
                || plan.Advice is not { Count: 5 }) throw new JsonException("自动路线结构不完整");
            var conditions = archive.Conditions;
            if (conditions.People is not (8 or 9) || !Enum.IsDefined(conditions.Strategy)
                || conditions.People != candidate.Final.People || conditions.Strategy != candidate.Final.Strategy
                || conditions.BannedCovenants is null || conditions.BannedOperators is null
                || conditions.LockedOperators is null || conditions.TransformAllowed is null
                || conditions.BannedCovenants.Concat(conditions.BannedOperators).Concat(conditions.LockedOperators)
                    .Concat(conditions.TransformAllowed).Any(string.IsNullOrWhiteSpace)) throw new JsonException("自动路线生成条件无效");
            var goals = candidate.Goals.Concat(candidate.SmallGoals).Concat(candidate.Final.Goals).Append(candidate.MainGoal).ToList();
            foreach (var stage in stages)
            {
                if (stage?.Definition is not { } definition || definition.TerminalLevel is < 1 or > 6
                    || definition.MaxTier is < 1 or > 6 || definition.People is < 1 or > 9
                    || string.IsNullOrWhiteSpace(definition.Name) || definition.Required is null
                    || stage.Operators is null || stage.Covenants is null || stage.Removed is null
                    || stage.Summary?.Warnings is null || stage.Summary.OperatorLayers is null)
                    throw new JsonException("自动路线阶段无效");
                goals.AddRange(definition.Required);
                if (definition.SmallGoals is { } small) goals.AddRange(small);
                if (stage.Operators.Any(o => o is null || string.IsNullOrWhiteSpace(o.Name) || o.Tier is < 1 or > 6
                        || o.Covenants is null || !Enum.IsDefined(o.Role))
                    || stage.Covenants.Any(c => c is null || string.IsNullOrWhiteSpace(c.Name)))
                    throw new JsonException("自动路线阶段人选或盟约无效");
            }
            if (stages.Count(s => s.IsFinal) != 1 || !stages[^1].IsFinal
                || !stages.Take(4).Select(s => s.Definition.TerminalLevel).SequenceEqual(new[] { 2, 3, 4, 6 })
                || goals.Any(g => g is null || string.IsNullOrWhiteSpace(g.Name) || g.Count <= 0)
                || candidate.Stackers.Any(s => s is null || string.IsNullOrWhiteSpace(s.Name))
                || candidate.Final.Operators.Any(o => o is null || string.IsNullOrWhiteSpace(o.Name) || o.NativeCovenants is null)
                || plan.Advice.Any(a => a is null || a.Focus is null || a.UpgradeText is null || a.Reserve is null
                    || a.KeyCards is null || a.Levels is null || a.Levels.Any(l => l is null || l.Focus is null
                        || l.PurchaseText is null || l.UpgradeText is null || l.EconomyText is null
                        || l.Reserve is null || l.KeyCards is null || l.Title is null || !Enum.IsDefined(l.Pace)
                        || l.TerminalLevel is < 0 or > 6))) throw new JsonException("自动路线内容无效");
            if (plan.Advice.SelectMany(a => a.Reserve.Concat(a.Levels.SelectMany(l => l.Reserve)))
                .Any(p => p is null || string.IsNullOrWhiteSpace(p.Name))) throw new JsonException("自动路线留牌无效");
            return archive;
        }
        catch (JsonException ex)
        {
            return new AutoRouteArchive { Plan = null!, Conditions = null!, LineupFingerprint = "", CatalogFingerprint = "",
                Error = ex.Message, InvalidData = document.RootElement.Clone() };
        }
    }

    public override void Write(Utf8JsonWriter writer, AutoRouteArchive value, JsonSerializerOptions options)
    {
        if (value.InvalidData is { } raw) raw.WriteTo(writer);
        else
        {
            var resolver = new DefaultJsonTypeInfoResolver();
            resolver.Modifiers.Add(info =>
            {
                foreach (var property in info.Properties.Where(p => p.Name.Equals("IconPath", StringComparison.OrdinalIgnoreCase)))
                    property.ShouldSerialize = (_, _) => false;
            });
            JsonSerializer.Serialize(writer, value, new JsonSerializerOptions(options) { TypeInfoResolver = resolver, IgnoreReadOnlyProperties = true });
        }
    }
}
