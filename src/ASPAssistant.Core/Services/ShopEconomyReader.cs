// Added for ASP Assistant Arknights on 2026-11-13; see NOTICE.
// The reading regions follow StrongholdAssistant's EconomyReader (LBEILC/StrongholdAssistant, Apache-2.0).
using System.Text.RegularExpressions;
using System.Text;

namespace ASPAssistant.Core.Services;

/// <summary>
/// What the shop phase shows besides the cards: round, funds, the upgrade price and the mode name.
/// Any part that was not readable is null.
/// </summary>
public sealed record ShopEconomy(int? Round, int? Money, int? UpgradeCost, bool UpgradeMaxed, string? Mode, bool IsPreparation)
{
    public bool HasAny => Round is not null || Money is not null || UpgradeCost is not null || UpgradeMaxed;
}

/// <summary>
/// Reads the shop HUD from the same frame as the shop cards.
/// <list type="bullet">
/// <item>Top bar: 「回合 N」 left of the phase title (休息一下 / 休整 during preparation); the mode name
/// (规程等级 N / …模拟) sits at the top left.</item>
/// <item>Bottom: the upgrade card's price badge above 升级, and the funds badge at the right edge on the same row.</item>
/// </list>
/// The bottom HUD is anchored to the bottom edge and grows with the in-game UI size (90–100), so its scale is
/// measured from where 升级 sits instead of assumed from the frame height.
/// </summary>
public sealed class ShopEconomyReader(IOcrEngine ocr)
{
    private double _hudScale;
    private (int Center, int Width, int Height, double Scale, byte[] Pixels, int Cost)? _upgradeBadge;
    // 1080p at UI size 90: 升级 text top is 101 px above the bottom edge; badges are 38 px tall, 273 px up.
    private const double ButtonFromBottom = 101;
    private const double BadgeFromBottom = 273;
    private const double BadgeHeight = 38;

    private static readonly Regex RoundInline = new(@"回合\D{0,2}(\d{1,2})$", RegexOptions.Compiled);
    private static readonly Regex ModeName = new(@"^[^\p{L}\p{N}]*(规程等级\d{1,2}|入门协议|[一-龥]{2}模拟)$", RegexOptions.Compiled);

    public async Task<ShopEconomy> ReadAsync(byte[] png, int width, int height)
    {
        double s = Math.Min(width / 1920.0, height / 1080.0);
        var (round, mode, preparation) = await ReadTopAsync(png, width, height, s);

        // Upgrade card: find 升级 / 已满级 in the lower-left band, then derive the HUD scale from its height above the bottom.
        var bottom = await ocr.RecognizeRegionAsync(png, 0, (int)(height - 160 * s * 1.15), (int)(width * 0.6), (int)(160 * s * 1.15));
        var button = bottom.FirstOrDefault(t => t.Text.Contains("满级", StringComparison.Ordinal))
                     ?? bottom.FirstOrDefault(t => t.Text.Contains("升级", StringComparison.Ordinal))
                     ?? await ProbeButtonAsync(png, width, s);
        // UI size 90 → 1.0, 100 → 10/9. OCR boxes wobble by a few pixels, so small deviations count as 90.
        double ratio = button is null ? 1 : (height - button.Box.Y) / (ButtonFromBottom * s);
        double k = s * (ratio < 1.04 ? 1 : Math.Min(ratio, 10.0 / 9));
        _hudScale = k;
        bool maxed = button?.Text.Contains("满级", StringComparison.Ordinal) == true;

        int? cost = null;
        if (button is not null && !maxed)
        {
            int center = button.Box.X + button.Box.W / 2;
            // Only the upgrade price is reusable. Compare all five OCR crop areas;
            // the balance is still read from this observation on every funds check.
            var badge = BmpPixels.TryRead(png)?.RegionDigest(
                center - (int)Math.Ceiling(32 * k), height - (int)Math.Ceiling((BadgeFromBottom + 4) * k),
                center + (int)Math.Ceiling(38 * k), height - (int)Math.Floor((BadgeFromBottom - BadgeHeight - 4) * k));
            if (badge != null && _upgradeBadge is { } last && last.Center == center && last.Scale == k
                && last.Width == width && last.Height == height && badge.AsSpan().SequenceEqual(last.Pixels))
                cost = last.Cost;
            else
            {
                cost = await ReadBadgeAsync(png, center - (int)(26 * k), height, k, (int)(52 * k));
                if (badge != null && cost is int price) _upgradeBadge = (center, width, height, k, badge, price);
            }
        }
        // Skip the coin icon at the badge's left; it pulls the recogniser towards letters.
        var money = preparation ? await ReadMoneyAsync(png, width, height)
            : await ReadBadgeAsync(png, width - (int)(120 * k), height, k, (int)(48 * k));
        return new(round, money, cost, maxed, mode, preparation);
    }

    // The gold funds badge can be located independently of the upgrade label, whose
    // animated OCR box sometimes puts the funds crop below the top of the digit.
    private static System.Drawing.Rectangle MoneyDigits(BmpPixels pixels)
    {
        double scale = Math.Min(pixels.Width / 1920d, pixels.Height / 1080d);
        int left = pixels.Width - (int)(245 * scale), right = pixels.Width - (int)(25 * scale);
        var edges = new List<(int X, int End, int Y)>();
        for (int y = pixels.Height - (int)(315 * scale); y < pixels.Height - (int)(205 * scale); y++)
        {
            int start = left;
            for (int x = left; x <= right; x++)
            {
                int color = pixels.Color(x, y);
                int red = color >> 16, green = color >> 8 & 255, blue = color & 255;
                if (red > 130 && green > 82 && red - blue > 68 && green - blue > 45) continue;
                int span = x - start, end = x - 1;
                if (span >= 65 * scale && span <= 140 * scale)
                {
                    foreach (var edge in edges)
                    {
                        int height = y - edge.Y;
                        if (height >= 29 * scale && height <= 50 * scale &&
                            Math.Abs(edge.X - start) < 8 * scale && Math.Abs(edge.End - end) < 8 * scale)
                            return System.Drawing.Rectangle.FromLTRB(edge.X + (int)Math.Round(span * .42),
                                edge.Y + (int)Math.Round(height * .07), edge.End + 1, y - (int)Math.Round(height * .04));
                    }
                    edges.Add((start, end, y));
                }
                start = x + 1;
            }
        }
        return System.Drawing.Rectangle.Empty;
    }

    private (int Width, int Height, System.Drawing.Rectangle Rect) _sampleBadge;

    internal byte[] SampleMoneyPixels(BmpPixels frame)
    {
        var rect = MoneyDigits(frame);
        var last = _sampleBadge;
        if (last.Width == frame.Width && last.Height == frame.Height && (rect.IsEmpty ||
            Math.Abs(last.Rect.X - rect.X) <= 4 && Math.Abs(last.Rect.Y - rect.Y) <= 2))
            rect = last.Rect;
        if (rect.IsEmpty) return [];
        _sampleBadge = (frame.Width, frame.Height, rect);
        var sample = new byte[24 * 12];
        for (int row = 0; row < 12; row++)
            for (int col = 0; col < 24; col++)
                sample[row * 24 + col] = (byte)frame.Gray(rect.X + (2 * col + 1) * rect.Width / 48,
                    rect.Y + (2 * row + 1) * rect.Height / 24);
        return sample;
    }

    private (int, int) _lastMoneySize;
    private System.Drawing.Rectangle _lastMoneyDigits;

    /// <summary>Rechecks only funds while the same shop layout remains open.</summary>
    public async Task<int?> ReadMoneyAsync(byte[] png, int width, int height)
    {
        if (BmpPixels.TryRead(png) is { } pixels)
        {
            var digits = MoneyDigits(pixels);
            if (digits.IsEmpty && _lastMoneySize == (width, height)) digits = _lastMoneyDigits;
            if (!digits.IsEmpty)
            {
                _lastMoneySize = (width, height);
                _lastMoneyDigits = digits;
                int left = digits.Right, right = digits.Left, top = digits.Bottom, bottom = digits.Top;
                for (int row = digits.Top; row < digits.Bottom; row++)
                    for (int col = digits.Left; col < digits.Right; col++)
                    {
                        int color = pixels.Color(col,row), red = color >> 16, green = (color >> 8) & 255, blue = color & 255;
                        if (red < 145 || green < 95 || red-blue < 90 || green-blue < 55) continue;
                        left = Math.Min(left,col); right = Math.Max(right,col);
                        top = Math.Min(top,row); bottom = Math.Max(bottom,row);
                    }
                if (bottom-top >= digits.Height * .4)
                {
                    int pad = (bottom-top+1)/2;
                    var ink = System.Drawing.Rectangle.Intersect(digits, System.Drawing.Rectangle.FromLTRB(left-pad,
                        top-pad/3, right+pad+1, bottom+pad/3+1));
                    if (Number(await ocr.RecognizeLineAsync(png, ink.X, ink.Y, ink.Width, ink.Height)) is int value)
                        return value;
                }
                if (Number(await ocr.RecognizeLineAsync(png, digits.X, digits.Y, digits.Width, digits.Height)) is int balance)
                    return balance;
            }
        }
        double scale = _hudScale;
        int x = width - (int)(120 * scale), w = (int)(48 * scale);
        // Leave room for the left stroke of 0 and the leading 1 of 15 at UI 90 in 16:10.
        int y = height - (int)((BadgeFromBottom - 1) * scale);
        var inner = await ocr.RecognizeLineAsync(png, x-(int)(7*scale), y, w+(int)(7*scale), (int)((BadgeHeight - 6) * scale));
        return Number(inner) ?? await ReadBadgeAsync(png, x, height, scale, w);
    }

    internal async Task<ShopEconomy> ReadPreparationAsync(byte[] png, int width, int height)
    {
        double scale = Math.Min(width / 1920d, height / 1080d);
        var (round, mode, preparation) = await ReadTopAsync(png, width, height, scale);
        if (preparation && round is null)
        {
            // The compact round badge can be omitted by the wide HUD text detector.
            var line = await ocr.RecognizeLineAsync(png, width / 2 - (int)(211 * scale), (int)(33 * scale),
                (int)(36 * scale), (int)(24 * scale));
            if (Number(line) is > 0 and <= 15 and var inferred) round = inferred;
        }
        return new(round, null, null, false, mode, preparation);
    }

    /// <summary>Fresh round, preparation phase and funds for a pending purchase; reuse the measured HUD scale.</summary>
    public async Task<ShopEconomy> ReadPurchaseAsync(byte[] png, int width, int height, ShopEconomy previous)
    {
        var (round, mode, preparation) = await ReadTopAsync(png, width, height, Math.Min(width / 1920.0, height / 1080.0));
        var money = preparation && round == previous.Round && mode == previous.Mode
            ? await ReadMoneyAsync(png, width, height) : null;
        return previous with { Round = round, Mode = mode, IsPreparation = preparation, Money = money };
    }

    /// <summary>
    /// A number inside a hexagonal badge. Single stylised digits are easy to misread (7 → 2 / Z, 0 → D / 8) and
    /// the badge outline reads as a letter when the crop touches it, so a few slightly shifted crops vote:
    /// the number at least two agree on wins, otherwise nothing is reported. A wrong price is worse than none.
    /// </summary>
    private async Task<int?> ReadBadgeAsync(byte[] png, int x, int height, double k, int w)
    {
        var votes = new List<int>();
        foreach (var (dx, dy, grow) in new[] { (0, 0, 0), (0, 3, 6), (0, -3, 0), (-3, 0, 6), (3, 0, 0) })
        {
            int y = (int)(height - (BadgeFromBottom + dy) * k);
            if (Number(await ocr.RecognizeLineAsync(png, x + (int)(dx * k), y, w + (int)(grow * k), (int)(BadgeHeight * k))) is { } n)
            {
                votes.Add(n);
                if (votes.Count(v => v == n) == 3) return n; // Three votes cannot be outvoted by the remaining two crops.
            }
        }
        var best = votes.GroupBy(v => v).OrderByDescending(g => g.Count()).FirstOrDefault();
        return best is not null && best.Count() >= 2 && best.Count() * 2 > votes.Count ? best.Key : null;
    }

    /// <summary>
    /// The greyed 升级 of some skins is missed by text detection; read the few spots the card can occupy
    /// (left of 6 / 5 / 4 / 3 shop slots, 218 px apart at UI size 90), as <see cref="TerminalLevelReader"/> does.
    /// </summary>
    private async Task<OcrTextResult?> ProbeButtonAsync(byte[] png, int width, double s)
    {
        for (int slots = 6; slots >= 3; slots--)
        {
            int c = (int)Math.Round(width / 2.0 + (-685 + 218 * (6 - slots)) * s);
            int x = c - (int)(55 * s), y = (int)(962 * s);
            if (x < 0) continue;
            var line = await ocr.RecognizeLineAsync(png, x, y, (int)(110 * s), (int)(40 * s));
            if (line.FirstOrDefault(t => t.Text.Contains("升级", StringComparison.Ordinal) || t.Text.Contains("满级", StringComparison.Ordinal)) is { } hit)
                return hit with { Box = (x, y + (int)(17 * s), (int)(110 * s), (int)(35 * s)) };
        }
        return null;
    }

    private async Task<(int? Round, string? Mode, bool Preparation)> ReadTopAsync(byte[] png, int width, int height, double s)
    {
        var top = await ocr.RecognizeRegionAsync(png, 0, 0, (int)(width * 0.6), (int)(height * 0.09));
        string? mode = top.Select(t => ModeName.Match(t.Text.Trim())).FirstOrDefault(m => m.Success)?.Groups[1].Value;
        bool preparation = top.Any(t => t.Text.Contains("休息", StringComparison.Ordinal) || t.Text.Contains("休整", StringComparison.Ordinal));

        int? round = null;
        foreach (var t in top)
            if (RoundInline.Match(t.Text.Trim()) is { Success: true } m) round = int.Parse(m.Groups[1].Value);
        var label = top.FirstOrDefault(t => t.Text.Trim() == "回合");
        if (round is null && label is not null)
        {
            // The number sits in its own box to the right; read that box alone (detection often drops it).
            var line = await ocr.RecognizeLineAsync(png, label.Box.X + label.Box.W + (int)(5 * s), label.Box.Y - (int)(2 * s),
                (int)(52 * s * 1.15), label.Box.H + (int)(4 * s));
            round = Number(line);
            round ??= Number(top.Where(t => t.Box.X > label.Box.X + label.Box.W && t.Box.X < label.Box.X + label.Box.W + 80 * s
                && Math.Abs(t.Box.Y - label.Box.Y) < 20 * s).OrderBy(t => t.Box.X).Take(1).ToList());
        }
        if (!preparation && round is > 0 and < 100 && mode is not null)
        {
            // During co-op the wide HUD detector can omit the text beside the skull.
            var phase = await ocr.RecognizeLineAsync(png, width / 2 - (int)Math.Round(27*s), (int)Math.Round(27*s),
                (int)Math.Round(122*s), (int)Math.Round(36*s));
            preparation = phase.Any(t => t.Text.Contains("休息", StringComparison.Ordinal) || t.Text.Contains("休整", StringComparison.Ordinal));
        }
        return (round is > 0 and < 100 ? round : null, mode, preparation);
    }

    /// <summary>A 1–3 digit number made of the line's text; common OCR slips (O→0, l/I→1) are fixed.</summary>
    public static int? Number(IReadOnlyList<OcrTextResult> line)
    {
        var text = string.Concat(line.Select(t => t.Text)).Trim().Normalize(NormalizationForm.FormKC)
            .Replace('O', '0').Replace('o', '0').Replace('l', '1').Replace('I', '1').Replace('|', '1');
        return Regex.IsMatch(text, @"^[0-9]{1,3}$") ? int.Parse(text) : null;
    }
}
