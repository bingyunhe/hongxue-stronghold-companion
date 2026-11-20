using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Services;

public sealed record LiveUnit(FieldMark Mark, long Id, string? Name, string? Purchase, bool Final, bool Manual)
{
    internal bool NeedsReview { get; init; }
    internal bool FromPair { get; init; }
    internal string? RepeatedSprite { get; init; }
    public bool FromPurchaseList { get; init; }
    public string IdentitySource => Name == null ? "身份待确认" : Manual ? "手动指定" : Final ? "游戏详情已核实"
        : FromPair ? "购买名单与棋子外观匹配" : FromPurchaseList ? "购买名单唯一匹配" : Purchase != null ? "购买与棋子外观匹配" : "棋子外观匹配";
}
public sealed record LiveRoster(string? Map, int? Round, IReadOnlyList<LiveUnit> Units)
{
    public IReadOnlyList<string> Bought { get; init; } = [];
}
internal sealed record FieldPurchase(string Name, double ShopX, bool Confirmed);

/// <summary>Only this game's units own corrections. A slot is never a persistent identity.</summary>
internal sealed class LiveFieldState(IReadOnlyDictionary<string, int> tiers)
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
    private readonly Dictionary<long, (LiveUnit Unit, long At)> _missing = [];
    private readonly HashSet<long> _partialLoss = [];
    private readonly Dictionary<long,(string Name, FieldMark Mark)> _verifiedLooks = [];
    private bool _lookChanged;

    public void Clear()
    {
        Roster = _observed = new(null, null, []);
        _offerBaseline = null; _offerBaselineTick = null;
        _batches.Clear(); _inferred.Clear(); _purchasesChanged = false;
        _bought.Clear(); _boughtRound = null; _selectedCard = null;
        CloseShop(); _pending.Clear(); _missing.Clear(); _partialLoss.Clear(); _verifiedLooks.Clear();
    }

    public bool NeedsBalance(IReadOnlyList<ShopItem> cards) =>
        _wallet == null || cards.Count < _shop.Count;
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
        if (!baseline) { _batches.Clear(); _inferred.Clear(); _verifiedLooks.Clear(); }
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
        if (_verifiedLooks.Count > 0)
        {
            bool sameView=!_lookChanged && marks.Count==old.Count && marks.All(m=>
                old.TryGetValue(m.Slot,out var prior) && ReferenceEquals(prior.Mark.Portrait,m.Portrait));
            _lookChanged=false;
            marks = marks.ToList();
            for (int i=0;i<marks.Count;i++)
            {
                var mark=marks[i];
                if (mark.Portrait.Length==0) continue;
                if (sameView)
                {
                    marks[i]=mark with { StandaloneName=old[mark.Slot].Mark.StandaloneName };
                    continue;
                }
                var nearest=_verifiedLooks.Values.Where(v=>v.Mark.Tier==mark.Tier && v.Mark.Slot.Bench==mark.Slot.Bench)
                    .Select(v=>(v.Name, v.Mark, Error:Difference(mark.Portrait,v.Mark.Portrait)))
                    .GroupBy(v=>v.Name).Select(g=>g.MinBy(v=>v.Error)).OrderBy(v=>v.Error).Take(2).ToArray();
                if (nearest.Length==0 || nearest[0].Error>16 || nearest.Length==2 && nearest[1].Error-nearest[0].Error<6) continue;
                var best=nearest[0];
                if (mark.StandaloneName==best.Name || mark.StandaloneName!=null && best.Error>9) continue;
                // A single reference cannot disambiguate two lookalikes on adjacent cells.
                if (marks.Any(other=>other.Slot!=mark.Slot && other.Slot.Bench==mark.Slot.Bench && other.Tier==mark.Tier &&
                    other.Portrait.Length!=0 && Difference(other.Portrait,best.Mark.Portrait)<best.Error+5)) continue;
                marks[i]=mark with { StandaloneName=best.Name };
            }
        }
        string? Identity(LiveUnit u) => u.Final ? u.Name : _inferred.GetValueOrDefault(u.Id) ?? u.Name;
        var moving = new Dictionary<string, long>();
        foreach (var mark in marks.Where(m => m.StandaloneName != null))
        {
            var sources = old.Values.Where(u => u.Mark.Tier == mark.Tier && Identity(u) == mark.StandaloneName).ToArray();
            if (sources.Length == 1 && sources[0].Mark.Slot != mark.Slot && marks.Count(m => m.StandaloneName == mark.StandaloneName) == 1)
                moving.Add(mark.StandaloneName!, sources[0].Id);
        }
        foreach (var mark in marks)
        {
            if (old.TryGetValue(mark.Slot, out var previous) && previous.Mark.Tier == mark.Tier &&
                !moving.ContainsValue(previous.Id) && (mark.StandaloneName == null || !moving.ContainsKey(mark.StandaloneName)) &&
                (Difference(previous.Mark.Appearance, mark.Appearance) < 46 || mark.SpriteName != null &&
                 (previous.Name == mark.SpriteName || previous.Mark.SpriteName == mark.SpriteName || _inferred.GetValueOrDefault(previous.Id) == mark.SpriteName) ||
                 (previous.Purchase != null || previous.Manual || _inferred.GetValueOrDefault(previous.Id) != null) && mark.SpriteName == null &&
                 now-_lastObservation <= 1200))
            {
                old.Remove(mark.Slot);
                var observed = Reconcile(previous, mark);
                changed |= previous.Name != observed.Name || (_batches.Count > 0 &&
                    (previous.RepeatedSprite != observed.RepeatedSprite || !ReferenceEquals(previous.Mark.Scores, mark.Scores)));
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
            var candidates = _missing.Values.Select(v => v.Unit).Where(u => u.Mark.Tier == mark.Tier &&
                (u.Mark.Slot != mark.Slot || _partialLoss.Contains(u.Id)) &&
                !(mark.Slot.Bench && _pending.Any(p => tiers[p.Name] == mark.Tier))).ToArray();
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
        _observed = new(map, round, next) { Bought = _boughtRound == round ? _bought.ToArray() : [] };
        if (_offerBaselineTick == now)
        {
            _offerBaseline = _observed;
            _offerBaselineTick = null;
        }
        Publish(changed || old.Count != 0 || added.Count != 0, samplePose);
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
        Roster = _inferred.Count == 0 ? _observed : _observed with
        {
            Units = _observed.Units.Select(u => !u.NeedsReview && _inferred.TryGetValue(u.Id, out var name)
                ? u with { Name = name, FromPurchaseList = name != null, FromPair = name != null && _batches.Any(b=>b.PairIdentified(u.Id)) } : u).ToArray()
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
        _observed = _observed with { Units = _observed.Units.Select(u => u.Id == id && u.Mark.Slot == slot && !u.Final
            ? u with { Name = null, Purchase = null, NeedsReview = true } : u).ToArray() };
        Publish(true);
    }

    public bool Confirm(long id, FieldSlot slot, string name, bool manual, FieldMark? beforeClick = null)
    {
        var u = _observed.Units.FirstOrDefault(x => x.Id == id && x.Mark.Slot == slot);
        if (u == null || u.Manual && !manual || !tiers.ContainsKey(name)) return false;
        _lookChanged=true;
        var sample=beforeClick ?? u.Mark;
        if (sample.Slot==slot && sample.Tier==u.Mark.Tier && sample.Portrait.Length>0)
            _verifiedLooks[id]=(name,sample);
        else _verifiedLooks.Remove(id);
        _observed = _observed with { Units = _observed.Units.Select(x => x.Id == id
            ? x with { Name = name, Final = true, Manual = manual, NeedsReview = false } : x).ToArray() };
        Publish(true);
        return true;
    }
}
