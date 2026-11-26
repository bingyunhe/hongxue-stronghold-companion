using System.Drawing;
using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Services;

public enum CustomRosterPage { Catalogue, Choosing, Prepared }
public sealed record CustomRosterReading(CustomRosterPage Page, List<CustomRosterEntry> Entries, List<(int Tier, int Slot)> Unread)
{
    public bool ConfirmedEmpty { get; init; }
}

public sealed class CustomRosterReader(IOcrEngine ocr, IReadOnlyList<Operator> candidates)
{
    // Card locations are expressed in the 1280 × 720 game viewport. Emulator title bars sit above it.
    private sealed class Frame(BmpPixels pixels)
    {
        private readonly double scale = Math.Min(pixels.Width / 1280d, pixels.Height / 720d);
        private int X(int x) => (int)Math.Round((pixels.Width - 1280 * scale) / 2 + x * scale);
        private int Y(int y) => (int)Math.Round(pixels.Height - (720 - y) * scale);
        public Rectangle Box(int x, int y, int w, int h) => Rectangle.FromLTRB(X(x), Y(y), X(x + w), Y(y + h));
        public int Color(int x,int y) => pixels.Color(X(x), Y(y));

        public byte[] Icon(int x, int y, int side)
        {
            var bytes = new byte[12 * 12 * 3];
            for (int cy = 0; cy < 12; cy++)
                for (int cx = 0; cx < 12; cx++)
                {
                    var r = Box(x + cx * side / 12, y + cy * side / 12,
                        (cx + 1) * side / 12 - cx * side / 12, (cy + 1) * side / 12 - cy * side / 12);
                    int red = 0, green = 0, blue = 0, count = 0;
                    for (int py = r.Top; py < Math.Max(r.Top + 1, r.Bottom); py++)
                        for (int px = r.Left; px < Math.Max(r.Left + 1, r.Right); px++)
                        {
                            int color = pixels.Color(px, py);
                            red += color >> 16; green += color >> 8 & 255; blue += color & 255; count++;
                        }
                    int i = (cy * 12 + cx) * 3;
                    bytes[i] = (byte)(red / count); bytes[i+1] = (byte)(green / count); bytes[i+2] = (byte)(blue / count);
                }
            return bytes;
        }
    }

    public static bool MayBeOpening(BmpPixels pixels)
    {
        var f = new Frame(pixels);
        for (int x = 1010; x <= 1168; x += 158)
        {
            int c = f.Color(x, 631), r = c >> 16, g = c >> 8 & 255, b = c & 255;
            if (!(g > 120 && g > r + 65 && b > r + 40) && !(r > 145 && Math.Abs(r - g) < 14 && Math.Abs(r - b) < 14)) return false;
        }
        return true;
    }

    public static int OpeningKey(BmpPixels pixels)
    {
        var f = new Frame(pixels);
        int c = f.Color(1010,631);
        var hash = new HashCode();
        bool choosing = (c >> 8 & 255) - (c >> 16) > 60;
        hash.Add(choosing);
        for (int y = 30; y <= 50; y += 4)
            for (int x = 631; x < 793; x += 5)
                hash.Add((f.Color(x,y) >> 8 & 255) > 160);
        if (choosing) return hash.ToHashCode();
        // The names on the ready panel are stationary even while the timer or player card animates.
        for (int row = 0; row < 2; row++)
            for (int y = 283; y <= 297; y += 3)
                for (int x = 981; x < 1230; x += 4)
                    hash.Add((f.Color(x, y + 256 * row) >> 8 & 255) > 155);
        return hash.ToHashCode();
    }

    public async Task<CustomRosterReading?> ReadAsync(byte[] image, BmpPixels pixels, bool opening)
    {
        var f = new Frame(pixels);
        async Task<string> Text(Rectangle box, bool line = false)
        {
            var words = line ? await ocr.RecognizeLineAsync(image, box.X, box.Y, box.Width, box.Height)
                : await ocr.RecognizeRegionAsync(image, box.X, box.Y, box.Width, box.Height);
            return string.Concat(words.Select(w => w.Text)).Replace(" ", "");
        }
        var header = await Text(opening ? f.Box(545, 17, 290, 53) : f.Box(541, 79, 250, 54));
        if (!header.Contains("自选编队")) return null;
        var page = CustomRosterPage.Catalogue;
        if (opening)
        {
            int c = f.Color(1010,631);
            if ((c >> 8 & 255) - (c >> 16) > 60) return new(CustomRosterPage.Choosing, [], []);
            var message = await Text(f.Box(663,380,275,64));
            if (message.Contains("未选择")) return new(CustomRosterPage.Prepared, [], []) { ConfirmedEmpty = true };
            if (!message.Contains("已选择")) return null;
            page = CustomRosterPage.Prepared;
        }
        var seen = new List<CustomRosterEntry>();
        var unread = new List<(int, int)>();
        for (int row = 0; row < 2; row++)
            for (int col = 0; col < (opening ? 2 : 6); col++)
            {
                int x = opening ? 1024 + 131 * col : 339 + 143 * col;
                int y = opening ? 227 + 256 * row : 295 + 270 * row;
                var icon = f.Icon(x, y, opening ? 29 : 32);
                // A blank slot has no illuminated skill glyph. A low-contrast occupied slot still has a name.
                var box = opening ? f.Box(x-47, 278+256*row, 126, 27) : f.Box(x-48, 351+270*row, 130, 29);
                if (icon.Max() < 85 && !HasNamePixels(pixels, box)) continue;
                string raw = await Text(box, true);
                var ranked = candidates.Select(op => (Op: op, Distance: EditDistance(raw, op.Name)))
                    .OrderBy(p => p.Distance).Take(2).ToArray();
                var best = ranked[0];
                bool clearName = best.Distance == 0 || best.Op.Name.Length >= 3 && best.Distance == 1 && ranked[1].Distance > 1;
                var matches = (clearName ? best.Op.SkillOptions.Select(s => (Op: best.Op, Skill:s))
                    : candidates.SelectMany(op => op.SkillOptions.Select(s => (Op: op, Skill:s))))
                    .Select(p => (p.Op, p.Skill, Error: IconError(icon, p.Skill.Signature))).OrderBy(p => p.Error).Take(2).ToArray();
                bool clearIcon = matches.Length > 0 && matches[0].Error < (clearName ? 51 : 36)
                    && (matches.Length == 1 || matches[1].Error - matches[0].Error > (clearName ? 5 : 9));
                var chosen = clearName ? best.Op : clearIcon && best.Distance > 0 && raw.Length > 0
                    && EditDistance(raw, matches[0].Op.Name) <= Math.Max(2, matches[0].Op.Name.Length/2) ? matches[0].Op : null;
                if (chosen == null || seen.Any(e => e.Name == chosen.Name)) { unread.Add((5+row,col)); continue; }
                seen.Add(new(chosen.Name, 5+row, clearIcon ? matches[0].Skill.Id : null, col));
            }
        return new(page, seen, unread);
    }

    private static bool HasNamePixels(BmpPixels pixels, Rectangle box)
    {
        int bright = 0;
        for (int y = box.Top; y < box.Bottom; y += 2)
            for (int x = box.Left; x < box.Right; x += 2)
                if (pixels.Gray(x,y) > 172 && ++bright >= 12) return true;
        return false;
    }

    private static double IconError(byte[] a, byte[] b)
    {
        int sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }

    private static int EditDistance(string a, string b)
    {
        var d = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 0; i < a.Length; i++)
        {
            int diagonal = d[0]++;
            for (int j = 1; j <= b.Length; j++)
            {
                int old = d[j];
                d[j] = Math.Min(diagonal + (a[i] == b[j-1] ? 0 : 1), Math.Min(d[j] + 1, d[j-1] + 1));
                diagonal = old;
            }
        }
        return d[b.Length];
    }
}
