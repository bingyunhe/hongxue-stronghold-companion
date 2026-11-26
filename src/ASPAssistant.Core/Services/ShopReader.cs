// Modified for ASP Assistant Regulation on 2026-11-09; see NOTICE.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Services;

/// <summary>
/// Reads the shop's bottom name strip, independently of card colour and slot count.
/// Uses the selected game's name band and colour preprocessing.
/// Gold light on an operator is not evidence of an elite upgrade.
/// </summary>
public sealed class ShopReader(IOcrEngine engine, IReadOnlyList<string> catalogNames)
{
    private readonly string[] _names = catalogNames.OrderByDescending(n => n.Length).ToArray();
    private volatile GameShopTheme _theme = GameShopTheme.Terminal;
    public GameShopTheme Theme { get => _theme; set => _theme = value; }
    public bool IsShopOpen { get; private set; }
    public string DiagnosticText { get; private set; } = "";
    private (double X, double Y, double Width, double Height) _controlRegion;
    private (double Left, double Width)[] _cardSlots = [];

    public async Task<IReadOnlyList<ShopItem>> ReadAsync(
        byte[] screenshot, int width, int height, IReadOnlyList<string> trackedNames)
    {
        IsShopOpen = false;
        DiagnosticText = "";
        if (trackedNames.Count == 0)
            return [];
        var theme = Theme;

        using var stream = new MemoryStream(screenshot);
        using var source = new Bitmap(stream);
        // Small captures lose thin strokes in the frozen white labels. Normalize
        // their working height before cropping; output coordinates remain ratios.
        using var enlarged = height < 1080 ? new Bitmap((int)Math.Round(width * 1080.0 / height), 1080) : null;
        if (enlarged != null)
        {
            using var graphics = Graphics.FromImage(enlarged);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, 0, 0, enlarged.Width, enlarged.Height);
            width = enlarged.Width;
            height = enlarged.Height;
        }
        var workingImage = enlarged ?? source;
        // Keep the whole bottom quarter: UI scaling moves both the names and
        // the shop control. A thin fixed strip cuts their strokes at 100% UI.
        int canvasTop = height * 3 / 4;
        using var strip = new Bitmap(width * 2, (height - canvasTop) * 2, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(strip))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(workingImage, new Rectangle(0, 0, strip.Width, strip.Height),
                0, canvasTop, width, height - canvasTop, GraphicsUnit.Pixel);
        }
        // 24-bit BMP: no PNG encode/decode on every scan tick (see ImageBytes)
        var encoded = ImageBytes.Encode(strip);
        var texts = (await engine.RecognizeRegionAsync(encoded, 0, 0, strip.Width, strip.Height)).ToList();
        DiagnosticText = $"底部区域放大图 {strip.Width}x{strip.Height}；坐标相对于放大图；原图底部起点 y={canvasTop}：\n" +
            string.Join("\n", texts.Select(t => $"{t.Text} @ {t.Box}"));
        var control = texts.FirstOrDefault(t => t.Text.Contains("收起", StringComparison.Ordinal)
            && t.Box.X + t.Box.W > width * 1.6);
        if (control is null)
        {
            // Side padding on ultrawide captures can put the control left of 80%.
            int controlLeft = Math.Max((int)(strip.Width * .65), strip.Width - height);
            var controls = await engine.RecognizeRegionAsync(encoded, controlLeft, 0,
                strip.Width - controlLeft, strip.Height);
            DiagnosticText += "\n控件复查：" + string.Join("; ", controls.Select(t => $"{t.Text} @ {t.Box}"));
            control = controls.FirstOrDefault(t => t.Text.Contains("收起", StringComparison.Ordinal));
        }
        if (control is null)
            return [];
        _controlRegion = (control.Box.X / (2.0 * width), (canvasTop + control.Box.Y / 2.0) / height,
            control.Box.W / (2.0 * width), control.Box.H / (2.0 * height));
        IsShopOpen = true;

        // Anchor the name line to actual text, or to the adjacent shop control
        // when freezing has hidden every catalogue name from the first pass.
        var items = MatchNames(texts.Where(t => Math.Abs(t.Box.Y + t.Box.H / 2.0
            - control.Box.Y - control.Box.H / 2.0) < control.Box.H * 1.5).ToList(),
            width, height, canvasTop, _names).ToList();
        var centers = items.Select(i => (i.OcrRegion.Y * height - canvasTop
            + i.OcrRegion.Height * height / 2) * 2).Order().ToArray();
        var heights = items.Select(i => i.OcrRegion.Height * height * 2).Order().ToArray();
        double nameCenter = centers.Length == 0 ? control.Box.Y + control.Box.H * 1.1 : centers[centers.Length / 2];
        double textHeight = heights.Length == 0 ? control.Box.H * .8 : heights[heights.Length / 2];
        bool IsOnNameRow(ShopItem item) => Math.Abs((item.OcrRegion.Y * height - canvasTop
            + item.OcrRegion.Height * height / 2) * 2 - nameCenter) <= textHeight * .65;
        items.RemoveAll(item => !IsOnNameRow(item));
        int nameTop = Math.Max(0, (int)(nameCenter - textHeight * .65));
        int nameHeight = Math.Min(strip.Height - nameTop, (int)Math.Ceiling(textHeight * 1.3));
        // Repeated vertical card edges provide the pitch at the current UI size.
        // Portrait details vary by row; a card border persists across most rows.
        var edges = new List<double>();
        var pixels = BmpPixels.TryRead(screenshot) ?? BmpPixels.TryRead(ImageBytes.Encode(source))!;
        int edgeStart = -1, edgeLast = -1;
        int edgeTop = (int)(source.Height * .77), edgeBottom = (int)(source.Height * .87);
        for (int x = (int)(source.Width * .15); x < source.Width * .90; x++)
        {
            int votes = 0;
            for (int y = edgeTop; y < edgeBottom; y++)
            {
                int a = pixels.Color(x - 1, y), b = pixels.Color(x, y);
                if (Math.Abs((a >> 16) - (b >> 16)) + Math.Abs((a >> 8 & 255) - (b >> 8 & 255))
                    + Math.Abs((a & 255) - (b & 255)) > 100)
                    votes++;
            }
            if (votes <= (edgeBottom - edgeTop) * .45)
                continue;
            if (edgeStart >= 0 && x - edgeLast > source.Height * .014)
            {
                edges.Add((edgeStart + edgeLast) / 2.0);
                edgeStart = -1;
            }
            if (edgeStart < 0) edgeStart = x;
            edgeLast = x;
        }
        if (edgeStart >= 0) edges.Add((edgeStart + edgeLast) / 2.0);
        var pitches = edges.Zip(edges.Skip(1), (a, b) => b - a)
            .Where(gap => gap > source.Height * .15 && gap < source.Height * .25).Order().ToArray();
        var cardLefts = new List<int>();
        double pitch = 0;
        if (pitches.Length >= 2)
        {
            pitch = pitches[pitches.Length / 2];
            var aligned = edges.Select(anchor => edges.Where(edge => Math.Abs((edge - anchor) / pitch
                - Math.Round((edge - anchor) / pitch)) < .08).ToArray()).OrderByDescending(row => row.Length).First();
            if (aligned.Length >= 4)
            {
                for (double left = aligned[0]; left < aligned[^1] - pitch * .5; left += pitch)
                    cardLefts.Add((int)Math.Round(left * width / source.Width));
            }
            pitch *= width / (double)source.Width;
        }
        _cardSlots = cardLefts.Select(left => (left / (double)width, pitch / width)).ToArray();
        bool complete = cardLefts.Count > 0;
        int covered = 0;
        foreach (var left in cardLefts)
        {
            int count = items.Count(i => (i.OcrRegion.X + i.OcrRegion.Width / 2) * width >= left
                && (i.OcrRegion.X + i.OcrRegion.Width / 2) * width < left + pitch);
            covered += count;
            if (count > 1 || count == 0 && !ShopPurchaseDetector.IsEmpty(pixels,
                new ShopItem { OcrRegion = ((left + pitch * .14) / width, 0, 0, 0) }))
            {
                complete = false;
                break;
            }
        }
        if (complete && covered == items.Count) return FinishItems(items, trackedNames);
        if (items.Count == 0 && cardLefts.Count > 0 && theme != GameShopTheme.Closure)
        {
            // Frozen names can all disappear. Their band still has a fixed
            // position inside the detected card, scaled by its actual pitch.
            nameTop = (int)Math.Round((height - canvasTop) * 2
                - pitch * (theme == GameShopTheme.Terminal ? .56 : .673));
            nameHeight = (int)Math.Round(pitch * (theme == GameShopTheme.Terminal ? .236 : .273));
            nameCenter = nameTop + nameHeight / 2.0;
            textHeight = nameHeight;
        }
        using (var original = (Bitmap)strip.Clone())
        using (var graphics = Graphics.FromImage(strip))
        {
            graphics.Clear(Color.Black);
            graphics.DrawImage(original, new Rectangle(0, nameTop, strip.Width, nameHeight),
                0, nameTop, strip.Width, nameHeight, GraphicsUnit.Pixel);
            if (theme != GameShopTheme.Closure)
            {
                // Red-channel contrast removes the blue freezing veil without
                // discarding the original dark lettering used by other skins.
                using var attributes = new ImageAttributes();
                float offset = theme == GameShopTheme.Terminal ? -1.3f : -1.72f;
                attributes.SetColorMatrix(new ColorMatrix([
                    [5, 5, 5, 0, 0], [0, 0, 0, 0, 0], [0, 0, 0, 0, 0],
                    [0, 0, 0, 1, 0], [offset, offset, offset, 0, 1]]));
                for (int i = 0; i < cardLefts.Count; i++)
                {
                    int left = cardLefts[i] * 2;
                    int cardWidth = (int)Math.Round(pitch * 2);
                    graphics.DrawImage(original, new Rectangle(left, i % 2 == 0 ? 60 : 200, cardWidth, nameHeight),
                        left, nameTop, cardWidth, nameHeight, GraphicsUnit.Pixel, attributes);
                }
            }
        }
        encoded = ImageBytes.Encode(strip);
        var recovered = await engine.RecognizeRegionAsync(encoded, 0, 0, strip.Width, strip.Height);
        DiagnosticText += "\n增强识别：" + string.Join("; ", recovered.Select(t => $"{t.Text} @ {t.Box}"));
        var mapped = recovered.Select(t => t.Box.Y + t.Box.H / 2.0 < nameTop
            ? t with { Box = (t.Box.X, t.Box.Y + nameTop
                - (t.Box.Y + t.Box.H / 2.0 < 170 ? 60 : 200), t.Box.W, t.Box.H) } : t).ToList();
        foreach (var item in MatchNames(mapped, width, height, canvasTop, _names).Where(IsOnNameRow))
        {
            // The two OCR passes can leave a few pixels between boxes for the same label.
            if (items.Any(i => i.Name == item.Name
                && Math.Max(i.OcrRegion.X, item.OcrRegion.X)
                    < Math.Min(i.OcrRegion.X + i.OcrRegion.Width, item.OcrRegion.X + item.OcrRegion.Width)
                        + height * .005 / width))
                continue;
            items.Add(item);
        }
        if (theme != GameShopTheme.Closure)
        {
            for (int slot = 0; slot < cardLefts.Count; slot++)
            {
                int left = cardLefts[slot];
                if (items.Any(i => (i.OcrRegion.X + i.OcrRegion.Width / 2) * width >= left
                    && (i.OcrRegion.X + i.OcrRegion.Width / 2) * width < left + pitch))
                    continue;
                foreach (double fraction in new[] { .82, .50 })
                {
                    int lineLeft = (int)Math.Round((left + pitch * .14) * 2);
                    int lineWidth = (int)Math.Round(pitch * fraction * 2);
                    var lines = await engine.RecognizeLineAsync(encoded, lineLeft,
                        nameTop, lineWidth, nameHeight);
                    DiagnosticText += $"\n单行区域 {lineLeft},{nameTop},{lineWidth},{nameHeight}：" + string.Join("; ", lines.Select(t => t.Text));
                    var matches = MatchNames(lines.Select(t => new OcrTextResult(
                        t.Text.Replace("斯卡菜", "斯卡蒂").Replace("复泰", "复奏"),
                        (lineLeft, nameTop, lineWidth, nameHeight))).ToList(), width, height, canvasTop, _names).Where(IsOnNameRow).ToList();
                    if (matches.Count == 0) continue;
                    items.AddRange(matches);
                    break;
                }
            }
        }
        return FinishItems(items, trackedNames);
    }

    // Locate every changed slot against a recently named shelf. A changed occupied
    // portrait, a moving shop control or an ambiguous layout requires fresh OCR.
    internal IReadOnlyList<ShopItem>? CompareOpenShelf(BmpPixels previous, BmpPixels current, IReadOnlyList<ShopItem> cards)
    {
        if (!IsShopOpen || previous.Width != current.Width || previous.Height != current.Height || cards.Count < 3)
            return null;
        int w = current.Width, h = current.Height;
        if (_cardSlots.Length < cards.Count) return null;

        // The control label must still be present in the same place.
        if (_controlRegion.Width <= 0 || !Similar((int)(_controlRegion.X * w), (int)(_controlRegion.Y * h),
            (int)(_controlRegion.Width * w), (int)(_controlRegion.Height * h), 2, .94)) return null;
        var remaining = new List<ShopItem>(cards.Count);
        foreach (var card in cards)
        {
            // Long names scroll within a fixed card. Locate its portrait from the card border.
            var center = card.OcrRegion.X + card.OcrRegion.Width / 2;
            var slot = _cardSlots.FirstOrDefault(s => center >= s.Left && center < s.Left + s.Width);
            if (slot.Width == 0) return null;
            if (ShopPurchaseDetector.IsEmpty(current, card)) continue;
            if (!Similar((int)((slot.Left + .18 * slot.Width) * w), (int)(h * .77),
                (int)(slot.Width * w * .58), (int)(h * .09), 4, .96) &&
                !SameGlyphs(previous, current, card.OcrRegion, card.OcrRegion.Height * h)) return null;
            remaining.Add(card);
        }
        return remaining.Count > 0 && UnnamedSlotsAreEmpty(current, remaining) ? remaining : null;

        bool Similar(int left, int top, int width, int height, int step, double minimum)
        {
            double a = 0, b = 0, aa = 0, bb = 0, ab = 0;
            int n = 0;
            for (int y = top; y < Math.Min(h, top + height); y += step)
                for (int x = left; x < Math.Min(w, left + width); x += step)
                {
                    int u = previous.Gray(x,y), v = current.Gray(x,y);
                    a += u; b += v; aa += u*u; bb += v*v; ab += u*v; n++;
                }
            double av = aa*n - a*a, bv = bb*n - b*b;
            return av > 36d*n*n && bv > 36d*n*n && ab*n - a*b >= minimum*Math.Sqrt(av*bv);
        }
    }

    private bool UnnamedSlotsAreEmpty(BmpPixels frame, IReadOnlyList<ShopItem> cards)
    {
        foreach (var slot in _cardSlots)
        {
            if (cards.Any(c => c.OcrRegion.X + c.OcrRegion.Width / 2 >= slot.Left &&
                c.OcrRegion.X + c.OcrRegion.Width / 2 < slot.Left + slot.Width)) continue;
            var location = new ShopItem { OcrRegion = (slot.Left + slot.Width * .14, 0, 0, 0) };
            if (!ShopPurchaseDetector.IsEmpty(frame, location)) return false;
        }
        return true;
    }

    // Compare glyphs locally so a dimmed price state can keep its name, while a
    // replaced character or an appended suffix invalidates that slot.
    internal async Task<bool> VerifyNamedShelfAsync(BmpPixels original, BmpPixels current, IReadOnlyList<ShopItem> remaining)
    {
        if (original.Width != current.Width || original.Height != current.Height || remaining.Count == 0) return false;
        using var stream = new MemoryStream(current.EncodedFrame);
        using var bitmap = new Bitmap(stream);
        if (!UnnamedSlotsAreEmpty(current, remaining) || !await Verify(_controlRegion, "收起", true)) return false;
        foreach (var card in remaining)
            if (!await Verify(card.OcrRegion, card.Name, false)) return false;
        return true;

        async Task<bool> Verify((double X, double Y, double Width, double Height) region, string name, bool control)
        {
            var padding = control ? 0 : region.Height * current.Height;
            if (SameGlyphs(original, current, region, padding)) return true;
            var bounds = Rectangle.Intersect(new Rectangle(0, 0, current.Width, current.Height),
                Rectangle.FromLTRB((int)(region.X * current.Width) - 2, (int)(region.Y * current.Height) - 2,
                    (int)Math.Ceiling((region.X + region.Width) * current.Width + padding) + 2,
                    (int)Math.Ceiling((region.Y + region.Height) * current.Height) + 2));
            var texts = await ReadPatchLineAsync(bitmap, bounds, !control);
            if (control) return texts.Any(t => t.Text.Contains("收起", StringComparison.Ordinal));
            var matches = MatchNames(texts, bounds.Width * 2, bounds.Height * 2, 0, _names);
            return matches.Count == 1 && matches[0].Name == name;
        }
    }

    private static bool SameGlyphs(BmpPixels original, BmpPixels current,
        (double X, double Y, double Width, double Height) region, double padding)
    {
        int left = Math.Max(0, (int)(region.X * current.Width - padding));
        int right = Math.Min(current.Width, (int)Math.Ceiling((region.X + region.Width) * current.Width + padding));
        int top = Math.Max(0, (int)(region.Y * current.Height) - 1);
        int bottom = Math.Min(current.Height, (int)Math.Ceiling((region.Y + region.Height) * current.Height) + 1);
        int block = Math.Max(4, (bottom - top) / 3);
        bool hasInk = false;
        for (int y = top; y < bottom; y += block)
            for (int x = left; x < right; x += block)
            {
                int n = 0, a = 0, b = 0, aa = 0, bb = 0, ab = 0, maxDelta = 0;
                for (int j = y; j < Math.Min(y + block, bottom); j++)
                    for (int i = x; i < Math.Min(x + block, right); i++)
                    {
                        int u = original.Gray(i,j), v = current.Gray(i,j);
                        a += u; b += v; aa += u*u; bb += v*v; ab += u*v; n++;
                        maxDelta = Math.Max(maxDelta, Math.Abs(u-v));
                    }
                double av = (double)aa*n - (double)a*a, bv = (double)bb*n - (double)b*b;
                hasInk |= av > 100d*n*n;
                if (maxDelta <= 18 || av < 16d*n*n && bv < 16d*n*n) continue;
                if (av <= 0 || bv <= 0 || (double)ab*n - (double)a*b < .96*Math.Sqrt(av*bv)) return false;
            }
        return hasInk;
    }

    internal async Task<int> ReadOfferPriceAsync(byte[] frame, ShopItem card, double pitchRatio)
    {
        var (width, height) = ImageBytes.GetDimensions(frame);
        double pitch = width * pitchRatio;
        // Price is printed in the coin above the portrait; position it relative to this card's name.
        int x = (int)Math.Round(card.OcrRegion.X * width + pitch * .44);
        int y = (int)Math.Round(card.OcrRegion.Y * height - pitch * .88);
        int size = (int)Math.Round(pitch * .12);
        if (size < 1 || x < 0 || y < 0 || x + size > width || y + size > height) return 0;
        var line = await engine.RecognizeLineAsync(frame, x, y, size, size);
        return line.Count == 1 && int.TryParse(line[0].Text.Trim(), out int price) && price > 0 ? price : 0;
    }

    private static IReadOnlyList<ShopItem> FinishItems(List<ShopItem> items, IReadOnlyList<string> trackedNames)
    {
        var occurrences = new Dictionary<string, int>();
        items.Sort((a, b) => a.OcrRegion.X.CompareTo(b.OcrRegion.X));
        foreach (var item in items)
        {
            occurrences.TryGetValue(item.Name, out int occurrence);
            item.Id = $"{item.Name}@{occurrence}";
            occurrences[item.Name] = occurrence + 1;
        }
        return items.Where(i => trackedNames.Contains(i.Name)).ToList();
    }

    /// <summary>Verify a candidate on a new frame without recognizing the whole shop again.</summary>
    public async Task<bool> IsSameOpenShopAsync(byte[] before, byte[] after, IReadOnlyList<ShopItem> remaining)
    {
        // A refresh can momentarily empty the whole shop; require a surviving named card.
        if (!IsShopOpen || remaining.Count == 0) return false;
        using var beforeStream = new MemoryStream(before);
        using var previous = new Bitmap(beforeStream);
        using var afterStream = new MemoryStream(after);
        using var current = new Bitmap(afterStream);
        if (previous.Size != current.Size) return false;
        var changed = new List<ShopItem>();
        foreach (var card in remaining)
        {
            // Include the next glyph: a refresh may replace a name with a longer name sharing its prefix.
            int padding = (int)Math.Ceiling(card.OcrRegion.Height * current.Height);
            int left = Math.Max(0, (int)(card.OcrRegion.X * current.Width) - padding);
            int top = Math.Max(0, (int)(card.OcrRegion.Y * current.Height) - 2);
            int right = Math.Min(current.Width, (int)Math.Ceiling((card.OcrRegion.X + card.OcrRegion.Width) * current.Width) + padding);
            int bottom = Math.Min(current.Height, (int)Math.Ceiling((card.OcrRegion.Y + card.OcrRegion.Height) * current.Height) + 2);
            bool same = true;
            for (int y = top; y < bottom && same; y++)
                for (int x = left; x < right; x++)
                    if (previous.GetPixel(x, y) != current.GetPixel(x, y)) { same = false; break; }
            if (!same) changed.Add(card);
        }
        if (changed.Count > 0 && _cardSlots.Length == 0) return false;
        var bounds = Rectangle.FromLTRB((int)(_controlRegion.X * current.Width), (int)(_controlRegion.Y * current.Height),
            (int)Math.Ceiling((_controlRegion.X + _controlRegion.Width) * current.Width),
            (int)Math.Ceiling((_controlRegion.Y + _controlRegion.Height) * current.Height));
        var control = await ReadPatchLineAsync(current, bounds, false);
        if (!control.Any(t => t.Text.Contains("收起", StringComparison.Ordinal))) return false;
        foreach (var card in changed)
        {
            var center = card.OcrRegion.X + card.OcrRegion.Width / 2;
            var slot = _cardSlots.FirstOrDefault(s => center >= s.Left && center < s.Left + s.Width);
            if (slot.Width == 0) return false;
            var nameBounds = Rectangle.FromLTRB(Math.Max((int)((slot.Left + slot.Width * .1) * current.Width),
                    (int)(card.OcrRegion.X * current.Width) - 3),
                Math.Max(0, (int)(card.OcrRegion.Y * current.Height) - 3),
                Math.Min(current.Width, (int)Math.Ceiling((slot.Left + slot.Width * .96) * current.Width)),
                Math.Min(current.Height, (int)Math.Ceiling((card.OcrRegion.Y + card.OcrRegion.Height) * current.Height) + 3));
            var texts = await ReadPatchLineAsync(current, nameBounds, true);
            var names = MatchNames(texts, nameBounds.Width * 2, nameBounds.Height * 2, 0, _names);
            if (names.Count != 1 || names[0].Name != card.Name) return false;
        }
        return true;
    }

    private async Task<IReadOnlyList<OcrTextResult>> ReadPatchLineAsync(Bitmap frame, Rectangle bounds, bool name)
    {
        using var patch = new Bitmap(bounds.Width * 2, bounds.Height * 2);
        using (var graphics = Graphics.FromImage(patch))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (name && Theme != GameShopTheme.Closure)
            {
                using var attributes = new ImageAttributes();
                float offset = Theme == GameShopTheme.Terminal ? -1.3f : -1.72f;
                attributes.SetColorMatrix(new ColorMatrix([
                    [5, 5, 5, 0, 0], [0, 0, 0, 0, 0], [0, 0, 0, 0, 0],
                    [0, 0, 0, 1, 0], [offset, offset, offset, 0, 1]]));
                graphics.DrawImage(frame, new Rectangle(0, 0, patch.Width, patch.Height), bounds.X, bounds.Y, bounds.Width, bounds.Height, GraphicsUnit.Pixel, attributes);
            }
            else graphics.DrawImage(frame, new Rectangle(0, 0, patch.Width, patch.Height), bounds, GraphicsUnit.Pixel);
        }
        return await engine.RecognizeLineAsync(ImageBytes.Encode(patch), 0, 0, patch.Width, patch.Height);
    }

    public IReadOnlyList<ShopItem> Match(IReadOnlyList<OcrTextResult> texts,
        int width, int height, int stripTop, IReadOnlyList<string> trackedNames)
    {
        // Do not leave markers on menus/closed shops with unrelated bottom text.
        if (!texts.Any(t => t.Text.Contains("收起", StringComparison.Ordinal) && t.Box.X + t.Box.W > width * 1.6))
            return [];
        return MatchNames(texts, width, height, stripTop, trackedNames);
    }

    private IReadOnlyList<ShopItem> MatchNames(IReadOnlyList<OcrTextResult> texts,
        int width, int height, int stripTop, IReadOnlyList<string> trackedNames)
    {
        var items = new List<ShopItem>();
        var occurrences = new Dictionary<string, int>();
        foreach (var text in texts.OrderBy(t => t.Box.X))
        {
            var value = text.Text.Replace(" ", "").Replace("傑", "杰").Replace("奧", "奥")
                .Replace("卫成推荐信", "卫戍推荐信").Replace("野繫", "野鬃")
                .Replace("卫成推荐值", "卫戍推荐信")
                .Replace("野紫", "野鬃").Replace("云边", "云迹");
            // Resolve against the whole catalogue before filtering tracking. Otherwise
            // tracking 维式重锤 would also match an untracked 终极维式重锤.
            var matches = new List<(string Name, int Start, int Length)>();
            foreach (var name in _names)
            {
                var candidate = name.Replace(" ", "");
                int start = value.IndexOf(candidate, StringComparison.Ordinal);
                while (start >= 0)
                {
                    if ((candidate.Length > 1 || value.Trim('M', 'X', 'A', '△', '入', 'λ', '×') == candidate)
                        && !matches.Any(m => start < m.Start + m.Length && start + candidate.Length > m.Start))
                        matches.Add((name, start, candidate.Length));
                    start = value.IndexOf(candidate, start + candidate.Length, StringComparison.Ordinal);
                }
            }

            // Wide images can merge neighbouring labels (or 鸿雪 + 收起). Split
            // their OCR bounds by character span instead of discarding a slot.
            foreach (var match in matches.OrderBy(m => m.Start))
            {
                if (!trackedNames.Contains(match.Name))
                    continue;
                // The shopkeeper panel can resemble a one-character operator
                // (e.g. "山"); only accept names within the merchandise area.
                if (text.Box.X + text.Box.W * (match.Start + match.Length / 2.0) / value.Length
                    > width + height * 1440 / 1080.0)
                    continue;
                occurrences.TryGetValue(match.Name, out int occurrence);
                occurrences[match.Name] = occurrence + 1;
                items.Add(new ShopItem
                {
                    Id = $"{match.Name}@{occurrence}",
                    Name = match.Name,
                    OcrRegion = ((text.Box.X + text.Box.W * match.Start / (double)value.Length) / (2 * width),
                        (stripTop + text.Box.Y / 2.0) / height,
                        text.Box.W * match.Length / (double)value.Length / (2 * width), text.Box.H / (2.0 * height)),
                });
            }
        }
        return items;
    }
}
