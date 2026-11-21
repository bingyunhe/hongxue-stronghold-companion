using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Services;

public sealed record LiveUnit(FieldMark Mark, long Id, string? Name, string? Purchase, bool Final, bool Manual)
{
    internal bool NeedsReview { get; init; }
    internal bool FromPair { get; init; }
    internal string? RepeatedSprite { get; init; }
    public bool FromPurchaseList { get; init; }
    public long? MemberId { get; internal init; }
    internal FieldMark? NameOnlySample { get; init; }
    public FieldMemoryLocation? Suggestion { get; init; }
    public string IdentitySource => Name == null ? "身份待确认" : Manual ? "手动指定" : Final ? "游戏详情已核实"
        : MemberId.HasValue ? "本局外观与站位匹配" : FromPair ? "购买名单与棋子外观匹配" : FromPurchaseList ? "购买名单唯一匹配" : Purchase != null ? "购买与棋子外观匹配" : "棋子外观匹配";
}
public sealed record LiveRoster(string? Map, int? Round, IReadOnlyList<LiveUnit> Units)
{
    public IReadOnlyList<string> Bought { get; init; } = [];
    public FieldObservation[] Observations { get; init; } = [];
    public IReadOnlyList<FieldMemoryLocation> Unlocated { get; init; } = [];
}
public sealed record FieldMemoryLocation(long Id,string Name,FieldSlot LastSlot);
internal sealed record FieldPurchase(string Name, double ShopX, bool Confirmed);

/// <summary>Only this game's units own corrections. A slot is never a persistent identity.</summary>
internal sealed partial class LiveFieldState(IReadOnlyDictionary<string, int> tiers)
{
    public LiveRoster Roster { get; private set; } = new(null, null, []);
    private LiveRoster _observed = new(null, null, []);
    private LiveRoster? _offerBaseline;
    private long? _offerBaselineTick;
    private readonly List<FieldPurchaseBatch> _batches = [];
    private readonly Dictionary<long, string?> _inferred = [];
    private bool _purchasesChanged;
    private long _nextId, _lastObservation;
    private List<ShopItem> _shop = [];
    private FieldPurchase? _selectedCard;

    public ShopItem? ObservePurchaseButton(BmpPixels frame)
    {
        foreach (var card in _shop)
        {
            if (!tiers.ContainsKey(card.Name)) continue;
            if (!ShopPurchaseDetector.HasPurchaseButton(frame, card)) continue;
            if (_selectedCard?.Name == card.Name && Math.Abs(_selectedCard.ShopX-card.OcrRegion.X)<.018) return null;
            _selectedCard = new(card.Name,card.OcrRegion.X,false);
            return card;
        }
        _selectedCard = null;
        return null;
    }
    private ShopEconomy? _wallet;
    private HashSet<string>? _awaitingDebit;
    private readonly List<FieldPurchase> _pending = [];
    private long _purchaseAt, _earlyDebitAt;
    private long? _hiddenAt;
    private int? _boughtRound;
    private readonly List<string> _bought = [];
    private readonly Dictionary<string,int> _confirmedCopies = [];
    private readonly Dictionary<long, (LiveUnit Unit, long At)> _missing = [];
    private readonly HashSet<long> _partialLoss = [];

    public void Clear()
    {
        Roster = _observed = new(null, null, []);
        _offerBaseline = null; _offerBaselineTick = null;
        _batches.Clear(); _inferred.Clear(); _purchasesChanged = false;
        _bought.Clear(); _confirmedCopies.Clear(); _boughtRound = null; _selectedCard = null;
        CloseShop(); _pending.Clear(); _missing.Clear(); _partialLoss.Clear(); _members.Clear(); _memoryChoices.Clear();
    }

    public bool NeedsBalance(IReadOnlyList<ShopItem> cards) =>
        _wallet == null || cards.Count < _shop.Count;
    internal HashSet<string> KnownReviewNames() => _members.Values.Select(m=>m.Name)
        .Concat(_confirmedCopies.Where(p=>p.Value>0).Select(p=>p.Key)).ToHashSet();

    internal string[] ReviewShapeCandidates(FieldMark mark)
    {
        // Purchase counts describe this observed batch only. Use unused copies
        // to break close review ranks without excluding gifts or untracked buys.
        return mark.ShapeCandidates.OrderByDescending(c=>
        {
            int bought=_boughtRound==Roster.Round?_bought.Count(n=>n==c.Name):0;
            int located=Roster.Units.Count(u=>u.Name==c.Name);
            return c.Score+(bought>located || Roster.Unlocated.Any(u=>u.Name==c.Name) ? .025 : 0);
        }).Take(3).Select(c=>c.Name).ToArray();
    }
    public void HideShop(long now) => _hiddenAt ??= now;
    private void CloseShop() { _shop.Clear(); _wallet = null; _awaitingDebit = null; _earlyDebitAt = 0; _hiddenAt = null; }
    public void Shop(BmpPixels frame, IReadOnlyList<ShopItem> cards, ShopEconomy? current, bool open, long now)
    {
        if (!open) HideShop(now);
        else if (_hiddenAt is long hidden && now-hidden>3500) CloseShop();
        if (open) _hiddenAt=null;
        if (!open || current is not { Round: not null, IsPreparation: true, Money: not null })
        {
            // Deployment briefly folds the shop. Only compare after it reopens in the same
            // round with the remaining named slots unchanged, inside the time window above.
            if (current is { IsPreparation:false }) CloseShop();
            else if (open && current == null && cards.Count >= _shop.Count) _shop = cards.ToList();
            return;
        }
        if (_boughtRound != current.Round) { _bought.Clear(); _boughtRound = current.Round; }
        bool Same(ShopItem a, ShopItem b) => a.Name == b.Name &&
            Math.Abs(a.OcrRegion.X - b.OcrRegion.X) < .018 && Math.Abs(a.OcrRegion.Y - b.OcrRegion.Y) < .015;
        if (_wallet?.Round == current.Round && _wallet.Mode == current.Mode &&
            cards.Count < _shop.Count && cards.All(c => _shop.Any(p => Same(c,p))))
        {
            // Selecting a card replaces its text with 确认购买 before debit or slot clearance.
            // Keep its last name across this animation; multi-buy may skip the intermediate balance.
            var lost = _shop.Where(p => !cards.Any(c => Same(c,p)) && ShopPurchaseDetector.IsEmpty(frame,p)).ToList();
            if (lost.Count == 0 || _wallet.Money <= current.Money)
            {
                var pendingNames = _shop.Where(p => tiers.ContainsKey(p.Name) && !cards.Any(c => Same(c,p))).Select(p=>p.Name).ToHashSet();
                if (_awaitingDebit == null || !_awaitingDebit.SetEquals(pendingNames)) _purchasesChanged = true;
                _awaitingDebit = pendingNames;
                return;
            }
            _awaitingDebit = null;
            if (now - _purchaseAt > 4000) _pending.Clear();
            if (cards.Count > 0 && _offerBaseline is { Map: not null } before && before.Round == current.Round)
            {
                var names = lost.Where(c => tiers.ContainsKey(c.Name)).Select(c => c.Name).ToList();
                if (names.Count > 0)
                {
                    var batch = _batches.LastOrDefault();
                    if (batch == null || batch.Bound || batch.Map != before.Map || batch.Round != current.Round)
                    {
                        batch = new(before);
                        _batches.Add(batch);
                    }
                    batch.Names.AddRange(names);
                    _purchasesChanged = true;
                }
            }
            foreach (var card in lost.Where(p=>tiers.ContainsKey(p.Name)))
            {
                // An entire page can disappear during a paid refresh. Require a matching
                // unit on the bench before accepting a transaction with no surviving cards.
                bool confirmed = cards.Count > 0;
                _pending.Add(new(card.Name,card.OcrRegion.X,confirmed));
                if (confirmed) _bought.Add(card.Name);
            }
            _purchaseAt = now;
            _shop.RemoveAll(lost.Contains);
            _wallet = current;
            _earlyDebitAt = 0;
            return;
        }
        if (_wallet?.Round == current.Round && _wallet.Mode == current.Mode && _wallet.Money > current.Money &&
            cards.Count == _shop.Count && cards.All(c=>_shop.Any(p=>Same(c,p))))
        {
            if (_earlyDebitAt == 0) _earlyDebitAt = now;
            if (now - _earlyDebitAt < 1000) return;
        }
        _earlyDebitAt = 0;
        if (_wallet?.Round != current.Round || _wallet.Mode != current.Mode || cards.Any(c => !_shop.Any(p => Same(c,p))))
        {
            _offerBaseline = _observed.Round == current.Round ? _observed : null;
            _offerBaselineTick = now;
        }
        if (_awaitingDebit != null) { _awaitingDebit=null; _purchasesChanged=true; }
        _shop = cards.ToList(); _wallet = current;
    }

    internal void RecordConfirmedPurchases(IReadOnlyList<string> names)
    {
        foreach (var name in names) _confirmedCopies[name]=_confirmedCopies.GetValueOrDefault(name)+1;
    }

    public bool PublishPurchases(int round)
    {
        if (_boughtRound != round || Roster.Round != round || Roster.Bought.SequenceEqual(_bought)) return false;
        _observed = _observed with { Bought = _bought.ToArray() };
        Publish(true);
        return true;
    }

    internal static double Difference(byte[] a, byte[] b)
    {
        int total = 0;
        for (int i = 0; i < a.Length; i++) total += Math.Abs(a[i] - b[i]);
        return (double)total / a.Length;
    }

    public void Observe(string map, int round, List<FieldMark> marks, long now, Action<FieldMark,string>? samplePose = null)
    {
        bool baseline = _observed.Map == map;
        bool changed = !baseline || _observed.Round != round;
        if (!baseline) { _batches.Clear(); _inferred.Clear(); _memoryChoices.Clear(); }
        if (!baseline || Roster.Round != round || now - _purchaseAt > 4000) _pending.Clear();
        var occupied = baseline ? Roster.Units.Select(u => u.Mark.Slot).ToHashSet() : [];
        var old = baseline ? _observed.Units.ToDictionary(u => u.Mark.Slot) : [];
        if (!baseline) _missing.Clear();
        if (marks.Count == 0 || !baseline || _purchasesChanged) _partialLoss.Clear();
        foreach (var key in _missing.Where(p => now - p.Value.At > (_partialLoss.Contains(p.Key) ? 6000 : 4000)).Select(p => p.Key).ToArray())
        {
            _missing.Remove(key);
            _partialLoss.Remove(key);
        }
        long previousMaxId = _nextId;
        var next = new List<LiveUnit>();
        var added = new List<FieldMark>();
        MatchMemory(marks);
        if (_memoryChoices.Count>0)
            marks=marks.Select(m=>_memoryChoices.TryGetValue(m.Slot,out var choice) && choice.Strong ?
                m with { StandaloneName=choice.Member.Name } : m).ToList();
        // A confirmed purchase can corroborate a visually located low-profile unit.
        // If copies compete for the same evidence, leave them for review.
        foreach (var group in marks.Where(m=>m.LowPose!=null && m.StandaloneName==null).GroupBy(m=>m.LowPose!.Name))
        {
            if (group.Count()!=1 || _confirmedCopies.GetValueOrDefault(group.Key)<=
                marks.Count(m=>m.StandaloneName==group.Key || old.TryGetValue(m.Slot,out var u) && u.Final && u.Name==group.Key))
                continue;
            var m=group.First();
            if (m.LowPose!.Score>=.77 && (!old.TryGetValue(m.Slot,out var previous) || !previous.Final || previous.Name==group.Key))
            {
                marks=marks.ToList();
                marks[marks.IndexOf(m)]=m with { StandaloneName=group.Key };
            }
        }
        string? Identity(LiveUnit u) => u.Final ? u.Name : _inferred.GetValueOrDefault(u.Id) ?? u.Name;
        var moving = new Dictionary<string, long>();
        foreach (var mark in marks.Where(m => m.StandaloneName != null))
        {
            var sources = old.Values.Where(u => u.Mark.Tier == mark.Tier && Identity(u) == mark.StandaloneName).ToArray();
            if (sources.Length != 1 || sources[0].Mark.Slot == mark.Slot ||
                marks.Count(m => m.StandaloneName == mark.StandaloneName) != 1) continue;
            var source = sources[0];
            if (source.NameOnlySample!=null) continue;
            // A newly deployed copy can be the only one whose name is readable.
            // Keep the original identity if its still-occupied tile looks unchanged.
            if (marks.Any(m => m.Slot == source.Mark.Slot && m.Tier == source.Mark.Tier &&
                m.StandaloneName == null && Difference(source.Mark.Appearance, m.Appearance) < 5)) continue;
            moving.Add(mark.StandaloneName!, source.Id);
        }
        foreach (var mark in marks)
        {
            if (old.TryGetValue(mark.Slot, out var previous) && !moving.ContainsValue(previous.Id) &&
                (mark.StandaloneName == null || !moving.ContainsKey(mark.StandaloneName)) &&
                SameOccupant(previous,mark,marks,now))
            {
                old.Remove(mark.Slot);
                var observed = Reconcile(previous, mark);
                changed |= previous.Name != observed.Name || (_batches.Count > 0 &&
                    (previous.RepeatedSprite != observed.RepeatedSprite || !ReferenceEquals(previous.Mark.Scores, mark.Scores) ||
                     !ReferenceEquals(previous.Mark.ShapeCandidates,mark.ShapeCandidates)));
                next.Add(observed);
            }
            else added.Add(mark);
        }
        if (next.Count == 0) _partialLoss.Clear();
        bool partial = old.Count >= 2 && next.Count > 0 && added.Count == 0 && !_purchasesChanged;
        foreach (var unit in old.Values)
        {
            _missing[unit.Id] = (unit, now);
            if (partial) _partialLoss.Add(unit.Id);
        }
        var edges = new Dictionary<FieldSlot, long[]>();
        foreach (var mark in added)
        {
            var candidates = _missing.Values.Where(v => v.Unit.NameOnlySample==null && v.Unit.Mark.Tier == mark.Tier && CanCarry(v.Unit,mark) &&
                (v.Unit.Mark.Slot != mark.Slot || _partialLoss.Contains(v.Unit.Id) ||
                 !mark.Slot.Bench && now - v.At <= 1200 && mark.StandaloneName is string name &&
                 Identity(v.Unit) == name && Difference(v.Unit.Mark.Appearance, mark.Appearance) < 5 &&
                 next.Any(other => Identity(other) == name)) &&
                !(mark.Slot.Bench && _pending.Any(p => tiers[p.Name] == mark.Tier))).Select(v => v.Unit).ToArray();
            var named = candidates.Where(u => mark.StandaloneName != null && Identity(u) == mark.StandaloneName).ToArray();
            edges[mark.Slot] = named.Length > 0 ? named.Select(u => u.Id).ToArray() :
                candidates.Where(u => (mark.StandaloneName == null || Identity(u) == null) &&
                    Difference(u.Mark.Appearance, mark.Appearance) < 24).Select(u => u.Id).ToArray();
        }
        var matches = MaximumMatching(edges, null);
        foreach (var mark in added)
        {
            var match = matches.FirstOrDefault(p => p.Value == mark.Slot);
            if (match.Key != 0 && MaximumMatching(edges, (mark.Slot, match.Key)).Count < matches.Count)
            {
                var unit = _missing[match.Key].Unit;
                _missing.Remove(unit.Id);
                _partialLoss.Remove(unit.Id);
                next.Add(Reconcile(unit, mark));
                continue;
            }
            string? purchase = null;
            if (baseline && mark.Slot.Bench && !occupied.Contains(mark.Slot))
            {
                var candidates = _pending.Where(p => tiers[p.Name] == mark.Tier).ToArray();
                if (candidates.Any(c => c.Name == mark.SpriteName)) purchase = mark.SpriteName;
                if (purchase != null)
                {
                    int index = _pending.FindIndex(c => c.Name == purchase);
                    if (!_pending[index].Confirmed) _bought.Add(purchase);
                    _pending.RemoveAt(index);
                }
            }
            var name = purchase != null && mark.SpriteName != null && purchase != mark.SpriteName ? null : purchase;
            next.Add(new(mark, ++_nextId, name ?? mark.StandaloneName, purchase, false, false));
        }
        if (old.Count >= 2 && !_purchasesChanged && now - _lastObservation <= 4000 && _batches.Count > 0)
            _batches[^1].FollowMoves(old.Values, next, previousMaxId);
        _lastObservation = now;
        BindMemory(next,map);
        _observed = new(map, round, next) { Bought = _boughtRound == round ? _bought.ToArray() : [] };
        if (_offerBaselineTick == now)
        {
            _offerBaseline = _observed;
            _offerBaselineTick = null;
        }
        Publish(changed || old.Count != 0 || added.Count != 0, samplePose);
    }

    private bool SameOccupant(LiveUnit previous,FieldMark mark,List<FieldMark> marks,long now)
    {
        if (previous.NameOnlySample is {} confirmed)
            return (mark.StandaloneName==null || mark.StandaloneName==previous.Name) &&
                (confirmed.Tier==mark.Tier || mark.Tier==0 || confirmed.Tier==0) &&
                confirmed.Camera==mark.Camera && confirmed.MemoryPatch.Length>0 && mark.MemoryPatch.Length>0 &&
                FieldMemoryAppearance.Distance(confirmed.MemoryPatch,mark.MemoryPatch)<24 &&
                FieldMemoryAppearance.Distance(previous.Mark.MemoryPatch,mark.MemoryPatch)<24;
        if (mark.Tier==0 && previous.MemberId is long id && previous.Mark.MemoryPatch.Length>0 &&
            _members[id].Samples.Any(s=>s.Tier!=0 && s.Slot==mark.Slot))
            return KeepsConfirmedPosition(_members[id],previous.Mark,mark,marks);
        if (previous.Mark.Tier!=mark.Tier || !CanCarry(previous,mark)) return false;
        return Difference(previous.Mark.Appearance,mark.Appearance)<46 ||
            mark.SpriteName!=null && (previous.Name==mark.SpriteName || previous.Mark.SpriteName==mark.SpriteName ||
                _inferred.GetValueOrDefault(previous.Id)==mark.SpriteName) ||
            (previous.Purchase!=null || previous.Manual || _inferred.GetValueOrDefault(previous.Id)!=null) &&
                mark.SpriteName==null && now-_lastObservation<=1200;
    }

    // Only retain identities along edges required in every maximum matching.
    private static Dictionary<long, FieldSlot> MaximumMatching(Dictionary<FieldSlot, long[]> edges, (FieldSlot Slot, long Id)? excluded)
    {
        var assigned = new Dictionary<long, FieldSlot>();
        foreach (var slot in edges.Keys) Assign(slot, []);
        return assigned;

        bool Assign(FieldSlot slot, HashSet<long> seen)
        {
            foreach (long id in edges[slot])
            {
                if (excluded == (slot, id) || !seen.Add(id)) continue;
                if (assigned.TryGetValue(id, out var prior) && !Assign(prior, seen)) continue;
                assigned[id] = slot;
                return true;
            }
            return false;
        }
    }

    private void Publish(bool changed, Action<FieldMark,string>? samplePose = null)
    {
        if (_purchasesChanged || changed)
        {
            _purchasesChanged = false;
            _inferred.Clear();
            if (_awaitingDebit is { Count: > 0 } waiting && _offerBaseline?.Map == _observed.Map &&
                _offerBaseline?.Round == _observed.Round && _offerBaseline != null)
                foreach (var u in _observed.Units)
                    if (!u.Final && u.Name != null && !waiting.Contains(u.Name) && _offerBaseline.Units.All(old=>old.Id!=u.Id))
                        _inferred[u.Id]=null;
            _batches.RemoveAll(b => b.Map != _observed.Map || (!b.Bound && b.Round != _observed.Round)
                || b.Expired(_observed.Units, _missing.Keys));
            foreach (var batch in _batches)
            {
                bool alreadyBound = batch.Bound;
                bool latestInRound = batch == _batches[^1] && batch.Round == _observed.Round;
                batch.Match(_observed.Units, tiers, _inferred, latestInRound, _missing, _lastObservation, samplePose);
                if (!alreadyBound && batch.Bound && latestInRound)
                {
                    _offerBaseline = _observed;
                    foreach (var name in batch.Names)
                    {
                        int i = _pending.FindIndex(p => p.Confirmed && p.Name == name);
                        if (i >= 0) _pending.RemoveAt(i);
                    }
                }
            }
        }
        var located=_observed.Units.Where(u=>u.MemberId.HasValue).Select(u=>u.MemberId!.Value).ToHashSet();
        Roster = _observed with
        {
            Unlocated = _members.Values.Where(m=>!located.Contains(m.Id)).Select(m=>m.Location).ToArray(),
            Units = _inferred.Count==0 ? _observed.Units : _observed.Units.Select(u => !u.Final && !u.MemberId.HasValue && !u.NeedsReview && _inferred.TryGetValue(u.Id, out var name)
                ? u with { Name = name, Suggestion=name!=null?null:u.Suggestion, FromPurchaseList = name != null, FromPair = name != null && _batches.Any(b=>b.PairIdentified(u.Id)) } : u).ToArray()
        };
    }

    private LiveUnit Reconcile(LiveUnit unit, FieldMark mark)
    {
        if (unit.NeedsReview || unit.Final) return unit with { Mark = mark };
        string? repeated = unit.RepeatedSprite == mark.SpriteName ? unit.RepeatedSprite : null;
        if (mark.SpriteName != null && unit.Mark.SpriteName == mark.SpriteName &&
            !unit.Mark.Appearance.AsSpan().SequenceEqual(mark.Appearance) &&
            (mark.Scores == null || !ReferenceEquals(mark.Scores, unit.Mark.Scores))) repeated = mark.SpriteName;
        return unit with { Mark = mark, Name = unit.Purchase ?? mark.StandaloneName ?? unit.Name, RepeatedSprite = repeated };
    }

    public void ReviewFailed(long id, FieldSlot slot)
    {
        _observed = _observed with { Units = _observed.Units.Select(u => u.Id == id && u.Mark.Slot == slot && !u.Final && u.MemberId == null
            ? u with { Name = null, Purchase = null, NeedsReview = true } : u).ToArray() };
        Publish(true);
    }

    internal bool ConfirmReviewName(long id,FieldSlot slot,string name)
    {
        var current=Roster.Units.FirstOrDefault(u=>u.Id==id && u.Mark.Slot==slot);
        if (current is not { Name:null } || !tiers.ContainsKey(name)) return false;
        if (!_members.Values.Any(m=>m.Name==name) && _confirmedCopies.GetValueOrDefault(name)<=1 &&
            !Roster.Units.Any(u=>u.Id!=id && u.Name==name))
            return Confirm(id,slot,name,true);
        _observed=_observed with {Units=_observed.Units.Select(u=>u.Id==id
            ? u with { Name=name, Final=true, Manual=true, MemberId=null, Suggestion=null, NeedsReview=false, NameOnlySample=u.Mark }
            : u).ToArray()};
        Publish(true);
        return true;
    }

    public bool Confirm(long id, FieldSlot slot, string name, bool manual, FieldMark? beforeClick = null)
    {
        var u = _observed.Units.FirstOrDefault(x => x.Id == id && x.Mark.Slot == slot);
        if (u == null || u.Manual && !manual || !tiers.ContainsKey(name)) return false;
        var sample=beforeClick ?? u.Mark;
        long member=Remember(u,name,sample);
        _observed = _observed with { Units = _observed.Units.Select(x => x.Id == id
            ? x with { Name = name, MemberId=member, NameOnlySample=null, Suggestion=null, Final = true, Manual = manual, NeedsReview = false }
            : x.Suggestion?.Id==member ? x with { Suggestion=null } : x).ToArray() };
        Publish(true);
        return true;
    }
}
