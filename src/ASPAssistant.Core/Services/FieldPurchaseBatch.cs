namespace ASPAssistant.Core.Services;

/// <summary>Tracks purchases against the change in board and bench occupancy for this shopping window.</summary>
internal sealed class FieldPurchaseBatch(LiveRoster before)
{
    internal string? Map { get; } = before.Map;
    internal int? Round { get; } = before.Round;
    internal List<string> Names { get; } = [];
    private readonly HashSet<long> _before = before.Units.Select(u => u.Id).ToHashSet();
    private readonly Dictionary<long, FieldSlot> _seen = [];
    private Dictionary<long, LiveUnit>? _members;
    private long _leftId, _rightId;
    private string? _pairVote;
    private bool _pairConfirmed, _pairUsed;
    internal bool PairIdentified(long id) => _pairUsed && (id == _leftId || id == _rightId);
    private FieldMark? _leftSample, _rightSample;
    internal bool Bound => _members != null;

    internal bool Expired(IReadOnlyList<LiveUnit> visible, ICollection<long> missing) =>
        _members != null && !_members.Keys.Any(id => missing.Contains(id) || visible.Any(u => u.Id == id));

    internal void FollowMoves(IEnumerable<LiveUnit> displaced, List<LiveUnit> next, long previousMaxId)
    {
        if (_members == null) return;
        var left = displaced.Where(u => next.All(n => n.Id != u.Id)).ToList();
        var arrivals = next.Where(u => u.Id > previousMaxId).ToList();
        // Every permutation must describe a possible move of the same cohort. Carry the set,
        // never an individual correction, across a visually indistinguishable simultaneous move.
        if (left.Count < 2 || arrivals.Count != left.Count || left.Any(u => !_members.ContainsKey(u.Id)) ||
            arrivals.Any(n => left.Any(p => p.Mark.Slot == n.Mark.Slot || p.Mark.Tier != n.Mark.Tier ||
                LiveFieldState.Difference(p.Mark.Appearance, n.Mark.Appearance) >= 24))) return;
        foreach (var unit in left) _members.Remove(unit.Id);
        foreach (var unit in arrivals) _members.Add(unit.Id, unit);
    }

    internal void Match(IReadOnlyList<LiveUnit> visible, IReadOnlyDictionary<string,int> tiers,
        Dictionary<long,string?> inferred, bool latestInRound, IReadOnlyDictionary<long,(LiveUnit Unit,long At)> missing, long observedAt,
        Action<FieldMark,string>? samplePose)
    {
        _pairUsed = false;
        // Two purchases can leave the bench together between scans. Rebind the batch, not
        // the old individual identities, when every lost seat has become a new board occupant.
        if (_members != null && latestInRound)
        {
            var lost = _members.Values.Where(u => visible.All(v => v.Id != u.Id)).ToArray();
            var arrived = visible.Where(u => !_before.Contains(u.Id) && !_members.ContainsKey(u.Id)).ToArray();
            if (lost.Length > 0 && lost.Length == arrived.Length && lost.All(u => u.Mark.Slot.Bench && !u.Final && missing.TryGetValue(u.Id,out var m) && m.At == observedAt) &&
                arrived.All(u => !u.Mark.Slot.Bench) && visible.Count(u=>_before.Contains(u.Id)) == _before.Count &&
                lost.Select(u=>u.Mark.Tier).Order().SequenceEqual(arrived.Select(u=>u.Mark.Tier).Order()))
            {
                foreach (var u in lost) _members.Remove(u.Id);
                foreach (var u in arrived) _members.Add(u.Id,u);
            }
        }
        if (_members == null)
        {
            var added = new List<LiveUnit>();
            foreach (var unit in visible)
            {
                if (_before.Contains(unit.Id)) continue;
                if (unit.Final && unit.Name != null && !Names.Contains(unit.Name))
                {
                    _seen.Remove(unit.Id);
                    continue;
                }
                _seen[unit.Id] = unit.Mark.Slot;
                added.Add(unit);
            }
            if (added.Count == Names.Count && added.All(u=>!u.Mark.Slot.Bench))
                foreach (var entry in _seen.Where(p=>p.Value.Bench && added.All(u=>u.Id!=p.Key) && missing.TryGetValue(p.Key,out var m) && m.At == observedAt).ToArray()) _seen.Remove(entry.Key);
            // A sale, an untracked move or an extra unknown seat makes the cohort ambiguous.
            if (added.Count != Names.Count || _seen.Count != Names.Count || visible.Count(u => _before.Contains(u.Id)) != _before.Count ||
                visible.Any(u => _before.Contains(u.Id) && u.Name == null && inferred.GetValueOrDefault(u.Id) == null && u.Mark.Slot.Bench)) return;
            if (!Names.Select(n => tiers[n]).Order().SequenceEqual(added.Select(u => u.Mark.Tier).Order())) return;
            _members = added.ToDictionary(u => u.Id);
        }
        // Preserve the identity of sold/off-screen members for accounting, never assign it to a replacement.
        foreach (var unit in visible)
            if (_members.ContainsKey(unit.Id))
            {
                var member = unit;
                if (!unit.Final && !unit.NeedsReview && unit.Name == null && unit.RepeatedSprite is string repeat && Names.Contains(repeat))
                {
                    member = unit with { Name = repeat };
                    inferred[unit.Id] = repeat;
                }
                _members[unit.Id] = member;
            }

        foreach (var u in _members.Values)
            if (!u.Final && u.Name != null && !Names.Contains(u.Name)) inferred[u.Id]=null;

        if (latestInRound && _members.Keys.All(id => visible.Any(u => u.Id == id)) &&
            visible.Any(u => !_before.Contains(u.Id) && !_members.ContainsKey(u.Id) &&
                (u.Name == null ? Names.Any(name => tiers[name] == u.Mark.Tier) : Names.Contains(u.Name)))) return;

        var remaining = Names.ToList();
        foreach (var unit in _members.Values.Where(u => u.Final))
            if (unit.Name == null || !remaining.Remove(unit.Name)) return;

        foreach (var group in _members.Values.Where(u => !u.Final).GroupBy(u => u.Mark.Tier))
        {
            var offers = remaining.Where(n => tiers[n] == group.Key).ToList();
            if (offers.Count != group.Count()) continue;
            var unresolved = group.ToList();
            bool conflict = false;
            foreach (var known in group.Where(u => u.Name != null).GroupBy(u => u.Name!))
            {
                int count = offers.Count(n => n == known.Key);
                if (!Names.Contains(known.Key)) continue;
                if (count == 0 && _members.Values.Any(u => u.Final && u.Name == known.Key)) continue;
                if (count < known.Count())
                {
                    foreach (var unit in known) inferred[unit.Id] = null;
                    conflict = true;
                    continue;
                }
                foreach (var unit in known)
                {
                    offers.Remove(known.Key);
                    unresolved.Remove(unit);
                }
            }
            if (!conflict && offers.Count == 2 && offers[0] != offers[1] && unresolved.Count == 2 &&
                _members.Count == 2 && unresolved.All(u => visible.Any(v => v.Id == u.Id)))
            {
                var pair = unresolved.OrderBy(u => u.Id).ToArray();
                MatchPair(pair[0], pair[1], offers[0], offers[1], inferred, samplePose);
                continue;
            }
            if (conflict || offers.Count == 0 || offers.Any(n => n != offers[0])) continue;
            foreach (var unit in unresolved) inferred[unit.Id] = offers[0];
        }
    }
    private void MatchPair(LiveUnit left, LiveUnit right, string a, string b, Dictionary<long,string?> inferred, Action<FieldMark,string>? samplePose)
    {
        var l = left.Mark;
        var r = right.Mark;
        string? vote=null;
        if (l.Scores!=null && r.Scores!=null && l.Scores.TryGetValue(a,out var la) &&
            l.Scores.TryGetValue(b,out var lb) && r.Scores.TryGetValue(a,out var ra) && r.Scores.TryGetValue(b,out var rb))
        {
            la=WithPose(l,a,la); lb=WithPose(l,b,lb);
            ra=WithPose(r,a,ra); rb=WithPose(r,b,rb);
            double gap=lb+ra-la-rb;
            if (Math.Abs(gap)>=7 && (gap>0?Math.Max(la,rb):Math.Max(lb,ra))<=64) vote=gap>0?a:b;
        }
        // Compare both possible assignments in the same score space. Two
        // plausible poses must support the pairing over its reverse.
        double? Shape(FieldMark mark,string name)=>mark.ShapeCandidates.FirstOrDefault(c=>c.Name==name)?.Score;
        if (Shape(l,a) is double sla && Shape(l,b) is double slb &&
            Shape(r,a) is double sra && Shape(r,b) is double srb)
        {
            double margin=sla+srb-slb-sra;
            if (Math.Abs(margin)>=.12 && (margin>0?Math.Min(sla,srb):Math.Min(slb,sra))>=.65)
            {
                var visual=margin>0?a:b;
                vote=vote==null || vote==visual ? visual : null;
            }
        }
        if (_leftId != left.Id || _rightId != right.Id || vote != _pairVote)
        {
            _leftId = left.Id; _rightId = right.Id;
            _pairVote = vote; _pairConfirmed = false;
            _leftSample = l; _rightSample = r;
        }
        else if (vote != null && _leftSample != null && _rightSample != null &&
            (Changed(_leftSample,l) || Changed(_rightSample,r))) _pairConfirmed = true;
        if (!_pairConfirmed || vote == null) return;
        _pairUsed = true;
        inferred[left.Id] = vote;
        inferred[right.Id] = vote == a ? b : a;

        double WithPose(FieldMark mark, string name, double score)
        {
            if (mark.PurchasePoseScores?.ContainsKey(name) != true) samplePose?.Invoke(mark, name);
            return mark.PurchasePoseScores?.TryGetValue(name, out double pose) == true && pose < 34 ? Math.Min(score, pose) : score;
        }

        static bool Changed(FieldMark prior, FieldMark fresh) =>
            (!ReferenceEquals(prior.Scores,fresh.Scores) || !ReferenceEquals(prior.ShapeCandidates,fresh.ShapeCandidates))
            && !prior.Appearance.AsSpan().SequenceEqual(fresh.Appearance);
    }

}
