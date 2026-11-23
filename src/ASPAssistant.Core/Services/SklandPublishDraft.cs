using ASPAssistant.Core.Models;
using ASPAssistant.Core.ViewModels;

namespace ASPAssistant.Core.Services;

public sealed record SklandPublishOperator(string Name, bool Elite, string Skill, string Module);
public sealed record SklandPublishStage(string Name, bool Final, int? Start, int? End,
    SklandPublishOperator[] Operators, string[] Gear, string Notes);
public sealed record SklandPublishDraft(string Title, string Scope, string[] StrategyIds, SklandPublishStage[] Tabs)
{
    public static SklandPublishDraft FromLineup(Lineup lineup)
    {
        if (lineup.SklandGuide != null || lineup.SourceUrl is { } url && SklandGuideImporter.IsOfficialGuideUrl(url))
            throw new InvalidDataException("这套阵容来自森空岛，可用「分享」转发原攻略。");
        var stages = TrackingViewModel.LineupStages(lineup)
            .Where(s => s.IsFinal || s.Slots.Count > 0 || s.RecommendedEquipments.Count > 0 || s.OperationIdea.Length > 0)
            .Select(s => new SklandPublishStage(s.Name, s.IsFinal, s.StartRound, s.EndRound,
                s.Slots.Select(op => new SklandPublishOperator(op.OperatorName, op.IsElite, op.GuideLoadout?.SkillId ?? "",
                    op.GuideLoadout?.ModuleId ?? "")).ToArray(),
                s.RecommendedEquipments.Concat(s.Slots.SelectMany(op => op.Equipments)).Distinct().ToArray(), s.OperationIdea)).ToArray();
        if (!stages.Any(s => s.Final && s.Operators.Length > 0)) throw new InvalidDataException("请先填写成型阵容的干员。");
        foreach (var stage in stages)
        {
            if (stage.Notes.Length > 1000) throw new InvalidDataException($"「{stage.Name}」的攻略讲解超过官网的 1000 字上限。");
            if (!stage.Final && (!stage.Start.HasValue || !stage.End.HasValue))
                throw new InvalidDataException($"请先在阵容编辑器为「{stage.Name}」设置起止回合。");
        }
        var strategies = lineup.Strategies.OrderBy(s => s.Tags.Count == 0 ? int.MaxValue : (int)s.Tags.Min())
            .Select(s => s.StrategyId).ToArray();
        return new(lineup.Name, "", strategies, stages);
    }
}
