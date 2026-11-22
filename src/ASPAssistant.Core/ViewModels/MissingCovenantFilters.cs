using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.ViewModels;

public sealed class MissingCovenantFilters
{
    public const string NoCore = "无核心盟约";
    public const string NoAdditional = "无附加盟约";

    public FilterChip Core { get; }
    public FilterChip Additional { get; }
    public IReadOnlyList<FilterChip> Chips { get; }
    public bool Active => Core.IsSelected || Additional.IsSelected;

    public MissingCovenantFilters(Action<FilterChip> changed)
    {
        Core = new(FilterChipKind.CoreCovenant, NoCore, NoCore, changed);
        Additional = new(FilterChipKind.OtherCovenant, NoAdditional, NoAdditional, changed);
        Chips = [Core, Additional];
    }

    public bool Matches(Operator? op) =>
        (!Core.IsSelected || op?.CoreCovenants.Count == 0)
        && (!Additional.IsSelected || op?.AdditionalCovenants.Count == 0);

    public void UpdateCounts(IEnumerable<Operator> candidates)
    {
        int core = 0, additional = 0;
        foreach (var op in candidates)
        {
            if (op.CoreCovenants.Count == 0 && (!Additional.IsSelected || op.AdditionalCovenants.Count == 0)) core++;
            if (op.AdditionalCovenants.Count == 0 && (!Core.IsSelected || op.CoreCovenants.Count == 0)) additional++;
        }
        Core.Count = core;
        Additional.Count = additional;
    }

    public void Clear()
    {
        Core.SetSelected(false);
        Additional.SetSelected(false);
    }
}
