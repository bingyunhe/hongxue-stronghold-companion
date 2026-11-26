using System.Drawing;
using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Services;

/// <summary>Confirms a purchase from a stable named card, two empty frames and a funds decrease.</summary>
public sealed class ShopPurchaseDetector
{
    private DateTimeOffset _lastOffer;
    public DateTimeOffset ConfirmedOfferAt { get; private set; }
    private List<ShopItem> _cards = [];
    private ShopEconomy? _before;
    private int _width, _height, _stable, _emptyFrames;
    private byte[]? _refreshFrame;

    internal (byte[] Frame, ShopItem Card, double Pitch)? PriceToVerify(IReadOnlyList<ShopItem> current, ShopEconomy? hud)
    {
        if (_refreshFrame is null || _shopHiddenAt.HasValue || _afterEnemyPanel || _cards.Count < 3 ||
            current.Count != _cards.Count - 1 || !SameRemainingCards(current) || hud is not { IsPreparation: true } || hud.Round != _before?.Round ||
            hud.Mode != _before?.Mode || hud.Money is not int now || _lastFundsRead is not int previous || previous <= now) return null;
        var missing = _cards.First(c => !current.Any(other => SameSlot(c, other)));
        if (missing.Price > 0) return null;
        var positions = _cards.Select(c => c.OcrRegion.X).Order().ToArray();
        double pitch = positions.Zip(positions.Skip(1), (left, right) => right - left).Min();
        return (_refreshFrame, missing, pitch);
    }
    private string _pending = "";
    private int? _pendingMoney;
    private long? _shopHiddenAt;
    private bool _purchasePending, _afterEnemyPanel;
    private int? _confirmedFunds, _lastFundsRead;
    private int _candidateFundsReads;
    private const int CandidateFundsReadLimit = 4;
    private int? _fundsBeforeCardChange;
    private int _fundsChangeGrace;
    private string? _lastCandidateDiagnostic;
    internal bool HasStableOffer => _stable >= 2 && _emptyFrames == 0 && _confirmedFunds.HasValue;
    public bool NeedsConfirmation => _emptyFrames == 1;

    public bool NeedsFundsRead(IReadOnlyList<ShopItem> cards, bool shopOpen)
    {
        if (!shopOpen) return false;
        if (_shopHiddenAt.HasValue) return true;
        if (!SameRemainingCards(cards)) return true;
        if (cards.Count < _cards.Count) return !_purchasePending || _candidateFundsReads < CandidateFundsReadLimit;
        return _stable == 1;
    }

    public bool CanReuseHudLayout(IReadOnlyList<ShopItem> cards, int width, int height)
        => !_shopHiddenAt.HasValue && width == _width && height == _height && SameRemainingCards(cards);

    private bool SameRemainingCards(IReadOnlyList<ShopItem> cards) => _before is not null && cards.Count <= _cards.Count
        && cards.All(c => _cards.Any(p => SameSlot(c, p) && c.Name == p.Name));

    internal void PauseForEnemyPanel()
    {
        _afterEnemyPanel = true;
        _emptyFrames = 0;
    }

    public void Reset(string reason)
    {
        if (_before is not null)
            AppLogger.Info("ShopPurchase", $"Baseline cleared: reason={reason}, stable={_stable}, money={_before.Money}, pending={_purchasePending}, cards=[{string.Join(", ", _cards.Select(c => c.Name))}]");
        _cards = [];
        _refreshFrame = null;
        _before = null;
        _afterEnemyPanel = false;
        _shopHiddenAt = null;
        _stable = _emptyFrames = 0;
        _pending = "";
        _pendingMoney = null;
        _purchasePending = false;
        _confirmedFunds = _lastFundsRead = null;
        _candidateFundsReads = 0;
        _fundsBeforeCardChange = null;
        _fundsChangeGrace = 0;
        _lastCandidateDiagnostic = null;
    }

    public IReadOnlyList<string> Observe(byte[] image, IReadOnlyList<ShopItem> cards, bool shopOpen,
        ShopEconomy? economy, IReadOnlySet<string> targets, bool freshFunds = true, DateTimeOffset? capturedAt = null)
    {
        // The purchase animation can hide the shop before either empty-slot observation.
        bool stableOffers = _stable >= 2 && !_purchasePending && _confirmedFunds.HasValue;
        if (!shopOpen && (NeedsConfirmation || stableOffers) && economy is { IsPreparation: true } && economy.Round == _before?.Round)
        {
            _shopHiddenAt ??= Environment.TickCount64;
            if (Environment.TickCount64 - _shopHiddenAt <= 3500) return [];
        }
        if (_shopHiddenAt is long hidden)
        {
            bool comparableFunds = NeedsConfirmation ? _pendingMoney == economy?.Money :
                cards.Count == _cards.Count ? _confirmedFunds == economy?.Money :
                cards.Count == _cards.Count - 1 && _confirmedFunds > economy?.Money;
            if (Environment.TickCount64 - hidden > 3500 || !freshFunds || cards.Count == 0 ||
                !SameRemainingCards(cards) || !comparableFunds)
                Reset("offers-changed-while-hidden");
            _shopHiddenAt = null;
        }
        if (!shopOpen || economy is not { IsPreparation: true, Round: not null })
        {
            Reset(!shopOpen ? "shop-closed" : economy is null ? "hud-unavailable"
                : !economy.IsPreparation ? "not-preparation" : "round-unavailable");
            return [];
        }
        if (_afterEnemyPanel)
        {
            // A paid refresh can blank every slot while the panel covers the shop.
            if (cards.Count == 0) Reset("no-visible-offer-after-enemy-panel");
            _afterEnemyPanel = false;
        }
        // A transient animation can obscure the mode label and multiple card names.
        // Keep the last verified shelf until an identifiable scene is observed again.
        if (economy.Mode is null && _before?.Mode is not null && economy.Round == _before.Round) return [];
        var (width, height) = ImageBytes.GetDimensions(image);
        bool sameScene = _before is not null && _before.Round == economy.Round && _before.Mode == economy.Mode
            && _width == width && _height == height;
        // A replacement at any position is a refresh, even when it costs the same one coin as an operator.
        bool sameCards = sameScene && SameRemainingCards(cards);
        var missing = sameCards ? _cards.Where(p => !cards.Any(c => SameSlot(c, p))).ToList() : [];
        // Selecting the first card can replace its name before the second baseline read.
        // The unchanged balance, remaining named slots and the button at that slot verify the offer.
        if (_stable == 1 && cards.Count > 0 && missing.Count == 1 && freshFunds &&
            economy.Money is int balance && _lastFundsRead == balance &&
            HasPurchaseButton(BmpPixels.TryRead(ImageBytes.ToBmp(image))!, missing[0]))
        {
            cards = _cards;
            missing.Clear();
        }
        var relevantMissing = missing.Any(c => targets.Contains(c.Name));
        if (!sameCards || missing.Count > 0 && !relevantMissing)
        {
            _confirmedFunds = _lastFundsRead = null;
            _fundsBeforeCardChange = null;
            _fundsChangeGrace = 0;
        }
        else if (missing.Count == 0)
        {
            if (freshFunds && _stable >= 2 && _fundsBeforeCardChange is null && _confirmedFunds > economy.Money)
            {
                // Payment can precede the name disappearing. Keep its baseline for two scans.
                AppLogger.Info("ShopPurchase", $"Payment baseline retained: money={_confirmedFunds}->{economy.Money}, stable={_stable}");
                _fundsBeforeCardChange = _confirmedFunds;
                _fundsChangeGrace = 2;
            }
            else if (_fundsChangeGrace > 0 && --_fundsChangeGrace == 0)
            {
                AppLogger.Info("ShopPurchase", $"Payment baseline expired: money={_fundsBeforeCardChange}->{economy.Money}");
                _fundsBeforeCardChange = null;
            }
        }
        bool firstPurchase = _refreshFrame is not null && _stable == 1 && missing.Count == 1 && cards.Count >= 2 &&
            relevantMissing && freshFunds && missing[0].Price > 0 && _lastFundsRead - economy.Money == missing[0].Price;
        var beforeMoney = firstPurchase ? _lastFundsRead : _fundsBeforeCardChange ?? _confirmedFunds;
        var empty = false;
        if (sameCards && _stable >= 2 && relevantMissing && freshFunds && _candidateFundsReads < CandidateFundsReadLimit)
            _candidateFundsReads++;
        if (sameCards && (_stable >= 2 || firstPurchase) && relevantMissing && beforeMoney > economy.Money)
        {
            using var stream = new MemoryStream(image);
            using var bitmap = new Bitmap(stream);
            empty = missing.All(c => IsEmpty(bitmap, c));
        }
        if (missing.Count > 0)
        {
            var reason = !relevantMissing ? "not-in-catalog" : _stable < 2 && !firstPurchase ? "baseline-not-stable"
                : !(beforeMoney > economy.Money) ? "funds-not-decreased" : !empty ? "slot-not-empty" : "awaiting-second-empty-frame";
            var diagnostic = $"Candidate: reason={reason}, stable={_stable}, money={beforeMoney}->{economy.Money}, round={economy.Round}, missing=[{string.Join(", ", missing.Select(c => $"{c.Name}@{c.OcrRegion.X:F3}"))}]";
            if (diagnostic != _lastCandidateDiagnostic)
            {
                AppLogger.Info("ShopPurchase", diagnostic);
                _lastCandidateDiagnostic = diagnostic;
            }
        }
        else _lastCandidateDiagnostic = null;
        if (empty)
        {
            _purchasePending = true;
            if (!freshFunds) return [];
            var pending = string.Join('|', missing.Select(c => $"{c.Name}@{c.OcrRegion.X:F3}"));
            _emptyFrames = pending == _pending && _pendingMoney == economy.Money ? _emptyFrames + 1 : 1;
            _pending = pending;
            _pendingMoney = economy.Money;
            if (_emptyFrames < 2) return [];
            var purchased = missing.Where((card, index) => targets.Contains(card.Name) &&
                !missing.Take(index).Any(prior => SameSlot(prior, card))).Select(card => card.Name).ToList();
            AppLogger.Info("ShopPurchase", $"Confirmed [{string.Join(", ", purchased)}], money={beforeMoney}->{economy.Money}");
            ConfirmedOfferAt = _lastOffer;
            _lastOffer = capturedAt ?? DateTimeOffset.UtcNow;
            _refreshFrame = null;
            _stable = Math.Max(_stable, 2);
            _cards = cards.ToList();
            _before = economy;
            _confirmedFunds = _lastFundsRead = economy.Money;
            _emptyFrames = 0;
            _pending = "";
            _purchasePending = false;
            _candidateFundsReads = 0;
            _fundsBeforeCardChange = null;
            _fundsChangeGrace = 0;
            _lastCandidateDiagnostic = null;
            return purchased;
        }
        if (sameCards && _stable >= 2 && relevantMissing)
        {
            // Keep the named card until empty pixels and funds agree; an animation or one bad funds
            // reading must not erase the only record of what occupied the now-empty position.
            _emptyFrames = 0;
            _pending = "";
            _purchasePending = true;
            return [];
        }
        if (!sameCards)
            AppLogger.Info("ShopPurchase", $"Baseline established: sameScene={sameScene}, round={economy.Round}, mode={economy.Mode}, size={width}x{height}, money={economy.Money}, previous=[{string.Join(", ", _cards.Select(c => c.Name))}], cards=[{string.Join(", ", cards.Select(c => $"{c.Name}@{c.OcrRegion.X:F3}"))}]");
        else if (_stable == 1 && missing.Count == 0)
            AppLogger.Info("ShopPurchase", $"Baseline stable: money={economy.Money}, cards={cards.Count}");
        _refreshFrame = !sameCards && sameScene && freshFunds && economy.Money.HasValue && cards.Count >= 3 ? image : null;
        _stable = sameCards && missing.Count == 0 ? _stable + 1 : 1;
        if (freshFunds)
        {
            if (economy.Money is { } amount && _lastFundsRead == amount) _confirmedFunds = amount;
            _lastFundsRead = economy.Money;
        }
        _lastOffer = capturedAt ?? DateTimeOffset.UtcNow;
        _cards = cards.ToList();
        _before = economy;
        _width = width; _height = height;
        _emptyFrames = 0; _pending = "";
        _purchasePending = false;
        _candidateFundsReads = 0;
        return [];
    }

    internal static bool HasPurchaseButton(BmpPixels frame, ShopItem card)
    {
        int cyan = 0;
        for (int row = 0; row < 7; row++)
            for (int col = 0; col < 6; col++)
            {
                int x = (int)(card.OcrRegion.X * frame.Width + col * frame.Height * .009);
                int y = (int)((.865 + row * .014) * frame.Height);
                if (x >= frame.Width) continue;
                int rgb = frame.Color(x, y);
                int r=rgb>>16, g=(rgb>>8)&255, b=rgb&255;
                if (g>140 && b>135 && g-r>55 && b-r>55) cyan++;
            }
        return cyan >= 16;
    }

    internal static bool SameNamedSlot(ShopItem a, ShopItem b) => a.Name == b.Name && SameSlot(a, b);

    private static bool SameSlot(ShopItem a, ShopItem b)
        => Math.Abs(a.OcrRegion.Y - b.OcrRegion.Y) < (a.Name == b.Name ? .035 : .015) &&
            (Math.Abs(a.OcrRegion.X - b.OcrRegion.X) < .018 ||
             a.Name == b.Name && Math.Abs(a.OcrRegion.X - b.OcrRegion.X) < .05 &&
             Math.Min(a.OcrRegion.X + a.OcrRegion.Width, b.OcrRegion.X + b.OcrRegion.Width)
                - Math.Max(a.OcrRegion.X, b.OcrRegion.X) > .5 * Math.Min(a.OcrRegion.Width, b.OcrRegion.Width));

    // Empty merchandise slots reveal the dark scene behind the shop. Check the card body and its bottom
    // edge as well as the name: losing OCR text on a frozen/dark card must never count as a purchase.
    public static bool IsEmpty(Bitmap image, ShopItem card)
        => IsEmpty(card, image.Width, image.Height, image.GetPixel);

    internal static bool IsEmpty(BmpPixels image, ShopItem card)
        => IsEmpty(card, image.Width, image.Height, (x, y) => Color.FromArgb(image.Color(x,y)));

    private static bool IsEmpty(ShopItem card, int width, int height, Func<int,int,Color> read)
    {
        var left = (int)(card.OcrRegion.X * width);
        var right = Math.Min(width - 1, left + (int)(height * .075));
        var top = (int)(height * .84);
        int dark = 0, samples = 0;
        double strongestEdge = 0;
        for (int y = top; y < height - 1; y += Math.Max(1, height / 540))
        {
            int edge = 0, row = 0;
            for (int x = left; x < right; x += Math.Max(1, height / 540))
            {
                var color = read(x, y);
                var below = read(x, Math.Min(height - 1, y + Math.Max(1, height / 360)));
                if (color.R < 85 && color.G < 100 && color.B < 145) dark++;
                if (Math.Abs(color.R - below.R) + Math.Abs(color.G - below.G) + Math.Abs(color.B - below.B) > 90) edge++;
                samples++; row++;
            }
            if (row > 0) strongestEdge = Math.Max(strongestEdge, edge / (double)row);
        }
        // Empty-slot emblems create short edges; a card border crosses most of the sampled width.
        return samples > 0 && dark / (double)samples > .92 && strongestEdge < .5;
    }
}
