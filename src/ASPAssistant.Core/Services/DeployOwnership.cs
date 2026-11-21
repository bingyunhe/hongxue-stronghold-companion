using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Services;

public sealed record FieldOwnership(FieldObservation Frame, IReadOnlyList<LiveUnit> Units,
    long? ConfirmedTrack = null, string? PreviousName = null, string? Sold = null)
{
    public long Sequence { get; init; }
}

/// <summary>Shop/manual evidence and simultaneous board copies describe the same holdings.</summary>
public sealed class DeployOwnership(DeployPoolState pool)
{
    private Guid _game;
    private int _boardRevision;
    private readonly Dictionary<long, string> _fieldTracks = [];

    public void SetManualCount(string name, int count, int? round = null, Guid game = default)
    {
        var before = pool.CountOf(name);
        pool.SetCount(name, count);
        pool.FieldCopies.Remove(name);
        foreach (var track in _fieldTracks.Where(p => p.Value == name).Select(p => p.Key).ToArray()) _fieldTracks.Remove(track);
        if (count < before) pool.ManualLimits[name] = new(pool.CountOf(name), DateTimeOffset.UtcNow);
        else if (count > before) pool.ManualLimits.Remove(name);
        Log(name, before, round, "手动设置", game);
    }

    public bool Purchase(string name, DateTimeOffset lastOffer, int? round, Guid game = default)
    {
        int before = pool.CountOf(name);
        if (pool.ManualLimits.TryGetValue(name, out var limit) && lastOffer <= limit.ChangedAt)
        {
            Log(name, before, round, "旧商店记录，保留手动数量", game);
            return false;
        }
        bool changed = pool.ManualLimits.Remove(name);
        // A purchase proves possession. Copies may have merged; only simultaneous units prove an extra deployed copy.
        if (before == 0) { pool.SetCount(name, 1); changed = true; }
        else if (pool.FieldCopies.GetValueOrDefault(name) == before)
        {
            SetFieldCopies(name, before - 1);
            changed = true;
        }
        Log(name, before, round, before == 0 ? "商店确认" : "商店确认，已有持有记录", game);
        return changed;
    }

    public bool Observe(FieldOwnership evidence)
    {
        var frame = evidence.Frame;
        if (!frame.PersonalBoard) return false;
        if (_game != frame.Game || _boardRevision != frame.BoardRevision)
        {
            _fieldTracks.Clear();
            _game = frame.Game;
            _boardRevision = frame.BoardRevision;
        }
        if (evidence.Sold is string sold)
        {
            var before = pool.CountOf(sold);
            if (before == 0) return false;
            pool.SetCount(sold, before - 1);
            var provisional = pool.FieldCopies.GetValueOrDefault(sold);
            bool credited = evidence.ConfirmedTrack is long soldTrack && _fieldTracks.Remove(soldTrack);
            SetFieldCopies(sold, Math.Min(before - 1, Math.Max(0, provisional - (credited ? 1 : 0))));
            pool.ManualLimits[sold] = new(before - 1, DateTimeOffset.UtcNow);
            Log(sold, before, frame.Round, "实况确认出售", frame.Game);
            return true;
        }
        bool changed = false;
        var units = evidence.Units.Where(u => u.Name != null && !u.NeedsReview &&
            (u.Final || u.MemberId.HasValue || u.Name == u.Mark.StandaloneName || u.FromPair || u.FromPurchaseList || u.Name == u.Purchase)).ToList();
        var confirmed = evidence.ConfirmedTrack is long track ? units.FirstOrDefault(u => u.Id == track) : null;
        if (confirmed != null && _fieldTracks.TryGetValue(confirmed.Id, out var old) && old != confirmed.Name &&
            (evidence.PreviousName == null || evidence.PreviousName == old))
        {
            _fieldTracks.Remove(confirmed.Id);
            int before = pool.CountOf(old), provisional = pool.FieldCopies.GetValueOrDefault(old);
            if (provisional > 0 && units.Count(u => u.Name == old) < before)
            {
                pool.SetCount(old, before - 1);
                SetFieldCopies(old, provisional - 1);
                pool.ManualLimits[old] = new(before - 1, DateTimeOffset.UtcNow);
                Log(old, before, frame.Round, $"{confirmed.Mark.Slot} 纠正为{confirmed.Name}", frame.Game);
                changed = true;
            }
        }
        foreach (var group in units.GroupBy(u => u.Name!))
        {
            string name = group.Key;
            int before = pool.CountOf(name);
            int seen = Math.Min(9, group.Count());
            if (pool.ManualLimits.TryGetValue(name, out var limit))
            {
                if (confirmed?.Name == name && seen > limit.Count)
                {
                    seen = Math.Min(seen, limit.Count + 1);
                    pool.ManualLimits[name] = new(seen, DateTimeOffset.UtcNow);
                    changed = true;
                }
                else seen = Math.Min(seen, limit.Count);
            }
            if (seen <= before)
            {
                if (confirmed?.Name == name) Log(name, before, frame.Round, $"{confirmed.Mark.Slot} · {confirmed.IdentitySource}", frame.Game);
                continue;
            }
            // Every unit in the simultaneous observation contributed to its lower bound.
            // Either of two visually identical copies may be the one the player later corrects.
            foreach (var unit in group)
                _fieldTracks[unit.Id] = name;
            pool.SetCount(name, seen);
            SetFieldCopies(name, pool.FieldCopies.GetValueOrDefault(name) + seen - before);
            Log(name, before, frame.Round, $"实况 {string.Join('、', group.Select(u => u.Mark.Slot))} · {group.First().IdentitySource}", frame.Game);
            changed = true;
        }
        return changed;
    }

    private void SetFieldCopies(string name, int value)
    {
        if (value == 0) pool.FieldCopies.Remove(name);
        else pool.FieldCopies[name] = value;
    }

    private void Log(string name, int before, int? round, string source, Guid game) =>
        AppLogger.Info("DeployOwnership", $"对局 {game:N} · 回合 {round?.ToString() ?? "?"} · {name} {before} → {pool.CountOf(name)} · {source}");
}
