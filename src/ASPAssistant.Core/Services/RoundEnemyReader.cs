using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ASPAssistant.Core.Data;

namespace ASPAssistant.Core.Services;

// Panel layout and bright-name recognition follow StrongholdAssistant; see NOTICE.
public sealed class RoundEnemyReader(IOcrEngine ocr, GarrisonRoundRouteStore? routes = null, IReadOnlySet<string>? bountyNames = null)
{
    internal readonly record struct Layout(Rectangle Title, Rectangle Names, Point? Button,
        bool HasBountyHeading, bool Scrolled, bool MoreBelow)
    {
        internal bool BottomUnknown { get; init; }
        internal int ThumbTop { get; init; }
        internal int ThumbBottom { get; init; }
    }

    private static Rectangle Area(Size size, int x, int y, int w, int h)
    {
        double scale = Math.Min(size.Width / 1920d, size.Height / 1080d);
        int origin = (size.Width - (int)(1920 * scale)) / 2;
        return new(origin + (int)(x * scale), (int)(y * scale), (int)(w * scale), (int)(h * scale));
    }

    internal static Rectangle ProbeArea(Size size) => Area(size, 1110, 100, 165, 155);

    internal static bool ProbeVisible(Bitmap strip, Size size)
    {
        var heading = InspectHeading(strip, size, ProbeArea(size).Location);
        return heading.Dark && heading.Visible;
    }

    private static (bool Visible, bool Bounty, bool Dark, Rectangle Edge) InspectHeading(Bitmap frame, Size size, Point offset)
    {
        double scale = Math.Min(size.Width / 1920d, size.Height / 1080d);
        var title = Area(size, 1170, 105, 99, 55);
        var edge = Area(size, 1125, 165, 5, 84);
        title.Offset(-offset.X, -offset.Y);
        edge.Offset(-offset.X, -offset.Y);
        int white = 0, gold = 0;
        for (int y = title.Top; y < title.Bottom; y += 2)
        for (int x = title.Left; x < title.Right; x += 2)
        {
            var color = frame.GetPixel(x, y);
            if (color.R >= 172 && color.G >= 172 && color.B >= 172) white++;
            if (Gold(color)) gold++;
        }
        bool HasDarkEdge(Rectangle strip)
        {
            int dark = 0;
            for (int y = strip.Top; y < strip.Bottom; y += 2)
            for (int x = strip.Left; x < strip.Right; x += 2)
            {
                var p = frame.GetPixel(x, y);
                if (p.R + p.G + p.B < 138) dark++;
            }
            return dark >= (strip.Width + 1) / 2 * ((strip.Height + 1) / 2) * .68;
        }
        var titleThreshold = ((title.Width + 1) / 2) * ((title.Height + 1) / 2) * .012;
        bool headingVisible = white >= titleThreshold;
        bool bounty = gold >= titleThreshold;
        bool darkEdge = HasDarkEdge(edge);
        if (!darkEdge)
        {
            // Desktop captures can place the first portrait over the usual edge sample.
            edge.Offset(-(int)(12 * scale), 0);
            darkEdge = HasDarkEdge(edge);
        }
        return (headingVisible || bounty, bounty, darkEdge, edge);
    }

    internal static Layout Inspect(Bitmap frame, bool locateButton)
    {
        double scale = Math.Min(frame.Width / 1920d, frame.Height / 1080d);
        var (headingVisible, bounty, darkEdge, edge) = InspectHeading(frame, frame.Size, Point.Empty);
        var title = Area(frame.Size, 1154, 94, 124, 66);
        var names = Area(frame.Size, 1250, 163, 416, 690);
        int searchBottom = names.Bottom;
        var (thumbTop, thumbBottom) = (0, 0);
        bool compactPanel = false;
        if (darkEdge)
        {
            // Find the bottom along the inner left edge, away from the portraits and text.
            int edgeColumn = edge.Left + edge.Width / 2, outside = 0;
            for (int y = edge.Bottom; y < names.Bottom; y += 2)
            {
                var p = frame.GetPixel(edgeColumn, y);
                outside = p.R + p.G + p.B > 195 ? outside + 1 : 0;
                if (outside < 4) continue;
                names.Height = y - 6 - names.Top;
                // A full-height list can also fit without a thumb. Verify a continuous
                // bottom edge across the name column before treating it as a single page.
                int border = 0;
                for (int n = 0; n < 9; n++)
                {
                    int x = names.Left + n * names.Width / 9;
                    int above = frame.GetPixel(x, names.Bottom - 5).G;
                    int below = frame.GetPixel(x, names.Bottom + 3).G;
                    if (below - above >= 28) border++;
                }
                compactPanel = names.Bottom < 570 * scale || border >= 8;
                break;
            }
            var scrollbar = Area(frame.Size, 1668, 96, 22, 755);
            scrollbar.Height = searchBottom - scrollbar.Top;
            (thumbTop, thumbBottom) = FindScrollThumb(frame, scrollbar, scale);
            if (thumbBottom == 0)
            {
                scrollbar = Area(frame.Size, 1748, 96, 13, 755);
                scrollbar.Height = searchBottom - scrollbar.Top;
                (thumbTop, thumbBottom) = FindScrollThumb(frame, scrollbar, scale);
            }
            if (thumbBottom > 0)
            {
                // A thin horizontal border survives even over a dark battlefield, where the left edge
                // alone can continue well below the panel. Verify it across the text column.
                bool borderFound = false;
                for (int y = thumbBottom + 1; y < searchBottom; y++)
                {
                    int min = 255, max = 0;
                    for (int n = 0; n < 9; n++)
                    {
                        int value = frame.GetPixel(names.Left + n * names.Width / 9, y).G;
                        min = Math.Min(min, value);
                        max = Math.Max(max, value);
                    }
                    bool brightBorder = min >= 67 && max - min <= 24;
                    // At the foot of the track the border can be dim over palace tiles.
                    // Check that it extends to the panel gutter and stands above the row behind it.
                    if (!brightBorder)
                    {
                        if (y - thumbBottom > 20 * scale || min < 32 || max - min > 12 ||
                            Math.Abs(frame.GetPixel(edgeColumn, y).G - min) > 12) continue;
                        int raised = 0;
                        for (int n = 0; n < 9; n++)
                            if (frame.GetPixel(names.Left + n * names.Width / 9, y - 2).G <= min - 20) raised++;
                        if (raised < 8) continue;
                    }
                    if (frame.GetPixel(scrollbar.Left - 2, y - 2).G > min - 15) continue;
                    names.Height = y - names.Top;
                    borderFound = true;
                    break;
                }
                // Beyond the apparent panel edge, grey scenery can resemble a scrollbar.
                if (!borderFound && thumbBottom > names.Bottom)
                {
                    scrollbar.Height = Math.Max(0, names.Bottom - scrollbar.Top);
                    (thumbTop, thumbBottom) = FindScrollThumb(frame, scrollbar, scale);
                }
            }
        }

        var button = locateButton ? FindEnemyButton(frame, Area(frame.Size, 1300, 24, 185, 42), scale) : null;
        bool panel = darkEdge && (headingVisible || thumbBottom > 0);
        return new(panel ? title : Rectangle.Empty, names, button, bounty,
            thumbTop > title.Top + 25 * scale, thumbBottom > 0 && thumbBottom < names.Bottom - 20 * scale)
        { BottomUnknown = thumbBottom == 0 && !compactPanel, ThumbTop = thumbTop, ThumbBottom = thumbBottom };
    }

    private static Point? FindEnemyButton(Bitmap frame, Rectangle strip, double scale)
    {
        var gold = new byte[strip.Width * strip.Height];
        for (int y = 0; y < strip.Height; y++)
        for (int x = 0; x < strip.Width; x++)
        {
            var pixel = frame.GetPixel(strip.X + x, strip.Y + y);
            if (pixel.G >= .62 * pixel.R && pixel.G <= pixel.R + 5 && pixel.B < .75 * pixel.G)
                gold[y * strip.Width + x] = pixel.R;
        }

        // The glowing border can span 90 pixels. Match the three glyphs inside it.
        foreach (byte brightness in (ReadOnlySpan<byte>)[165, 138])
        {
            var components = FindGoldComponents(gold, strip.Size, brightness, (int)Math.Ceiling(15 * scale * scale));
            foreach (var lens in components)
            {
                if (!Between(lens.Width, 15, 30) || !Between(lens.Height, 15, 37)) continue;
                foreach (var first in components)
                {
                    int y = first.Top + first.Height / 2;
                    if (!IsArrow(first) || !Between(first.Left - lens.Left, 22, 39)
                        || !Between(y - (lens.Top + lens.Height / 2), -5, 10)) continue;
                    foreach (var second in components)
                    {
                        if (IsArrow(second) && Between(second.Left - first.Left, 11, 20)
                            && Math.Abs(second.Top - first.Top) <= 3 * scale
                            && Math.Abs(second.Width - first.Width) <= 4 * scale
                            && Math.Abs(second.Height - first.Height) <= 4 * scale)
                            return new Point(strip.X + (lens.Left + second.Right) / 2, strip.Y + y);
                    }
                }
            }
        }
        return null;

        bool IsArrow(Rectangle component) => Between(component.Width, 6, 16) && Between(component.Height, 7, 17);
        bool Between(int pixels, int min, int max) => pixels >= Math.Floor(min * scale) && pixels <= Math.Ceiling(max * scale);
    }

    private static List<Rectangle> FindGoldComponents(byte[] gold, Size size, byte brightness, int minPixels)
    {
        var components = new List<Rectangle>();
        var visited = new bool[gold.Length];
        var pending = new Queue<Point>();
        for (int y = 0; y < size.Height; y++)
        for (int x = 0; x < size.Width; x++)
        {
            if (!Visit(x, y)) continue;
            int left = x, top = y, right = x, bottom = y, count = 0;
            while (pending.TryDequeue(out var p))
            {
                left = Math.Min(left, p.X);
                right = Math.Max(right, p.X);
                top = Math.Min(top, p.Y);
                bottom = Math.Max(bottom, p.Y);
                count++;
                Visit(p.X - 1, p.Y);
                Visit(p.X + 1, p.Y);
                Visit(p.X, p.Y - 1);
                Visit(p.X, p.Y + 1);
            }
            if (count >= minPixels) components.Add(Rectangle.FromLTRB(left, top, right + 1, bottom + 1));
        }
        return components;

        bool Visit(int x, int y)
        {
            if ((uint)x >= (uint)size.Width || (uint)y >= (uint)size.Height) return false;
            int index = y * size.Width + x;
            if (visited[index] || gold[index] < brightness) return false;
            visited[index] = true;
            pending.Enqueue(new Point(x, y));
            return true;
        }
    }

    private static (int Top, int Bottom) FindScrollThumb(Bitmap frame, Rectangle strip, double scale)
    {
        (int Top, int Bottom) longest = default;
        for (int x = strip.Left; x < strip.Right; x++)
        {
            int start = strip.Top;
            for (int y = strip.Top; y < strip.Bottom; y++)
            {
                var p = frame.GetPixel(x, y);
                if (p.R is >= 106 and <= 142 && Math.Abs(p.R - p.G) <= 4 && Math.Abs(p.R - p.B) <= 4)
                {
                    if (y - start > longest.Bottom - longest.Top) longest = (start, y);
                }
                else start = y + 1;
            }
        }
        return longest.Bottom - longest.Top >= 35 * scale ? longest : default;
    }

    internal async Task<RoundEnemyReadResult?> ReadAsync(byte[] screenshot, Bitmap frame, Layout layout,
        RoundEnemyReadResult? previousPage = null, string? mode = null, int round = 0, Func<bool>? stillRequested = null)
    {
        if (layout.Title.IsEmpty || stillRequested?.Invoke() == false) return null;
        var title = layout.Title;
        title.Height = layout.Names.Bottom - title.Top;
        var headings = await ocr.RecognizeRegionAsync(screenshot, title.X, title.Y, title.Width, title.Height);
        if (stillRequested?.Invoke() == false) return null;
        var sections = headings.Select(t => (Name: Heading(t.Text), t.Box))
            .Where(t => t.Name is "敌人" or "悬赏" or "领袖").OrderBy(t => t.Box.Y).ToArray();
        string? inherited = layout.Scrolled ? previousPage?.LastSection : null;
        bool clippedBounty = layout.Scrolled && layout.HasBountyHeading && sections.FirstOrDefault().Name == "敌人" && bountyNames is not null;
        bool continuation = inherited is not null || clippedBounty;
        if (sections.Length == 0 && !continuation) return null;
        // Bounties precede the enemy section. Only its heading at the top of a fresh panel
        // identifies an ordinary roster; a scrolled bounty continuation can also say “敌人”.
        bool singlePage = routes?.IsKnownNonBossRound(mode, round) == true && !layout.HasBountyHeading &&
            !layout.Scrolled && previousPage?.Bounties.Length is not > 0 && sections.Length == 1 &&
            sections[0].Name == "敌人" && sections[0].Box.Y < layout.Title.Bottom;

        double scale = Math.Min(frame.Width / 1920d, frame.Height / 1080d);
        var area = layout.Names;
        // Align with the text column even when the panel uses a larger in-game UI size.
        int textLeft = sections.Length > 0 ? sections[0].Box.X + (int)(74 * scale) : area.X;
        area.Width = area.Right - textLeft;
        area.X = textLeft;
        if (layout.Scrolled)
        {
            var bottom = area.Bottom;
            area.Y = title.Top;
            area.Height = bottom - area.Top;
        }
        using var names = frame.Clone(area, PixelFormat.Format24bppRgb);
        var bands = new List<(int Top, int Bottom)>();
        int firstColumns = (int)(160 * scale);
        var data = names.LockBits(new Rectangle(Point.Empty, names.Size), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[names.Width * 3];
            for (int y = 0; y < names.Height; y++)
            {
                var address = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(address, row, 0, row.Length);
                int count = 0;
                for (int i = 0; i < row.Length; i += 3)
                {
                    bool bright = row[i] >= 190 && row[i + 1] >= 190 && row[i + 2] >= 190;
                    if (bright && i < firstColumns * 3) count++;
                    row[i] = row[i + 1] = row[i + 2] = bright ? (byte)255 : (byte)0;
                }
                Marshal.Copy(row, 0, address, row.Length);
                if (count < 11 * scale) continue;
                if (bands.Count == 0 || y - bands[^1].Bottom > 6 * scale) bands.Add((y, y));
                else bands[^1] = (bands[^1].Top, y);
            }
        }
        finally { names.UnlockBits(data); }
        bands.RemoveAll(b => b.Bottom - b.Top < 12 * scale);
        if (bands.Count == 0) return new([], [], singlePage || !layout.Scrolled && !layout.BottomUnknown,
            singlePage || !layout.MoreBelow && !layout.BottomUnknown, true, null, 0);

        if (stillRequested?.Invoke() == false) return null;
        var candidates = routes?.PossibleEnemies(mode, round);
        // Leader descriptions and the fade at the bottom of the panel lose strokes in the white mask.
        // Read the original name column once; the mask still distinguishes titles from description lines.
        var mixedLeaderPage = sections.Any(s => s.Name == "领袖") && sections.Any(s => s.Name == "敌人") && candidates is { Count: > 0 };
        var words = mixedLeaderPage
            ? (await ocr.RecognizeRegionAsync(screenshot, area.X, area.Y, area.Width, area.Height))
                .Select(w => new OcrTextResult(w.Text, (w.Box.X - area.X, w.Box.Y - area.Y, w.Box.W, w.Box.H))).ToArray()
            : await ocr.RecognizeRegionAsync(ImageBytes.Encode(names), 0, 0, names.Width, names.Height);
        if (mixedLeaderPage)
        {
            var enemyHeading = sections.First(s => s.Name == "敌人").Box;
            foreach (var word in words.Where(w => candidates!.Contains(Normalize(w.Text)) &&
                w.Box.Y + area.Y >= enemyHeading.Y + enemyHeading.H && w.Box.H >= 14 * scale))
            {
                int middle = word.Box.Y + word.Box.H / 2;
                if (!bands.Any(b => middle >= b.Top && middle <= b.Bottom))
                    bands.Add((word.Box.Y, Math.Min(names.Height - 1, word.Box.Y + word.Box.H - 1)));
            }
            bands.Sort();
        }
        var enemies = new List<string>();
        var leaders = new List<string>();
        var bounties = new List<string>();
        bool unread = false, validAfterUnread = false;
        int retries = 0, section = -1;
        foreach (var (top, bottom) in bands)
        {
            if (stillRequested?.Invoke() == false) return null;
            while (section + 1 < sections.Length && sections[section + 1].Box.Y + sections[section + 1].Box.H / 2 < area.Top + top)
                section++;
            if (section < 0 && !continuation) { unread = true; continue; }
            var band = words.Where(t => t.Box.Y + t.Box.H / 2 >= top && t.Box.Y + t.Box.H / 2 <= bottom)
                .OrderBy(t => t.Box.X).ToArray();
            var name = band.Length > 0 && band.All(t => t.Box.H >= 14 * scale && t.Box.H <= 42 * scale)
                ? string.Concat(band.Select(t => t.Text.Trim())) : "";
            name = Normalize(name);
            if (section < 0 && previousPage is not null)
            {
                // A partly repeated page can start in the preceding bounty or leader block.
                if (previousPage.Bounties.Contains(name) && !previousPage.Names.Contains(name)) inherited = "悬赏";
                else if (previousPage.Leaders.Contains(name) && !previousPage.Names.Contains(name)) inherited = "领袖";
                else if (previousPage.Names.Contains(name)) inherited = "敌人";
            }
            var kind = section >= 0 ? sections[section].Name
                : clippedBounty && (inherited is null || bountyNames!.Contains(name)) ? "悬赏" : inherited;
            bool Known(string value) => kind switch
            {
                "悬赏" when bountyNames is not null => bountyNames.Contains(value),
                // This named summon appears as its own panel entry in 管's battle, outside the spawn table.
                "敌人" when candidates is { Count: > 0 } => candidates.Contains(value) ||
                    value == "“余音”" && candidates.Contains("假想敌：管"),
                _ => true
            };
            string PanelName(string value)
            {
                if (kind != "敌人" || candidates is null || candidates.Contains(value)) return value;
                var quoted = "“" + value.Trim('“', '”', '"') + "”";
                return candidates.Contains(quoted) ? quoted : value;
            }
            name = PanelName(name);
            if (name.Length == 0 || !Known(name))
            {
                retries++;
                name = PanelName(Normalize(await ReadNameLineAsync(frame, area, names, top, bottom, scale)));
            }
            if (name.Length == 0 || !Known(name)) { unread = true; continue; }
            validAfterUnread |= unread;
            (kind == "悬赏" ? bounties : kind == "领袖" ? leaders : enemies).Add(name);
        }
        return new(enemies.Distinct(StringComparer.Ordinal).ToArray(), bounties.Distinct(StringComparer.Ordinal).ToArray(),
            singlePage || (!layout.Scrolled || clippedBounty && bounties.Count > 0 &&
                layout.ThumbTop - title.Top < 64 * scale && layout.ThumbBottom - layout.ThumbTop > .8 * title.Height) && !layout.BottomUnknown,
            singlePage || !layout.MoreBelow && !layout.BottomUnknown,
            unread || sections.Any(s => s.Name == "敌人") && enemies.Count == 0,
            sections.Length > 0 ? sections[^1].Name : inherited, retries)
        { BountyLanes = LocateBountyLanes(frame, bounties), Leaders = leaders.Distinct().ToArray(), UnreadTailAnchor = unread && !validAfterUnread && enemies.Count > 0
                ? enemies[^1] : null };
    }

    // Work in the same centered game viewport as the panel reader. Only the enemy staging
    // area is searched: the handbook portrait to its right depicts the same unit.
    internal static Dictionary<string, string> LocateBountyLanes(Bitmap frame, IReadOnlyList<string> bounties)
    {
        var result = new Dictionary<string, string>();
        if (!bounties.Contains("遮天面包机")) return result;
        using var board = new Bitmap(358, 202);
        using (var g = Graphics.FromImage(board))
            g.DrawImage(frame, new Rectangle(0, 0, 358, 202), Area(frame.Size, 0, 0, 1920, 1080), GraphicsUnit.Pixel);
        Point Gate(Rectangle area)
        {
            int count = 0, sx = 0, sy = 0;
            for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
            {
                var c = board.GetPixel(x, y);
                if (c.R < 152 || c.R < c.G * 1.8 || c.R < c.B * 1.8) continue;
                count++; sx += x; sy += y;
            }
            return count < 45 ? Point.Empty : new Point(sx / count, sy / count);
        }
        var upper = Gate(new Rectangle(55, 37, 45, 42));
        var lower = Gate(new Rectangle(28, 117, 56, 48));
        if (upper.IsEmpty || lower.IsEmpty) return result;
        // The saucer has a pale central dome with a turquoise rim above and on both sides.
        bool Rim(Color c) => c.G - c.R > 24 && Math.Abs(c.G - c.B) < 14 && c.G >= 120;
        string? found = null;
        for (int y = 48; y < 193; y++)
        for (int x = 42; x < 202; x++)
        {
            var center = board.GetPixel(x, y);
            if (Math.Min(center.R, Math.Min(center.G, center.B)) < 219 ||
                !Rim(board.GetPixel(x - 7, y)) || !Rim(board.GetPixel(x + 7, y - 2)) ||
                !Rim(board.GetPixel(x - 3, y - 6))) continue;
            double Distance(Point gate) => Math.Sqrt(Math.Pow(x - gate.X, 2) + Math.Pow(y - gate.Y, 2));
            double diff = Distance(upper) - Distance(lower);
            if (Math.Abs(diff) < 12) return result;
            var lane = diff > 0 ? "lower" : "upper";
            if (found != null && found != lane) return result;
            found = lane;
        }
        if (found != null) result["遮天面包机"] = found;
        return result;
    }

    private static string Heading(string text)
    {
        var label = text.Replace(" ", "").Trim();
        // OCR can join the section icon and its label into one three-character token.
        return label.Length == 3 && label[1..] is "敌人" or "悬赏" or "领袖" ? label[1..] : label;
    }

    private static string Normalize(string text) => text.Trim().Replace(" ", "") switch
    {
        "磨茗" => "磨砻",
        "残党乐团圓号手" => "残党乐团圆号手",
        "异质裂兽·a" => "异质裂兽·α",
        "余音" or "“余音" or "余音”" => "“余音”",
        var boss when boss.StartsWith("假想敌:") => boss.Replace(":", "："),
        var other => other
    };

    private async Task<string> ReadNameLineAsync(Bitmap frame, Rectangle area, Bitmap mask, int top, int bottom, double scale)
    {
        int padding = (int)Math.Ceiling(6 * scale);
        var line = Rectangle.Intersect(area, new Rectangle(area.X, area.Y + top - padding, area.Width, bottom - top + 1 + padding * 2));
        // The panel is wider than a name. Pass just the contiguous text to single-line OCR.
        int left = -1, right = -1;
        for (int x = 0; x < mask.Width; x++)
        {
            if (right >= 0 && x - right > 12 * scale) break;
            for (int y = top; y <= bottom; y++)
            {
                if (mask.GetPixel(x, y).R == 0) continue;
                if (left < 0) left = x;
                right = x;
                break;
            }
        }
        int end = Math.Min(line.Right, area.Left + right + padding + 1);
        line.X = Math.Max(line.X, area.Left + left - padding);
        line.Width = end - line.X;
        using var crop = frame.Clone(line, PixelFormat.Format24bppRgb);
        double factor = Math.Max(1.5, 1 / scale);
        using var enlarged = new Bitmap(crop, new Size((int)Math.Round(crop.Width * factor), (int)Math.Round(crop.Height * factor)));
        var words = await ocr.RecognizeLineAsync(ImageBytes.Encode(enlarged), 0, 0, enlarged.Width, enlarged.Height);
        return string.Concat(words.OrderBy(t => t.Box.X).Select(t => t.Text.Trim()));
    }

    private static bool Bright(Color p) => p.R >= 190 && p.G >= 190 && p.B >= 190;
    internal static ulong PageStamp(Bitmap image, Layout layout)
    {
        ulong hash = (14695981039346656037UL ^ (uint)layout.ThumbTop) * 1099511628211UL ^ (uint)layout.ThumbBottom;
        var bounds = layout.Names;
        int step = Math.Max(3, bounds.Width / 36);
        for (int y = layout.Title.Top; y < bounds.Bottom; y += step)
        for (int x = bounds.Left; x < bounds.Right; x += step)
            hash = (hash ^ (Bright(image.GetPixel(x, y)) ? 1UL : 0UL)) * 1099511628211UL;
        return hash;
    }
    private static bool Gold(Color p) => p.R >= 175 && p.G >= 124 && p.B < .6 * p.G;
}

internal sealed record RoundEnemyReadResult(string[] Names, string[] Bounties,
    bool StartsAtTop, bool EndsAtBottom, bool HasUnreadRows, string? LastSection, int Retries)
{
    internal IReadOnlyDictionary<string, string> BountyLanes { get; init; } = new Dictionary<string, string>();
    public string[] Leaders { get; init; } = [];
    public string? UnreadTailAnchor { get; init; }
    internal bool HasNames => Names.Length > 0 || Bounties.Length > 0 || Leaders.Length > 0;
}
