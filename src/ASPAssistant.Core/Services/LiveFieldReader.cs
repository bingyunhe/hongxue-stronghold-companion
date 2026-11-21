// Per-cell badge recognition adapted from LBEILC/StrongholdAssistant (Apache-2.0).
using System.Drawing;
using System.Drawing.Imaging;

using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Services;

public readonly record struct FieldSlot(int Col, int Row)
{
    public bool Bench => Row == -1;
    public override string ToString() => Bench ? $"整备区右起第{10 - Col}席" : PlacementCells.Code(new(Col, Row));
}
public sealed record FieldMark(FieldSlot Slot, int Tier, Point Foot, byte[] Appearance, string? SpriteName)
{
    internal IReadOnlyDictionary<string,double>? Scores { get; init; }
    internal double Tile { get; init; }
    internal string? StandaloneName { get; init; }
    internal byte[] Portrait { get; init; } = [];
    internal int[] MemoryPatch { get; init; } = [];
    internal (double X,double Y)? ShapeCenter { get; init; }
    internal BoardCamera Camera { get; init; }
    internal Dictionary<string,double>? PurchasePoseScores { get; set; }
    internal FieldShapeLibrary.Candidate[] ShapeCandidates { get; init; } = [];
    internal FieldShapeLibrary.Candidate? LowPose { get; init; }
    internal byte[]? BenchShapeDigest { get; init; }
}
internal sealed record FieldBadge(double X, double Y, int Tier, double Score, double Size);
public sealed partial class LiveFieldReader
{
    private readonly List<(int Tier, int Size, double[] Values, double[] Fine, double[] Lower, double[] LowerFine)> _templates = [];
    private readonly GarrisonBoardData _maps;
    private readonly Lazy<FieldSpriteLibrary> _sprites;
    private readonly Lazy<FieldShapeLibrary> _shapes;
    private readonly Lazy<Dictionary<string,byte[][]>> _purchasePoses;
    private sealed record BoardAppearance(string Name, int Tier, int[][] Samples);
    private readonly BoardAppearance[] _boardAppearances;
    private sealed record CityFieldReference(Dictionary<string, int[]> Empty, BoardAppearance[] Looks, BoardAppearance[] TopRowLooks)
    {
        public Dictionary<string, byte[]> ReviewHeads { get; init; } = [];
    }
    private readonly CityFieldReference _cityField;
    private readonly (string Name, double[] Head)[] _reviewHeads;
    public LiveFieldReader(string directory, GarrisonBoardData maps)
    {
        _maps = maps;
        using var boardFile = File.OpenRead(Path.Combine(directory, "template", "field-board-appearances.json"));
        _boardAppearances = System.Text.Json.JsonSerializer.Deserialize<BoardAppearance[]>(boardFile)!;
        using var cityFile = File.OpenRead(Path.Combine(directory, "template", "city-expanded-field.json"));
        _cityField = System.Text.Json.JsonSerializer.Deserialize<CityFieldReference>(cityFile)!;
        _reviewHeads = _cityField.ReviewHeads.Select(entry =>
        {
            if (entry.Value.Length != 7 * 7 * 3)
                throw new InvalidDataException($"Invalid review head sample for {entry.Key}");
            var head = Array.ConvertAll(entry.Value, value => (double)value);
            NormalizeHead(head);
            return (entry.Key, head);
        }).ToArray();
        _purchasePoses = new(() => LoadPurchasePoses(Path.Combine(directory, "template", "field-purchase-poses.json")));
        _sprites = new(() => new FieldSpriteLibrary(Path.Combine(directory, "template", "field-sprites.bin")));
        _shapes = new(() => new FieldShapeLibrary(Path.Combine(directory, "template", "field-shapes.bin")));
        for (int tier = 1; tier <= 6; tier++)
        {
            using var image = new Bitmap(Path.Combine(directory, "template", $"field-tier-{tier}.png"));
            // IV/VI 原图是暗框图标；复用共用亮框，将原字形还原到场上角标比例。
            if (tier is 4 or 6)
            {
                using var glyph = (Bitmap)image.Clone();
                using var border = new Bitmap(Path.Combine(directory, "template", "field-tier-3.png"));
                using var g = Graphics.FromImage(image);
                g.DrawImage(border, new Rectangle(0, 0, border.Width, border.Height), new Rectangle(0, 0, border.Width, border.Height), GraphicsUnit.Pixel);
                g.DrawImage(glyph, new Rectangle(5, 5, 30, 30), new Rectangle(8, 8, 24, 24), GraphicsUnit.Pixel);
            }
            foreach (int size in new[] { 34, 40, 44 })
            {
                using var scaled = new Bitmap(size, size);
                using (var g = Graphics.FromImage(scaled)) { g.Clear(Color.Black); g.DrawImage(image, new Rectangle(0, 0, size, size), new Rectangle(0, 0, image.Width, image.Height), GraphicsUnit.Pixel); }
                var px = BmpPixels.TryRead(ImageBytes.Encode(scaled))!;
                var values = Sample(px, 0, 0, size);
                Normalize(values);
                var fine = Sample(px, 0, 0, size, 20); Normalize(fine);
                var lower = Sample(px, 0, 0, size, 10, 5); Normalize(lower);
                var lowerFine = Sample(px, 0, 0, size, 20, 10); Normalize(lowerFine);
                _templates.Add((tier, size, values, fine, lower, lowerFine));
            }
        }
    }
    private Dictionary<FieldSlot, FieldMark> _previous = [];
    private string? _map;

    internal Point CellCenter(FieldSlot slot, BoardCamera camera, BmpPixels pixels)
    {
        var center = _maps.Cameras[camera].Project(slot.Col + .5, slot.Row + .5);
        return new((int)Math.Round(center.X * pixels.Width), (int)Math.Round(center.Y * pixels.Height));
    }

    // While dragging, the game paints both legal floor cells green and the bench cyan.
    // The sprite still shown in its old seat is a ghost, not a second observation.
    internal static bool IsDragging(BmpPixels frame)
    {
        int cyan = 0, green = 0;
        for (int y=68; y<81; y+=2)
            for (int x=48; x<120; x+=2)
            {
                int rgb=frame.Color(frame.Width*x/160,frame.Height*y/90);
                int r=rgb>>16, g=(rgb>>8)&255, b=rgb&255;
                if (g>70 && b>r*1.3 && g>r*1.25 && b>.72*g) cyan++;
            }
        if (cyan<45) return false;
        for (int y=14; y<59; y+=2)
            for (int x=48; x<113; x+=2)
            {
                int rgb=frame.Color(frame.Width*x/160,frame.Height*y/90);
                int r=rgb>>16, g=(rgb>>8)&255, b=rgb&255;
                if (g>1.25*r && g>1.4*b && g>65) green++;
            }
        return green>=55;
    }
    public List<FieldMark> Read(BmpPixels frame, BoardMatch board, bool identify, bool force = false, CancellationToken cancellation = default)
        => ReadWithTrace(frame, board, identify, force, cancellation, null);

    internal List<FieldMark> ReadWithTrace(BmpPixels frame, BoardMatch board, bool identify, bool force,
        CancellationToken cancellation, Dictionary<FieldSlot, SlotReading>? readings)
    {
        var marks = new List<FieldMark>();
        var unanchored = new HashSet<FieldSlot>();
        var phantoms = new HashSet<FieldSlot>();
        var anchors = new List<(double X,double Y)>();
        double scale = frame.Width / 1920d;
        bool expanded = board.Camera == BoardCamera.Expanded;
        var projection = _maps.Cameras[board.Camera];
        foreach (var (cell, type) in board.Map.AllCells())
        {
            if (cell.Row >= 4 || !GarrisonMap.CanDeploy(type, false)) continue;
            var center = projection.Project(cell.Col + .5, cell.Row + .5);
            var right = projection.Project(cell.Col + 1.5, cell.Row + .5);
            double tile = Math.Abs(right.X - center.X) * frame.Width;
            Inspect(new(cell.Col, cell.Row), center.X * frame.Width - tile * (expanded ? .4 : .35),
                center.Y * frame.Height - (expanded ? 110 : 116 - 2 * cell.Row) * scale, tile);
        }
        // A model in the next row can extend over this cell. Compare against the
        // independently detected neighbours before accepting a foreground-only mark.
        foreach (var slot in unanchored)
        {
            var center = projection.Project(slot.Col + .5,slot.Row + .5);
            double x = center.X * frame.Width, y = center.Y * frame.Height;
            if (!marks.Any(m => m.Slot.Row == slot.Row + 1 && !unanchored.Contains(m.Slot) &&
                Math.Abs(m.Foot.X-x) < m.Tile*.5 && y <= m.Foot.Y && y >= m.Foot.Y-m.Tile)) continue;
            marks.RemoveAll(m => m.Slot == slot);
            if (readings != null) readings[slot] = readings[slot] with { Decision = SlotReading.Uncertain };
        }
        for (int col = 0; col < 10; col++)
            Inspect(new(col, -1), (expanded ? 254 + col * 121.2 : 170 + col * 149.5) * scale,
                (expanded ? 609 : 798) * frame.Height / 1080d, (expanded ? 121.2 : 149.5) * scale);
        InspectShapes(frame,board,marks,anchors,phantoms,identify,force,cancellation,readings);
        cancellation.ThrowIfCancellationRequested();
        for (int i=0;i<marks.Count;i++)
            if (marks[i].MemoryPatch.Length==0 && marks[i].Portrait.Length!=0)
                marks[i]=marks[i] with { MemoryPatch=FieldMemoryAppearance.Sample(frame,marks[i]),Camera=board.Camera };
        if (identify) { _previous = marks.ToDictionary(m => m.Slot); _map = board.Map.Id; }
        return marks;

        void Inspect(FieldSlot slot, double x, double y, double tile)
        {
            cancellation.ThrowIfCancellationRequested();
            // The covenant HUD can cover the entire badge in row A. Look for a known
            // head and torso relative to the tile, independently of that overlay.
            BoardAppearance? boardLook = null;
            double boardError = 0;
            var center = projection.Project(slot.Col + .5, slot.Row + .5);
            double cx = center.X * frame.Width, cy = center.Y * frame.Height;
            bool city = expanded && !slot.Bench && board.Map.Id == "city";
            bool anchored = false;
            bool foreground = city && ChangedFromEmpty(frame, cx, cy, tile,
                board.Map.At(new(slot.Col, slot.Row)) == BoardCellType.High, slot.Row == 0, _cityField.Empty[slot.ToString()], out anchored);
            var badge = ReadBadge(frame, frame.Width, frame.Height, x, y, !slot.Bench, expanded, out var bounds);
            int badgeTier = badge is { Score: >= .76 } ? badge.Tier : 0;
            void Trace(string reason) { if (readings != null) readings[slot] = new(bounds, reason) { BadgeTier = badgeTier }; }
            if (!slot.Bench && (badge != null || expanded && slot.Row == 0))
            {
                boardLook = MatchBoardAppearance(_boardAppearances, frame, cx, cy, tile,
                    expanded && slot.Row == 0 ? null : badge?.Tier, out boardError);
                if (boardLook != null && badge?.Tier != boardLook.Tier)
                    badge = null;
            }
            if (badge is null && boardLook is null)
            {
                if (!foreground) { Trace("角标与已知棋盘外观均未匹配"); return; }
                boardLook = MatchBoardAppearance(_boardAppearances, frame, cx, cy, tile, null, out boardError);
            }
            if (badge != null && (badge.X + 90 * scale >= frame.Width || badge.Y + 151 * scale >= frame.Height ||
                slot.Bench && HasSummonCount(frame, badge, scale))) { Trace("超出取样边界或整备区召唤物"); return; }
            var foot = new Point((int)((badge?.X ?? x) + 35 * scale), (int)((badge?.Y ?? y) + (slot.Bench ? 132 : 112) * scale));
            if (!slot.Bench && badge is { Score: >= .76 })
            {
                double dx=(foot.X-cx)/tile+.085,dy=(foot.Y-cy)/tile+.11;
                if (Math.Abs(dx)<.12 && Math.Abs(dy)<.12) anchors.Add((dx,dy));
            }
            var tier = boardLook?.Tier ?? badge?.Tier ?? 0; // Occupied without a readable tier badge.
            bool obscured = boardLook == null && badge != null && (!HasUnitEvidence(frame, badge, frame.Width, frame.Height, !slot.Bench)
                || tier == 4 && badge!.Score < .73);
            if (obscured && !anchored && tier != 1 && (slot.Bench || tier != 4))
            {
                if (!TryBenchShape()) Trace(city ? SlotReading.Uncertain : "身体特征不足");
                return;
            }
            if (!slot.Bench && tier == 4 && IsPhantom(frame, foot, tile)) { phantoms.Add(slot); Trace("幻影过滤"); return; }
            if (city && boardLook == null && (foreground || tier is 1 or 3))
            {
                boardLook = MatchBoardAppearance(_cityField.Looks, frame, cx, cy, tile, badge?.Tier, out boardError);
                if (boardLook == null && slot.Row == 0)
                    boardLook = MatchBoardAppearance(_cityField.TopRowLooks, frame, cx, cy, tile, badge?.Tier, out boardError);
                if (boardLook != null) tier = boardLook.Tier;
            }
            if (foreground && !anchored && boardLook == null && badge == null) unanchored.Add(slot);
            Trace(foreground ? "与空城区的脚下、身体区域均不同" : "检测到占用");
            var appearance = BodySample(frame,foot,tile,false);
            if (identify && !force && _map == board.Map.Id && _previous.TryGetValue(slot, out var prior) &&
                prior.Camera == board.Camera && prior.Tier == tier && (boardLook == null || prior.StandaloneName == boardLook.Name) && LiveFieldState.Difference(prior.Appearance, appearance) < 5 &&
                (!obscured || prior.SpriteName is string known && prior.Scores![known] <= 23))
            {
                marks.Add(prior with { Foot=foot, Appearance=appearance, Tile=tile });
                return;
            }
            // Sample the center of the head and torso, away from neighboring weapons and floor edges.
            var portrait=BodySample(frame,foot,tile,true);
            if (!identify && (!obscured || anchored))
            {
                marks.Add(new(slot,tier,foot,appearance,boardLook?.Name) { Tile=tile, Portrait=portrait });
                return;
            }
            if (boardLook != null)
            {
                marks.Add(new(slot,tier,foot,appearance,boardLook.Name) { Tile=tile, Portrait=portrait,
                    StandaloneName=boardLook.Name, Scores=new Dictionary<string,double> { [boardLook.Name]=boardError } });
                return;
            }
            if (tier == 0)
            {
                marks.Add(new(slot, tier, foot, appearance, null) { Tile = tile, Portrait = portrait });
                return;
            }
            var name = _sprites.Value.Match(frame,foot,tile,tier,slot.Bench,out var scores,out var patch);
            if (obscured && (name == null || scores[name] > 23))
            {
                if (anchored)
                {
                    Trace("本格脚下及身体有独立前景，身份待复核");
                    marks.Add(new(slot,tier,foot,appearance,null) { Tile=tile, Portrait=portrait });
                }
                else if (!TryBenchShape()) Trace(city ? SlotReading.Uncertain : "遮挡且身份匹配不足");
                return;
            }
            if (!identify)
            {
                marks.Add(new(slot,tier,foot,appearance,null) { Tile=tile, Portrait=portrait });
                return;
            }
            var mark = new FieldMark(slot,tier,foot,appearance,name) { Scores=scores, Tile=tile, Portrait=portrait };
            var poses = _purchasePoses.Value;
            var shortlist = poses.Keys.Where(scores.ContainsKey)
                .OrderBy(n=>PoseError(patch,slot.Bench,poses[n],25)).Take(5);
            foreach (var candidate in shortlist)
                (mark.PurchasePoseScores ??= [])[candidate]=PoseError(patch,slot.Bench,poses[candidate],5);
            var ranked = scores.Select(p => (p.Key, Error:Math.Min(p.Value,mark.PurchasePoseScores?.GetValueOrDefault(p.Key,double.MaxValue) ?? double.MaxValue)))
                .OrderBy(p=>p.Error).Take(2).ToArray();
            string? independent=null;
            if (ranked.Length==2)
            {
                var best=ranked[0];
                bool dense = mark.PurchasePoseScores?.GetValueOrDefault(best.Key,double.MaxValue) == best.Error;
                // Dense, foot-aligned poses have more independent image samples than the legacy sparse sprites.
                if (best.Error < (dense ? 30 : 25) && ranked[1].Error-best.Error >= (dense ? 3 : 5))
                    independent=best.Key;
            }
            if (mark.PurchasePoseScores is { Count: >=2 } measured)
            {
                var dense=measured.OrderBy(v=>v.Value).Take(2).ToArray();
                var best=dense[0];
                double margin=dense[1].Value-best.Value;
                bool agrees=best.Value<=34 && (name==best.Key && margin>=3 ||
                    margin>=4 && scores[best.Key]<=32 && scores[best.Key]==scores.Values.Min());
                bool distinctive=best.Value<=32 && margin>=10 && (name==null || name==best.Key || scores[name]>=25);
                bool sparseAndDense = name==best.Key && scores[best.Key]<=28 && margin>=3;
                if (agrees || distinctive || sparseAndDense) independent=best.Key;
            }
            marks.Add(mark with { StandaloneName=independent });

            bool TryBenchShape()
            {
                if (!slot.Bench || badge is not { Score: >= .76 }) return false;
                // Includes the full matcher crop and its blur border in either camera.
                var digest=identify?frame.RegionDigest((int)(foot.X-tile*1.1),(int)(foot.Y-tile*2.05),
                    (int)Math.Ceiling(foot.X+tile*1.15),(int)Math.Ceiling(foot.Y+tile*.5)):null;
                if (identify && !force && _map==board.Map.Id && _previous.TryGetValue(slot,out var previous) &&
                    previous.Camera==board.Camera && previous.Foot==foot && previous.Tile==tile && previous.Tier==tier &&
                    digest.AsSpan().SequenceEqual(previous.BenchShapeDigest))
                {
                    marks.Add(previous);
                    Trace("整备席角标与人物轮廓相符");
                    return true;
                }
                var fits=_shapes.Value.Match(frame,foot.X+.05*tile,foot.Y+.039*tile,tile,tier,cancellation);
                if (fits.Length<2 || fits[0].Score<.65 || fits[0].Score-fits[1].Score<.11) return false;
                marks.Add(new(slot,tier,foot,BodySample(frame,foot,tile,false),null)
                {
                    Tile=tile,Portrait=BodySample(frame,foot,tile,true),
                    StandaloneName=identify?Accepted(fits)?.Name:null,ShapeCandidates=identify?fits:[],BenchShapeDigest=digest
                });
                Trace("整备席角标与人物轮廓相符");
                return true;
            }
        }
    }
    private static bool ChangedFromEmpty(BmpPixels frame, double cx, double cy, double tile, bool high, bool topRow, int[] empty, out bool anchored)
    {
        ReadOnlySpan<int> columns = [-22, -13, -4, 5, 14, 23];
        ReadOnlySpan<int> rows = [-6, -13, -20, -30, -39, -48, -57, -66];
        int radius = Math.Max(1, (int)Math.Round(tile * .018));
        int index = 0, feet = 0, body = 0;
        int footBands = 0, torsoBands = 0, upperBody = 0;
        foreach (int row in rows)
        {
            int y = (int)Math.Round(cy + (row - (high ? 18 : 0)) * tile / 100);
            int previous = 0, horizontalContrast = 0, changed = 0;
            foreach (int col in columns)
            {
                int x = (int)Math.Round(cx + col * tile / 100);
                int a = frame.Color(x-radius,y), b = frame.Color(x+radius,y);
                int c = frame.Color(x,y-radius), d = frame.Color(x,y+radius);
                int background = empty[index++], difference = 0, average = 0;
                for (int shift = 0; shift <= 16; shift += 8)
                {
                    int value = (((a >> shift) & 255) + ((b >> shift) & 255) + ((c >> shift) & 255) + ((d >> shift) & 255)) / 4;
                    average |= value << shift;
                    if (col != columns[0]) horizontalContrast += Math.Abs(value - ((previous >> shift) & 255));
                    difference += Math.Abs(value - ((background >> shift) & 255));
                }
                previous = average;
                if (difference > 3 * 30) changed++;
                if (row >= -20) { if (difference > 3 * 60) feet++; }
                else if (difference > 3 * 38) body++;
            }
            // A neighbour can cover the feet or torso alone. Require foreground with
            // lateral detail in multiple bands both at and above this cell's anchor.
            if (changed >= 3 && horizontalContrast >= 5 * 3 * 20)
            {
                if (row >= -20) footBands++;
                else torsoBands++;
            }
            if (row == -39) upperBody = body;
            if (row == -20 && feet < 12 && footBands < 2) { anchored = false; return false; }
        }
        anchored = footBands >= 2 && torsoBands >= 2;
        return anchored || feet >= 12 && (body >= 14 || topRow && feet >= 15 && upperBody >= 8);
    }

    internal string[] RankReviewCandidates(BmpPixels pixels, BoardMatch board, FieldSlot slot)
    {
        if (slot.Bench || board.Map.Id != "city" || board.Camera != BoardCamera.Expanded || _reviewHeads.Length == 0) return [];
        var projection = _maps.Cameras[board.Camera];
        var center = projection.Project(slot.Col + .5, slot.Row + .5);
        double tile = (projection.Project(slot.Col + 1.5, slot.Row + .5).X - center.X) * pixels.Width;
        int step = Math.Max(1, (int)Math.Round(tile * .035));
        var best = new double[_reviewHeads.Length];
        Span<double> patch = stackalloc double[7 * 7 * 3];
        for (int dy = -step; dy <= step; dy += step)
        for (int dx = -2 * step; dx <= 2 * step; dx += step)
        {
            int i = 0;
            for (int row = -79; row < -44; row += 5)
            for (int col = -16; col < 12; col += 4)
            {
                int x = (int)Math.Round(center.X * pixels.Width + col * tile / 100) + dx;
                int y = (int)Math.Round(center.Y * pixels.Height + row * tile / 100) + dy;
                int left = pixels.Color(x-1,y), right = pixels.Color(x+1,y);
                int top = pixels.Color(x,y-1), bottom = pixels.Color(x,y+1);
                for (int shift = 16; shift >= 0; shift -= 8)
                    patch[i++] = (((left >> shift) & 255) + ((right >> shift) & 255) +
                                  ((top >> shift) & 255) + ((bottom >> shift) & 255)) / 4;
            }
            NormalizeHead(patch);
            for (int n = 0; n < _reviewHeads.Length; n++)
            {
                double correlation = 0;
                var head = _reviewHeads[n].Head;
                for (int j = 0; j < patch.Length; j++) correlation += patch[j] * head[j];
                best[n] = Math.Max(best[n], correlation);
            }
        }
        return Enumerable.Range(0, best.Length).Where(n => best[n] > 0)
            .OrderByDescending(n => best[n]).Take(3).Select(n => _reviewHeads[n].Name).ToArray();
    }

    private static void NormalizeHead(Span<double> rgb)
    {
        double energy = 0;
        for (int channel = 0; channel < 3; channel++)
        {
            double mean = 0;
            for (int i = channel; i < rgb.Length; i += 3) mean += rgb[i];
            mean /= rgb.Length / 3;
            for (int i = channel; i < rgb.Length; i += 3)
            {
                rgb[i] -= mean;
                energy += rgb[i] * rgb[i];
            }
        }
        if (energy == 0) return;
        double length = Math.Sqrt(energy);
        for (int i = 0; i < rgb.Length; i++) rgb[i] /= length;
    }

    private static BoardAppearance? MatchBoardAppearance(BoardAppearance[] appearances, BmpPixels pixels, double cx, double cy, double tile, int? tier, out double best)
    {
        best = 22;
        BoardAppearance? match = null;
        int step = Math.Max(1, (int)Math.Round(tile * .035));
        foreach (var look in appearances)
        {
            if (tier.HasValue && tier != look.Tier) continue;
            for (int dy = -step; dy <= step; dy += step)
            for (int dx = -2 * step; dx <= 2 * step; dx += step)
            {
                int sum = 0;
                double limit = best * 3 * look.Samples.Length;
                foreach (var sample in look.Samples)
                {
                    int x = (int)Math.Round(cx + sample[0] * tile / 100) + dx;
                    int y = (int)Math.Round(cy + sample[1] * tile / 100) + dy;
                    if (x < 1 || y < 1 || x >= pixels.Width - 1 || y >= pixels.Height - 1) { sum = int.MaxValue; break; }
                    int a = pixels.Color(x-1,y), b = pixels.Color(x+1,y), c = pixels.Color(x,y-1), d = pixels.Color(x,y+1);
                    for (int channel = 0; channel < 3; channel++)
                    {
                        int shift = 16 - channel * 8;
                        int value = (((a >> shift) & 255) + ((b >> shift) & 255) + ((c >> shift) & 255) + ((d >> shift) & 255)) / 4;
                        sum += Math.Abs(value - sample[2 + channel]);
                    }
                    if (sum >= limit) break;
                }
                if (sum >= limit) continue;
                match = look;
                best = sum / (3d * look.Samples.Length);
            }
        }
        return match;
    }

    private FieldBadge? ReadBadge(BmpPixels px, int width, int height, double cx, double cy, bool board, bool expanded, out Rectangle bounds)
    {
        double scale = width / 1920d;
        // 展开商店时第一排角标与盟约栏重叠，只比较角标下半部。
        bool lower = board && cy < 180 * scale;
        var values = new double[lower ? 50 : 100];
        var fine = new double[lower ? 200 : 400];
        FieldBadge? best = null;
        bounds = Rectangle.Empty;
        foreach (int size in new[] { 34, 40, 44 })
        {
            var templates = _templates.Where(t => t.Size == size).ToArray();
            int actualSize = Math.Max(10, (int)Math.Round(size * scale));
            int radius = (int)Math.Ceiling((board ? 9 : 12) * scale);
            int horizontal = (int)Math.Ceiling((!board && expanded ? 18 : 12) * scale);
            // 横向扩展按完整的两像素步长进行，保留原搜索窗的粗采样位置。
            horizontal += (horizontal - radius) % 2;
            // Bottom-row badges can sit another coarse sample to the left in the open-shop camera.
            if (board && expanded && cy > height * .37) horizontal += 2;
            int left = (int)Math.Round(cx) - actualSize / 2;
            int top = (int)Math.Round(cy) - actualSize / 2;
            // Search the full vertical interval between the normal and elevated badge.
            int step = board ? 2 : 1;
            int firstY = (int)Math.Round(cy - (board ? 26 * scale : expanded ? 20 * scale : 0)) - actualSize / 2 - radius;
            var area = Rectangle.FromLTRB(Math.Max(0,left-horizontal), Math.Max(0,firstY),
                Math.Min(width,left+horizontal+actualSize+step-1), Math.Min(height,top+radius+actualSize+step-1));
            bounds = bounds.IsEmpty ? area : Rectangle.Union(bounds, area);
            for (int y = Math.Max(0, firstY); y <= Math.Min(height - actualSize - 1, top + radius); y += step)
                for (int x = Math.Max(0, left - horizontal); x <= Math.Min(width - actualSize - 1, left + horizontal); x += step)
                {
                    // 位置已受约束，亮框预筛允许比整屏搜索更低的对比度。
                    // Every tier badge has a dark interior on both sides of the numeral.
                    int inset = actualSize / 5;
                    int band = y + actualSize / 3;
                    if (!board && px.Gray(x + inset, band) > 110 && px.Gray(x + actualSize - inset - 1, band) > 110) continue;
                    int mid = actualSize / 2;
                    int outer = Math.Max(1, (int)Math.Round(2 * scale));
                    int inner = Math.Max(outer + 1, (int)Math.Round(7 * scale));
                    if (px.Gray(x + outer, y + mid) < px.Gray(x + inner, y + mid) + 6
                        && px.Gray(x + actualSize - outer - 1, y + mid) < px.Gray(x + actualSize - inner - 1, y + mid) + 6) continue;
                    if (!board)
                    {
                        int above=y+actualSize/3, below=y+actualSize*2/3;
                        bool Rim(int row) => px.Gray(x+outer,row)-px.Gray(x+inner,row)>=6 ||
                            px.Gray(x+actualSize-outer-1,row)-px.Gray(x+actualSize-inner-1,row)>=6;
                        if (!Rim(above) || !Rim(below)) continue;
                    }
                    Sample(px, x, y, actualSize, 10, lower ? 5 : 0, values); Normalize(values);
                    bool coarseMatch = false;
                    foreach (var template in templates)
                    {
                        if (Dot(lower ? template.Lower : template.Values, values) < .60) continue;
                        coarseMatch = true;
                        break;
                    }
                    if (!coarseMatch)
                    {
                        // Narrow coloured numerals can fall between coarse samples.
                        if (!board || !expanded || lower) continue;
                        int color = px.Color(x + mid, y + mid);
                        if (((color >> 8) & 255) < 115 || ((color >> 8) & 255) < (color >> 16) + 65) continue;
                    }
                    for(int yy=y;yy<y+step && yy<height-actualSize;yy++)
                        for(int xx=x;xx<x+step && xx<width-actualSize;xx++)
                        {
                            Sample(px, xx, yy, actualSize, 20, lower ? 10 : 0, fine); Normalize(fine);
                            foreach (var t in templates)
                            {
                                if (!coarseMatch && t.Tier is not (3 or 4)) continue;
                                double score = Dot(lower ? t.LowerFine : t.Fine, fine);
                                if (best is not null && score <= best.Score) continue;
                                if (score < .73)
                                {
                                    // 一阶细竖线的外框会被邻格小人挡住一角；两侧亮边均清晰时允许轻微缺损。
                                    if (!board || lower) continue;
                                    if (t.Tier == 4)
                                    {
                                        if (score < .69) continue;
                                        int rim = px.Color(xx + outer, yy + mid);
                                        if ((rim & 255) < (rim >> 16) + 50) continue;
                                    }
                                    else if (score < .71 || t.Tier != 1
                                        || px.Gray(xx + outer, yy + mid) - px.Gray(xx + inner, yy + mid) < 30
                                        || px.Gray(xx + actualSize - outer - 1, yy + mid) - px.Gray(xx + actualSize - inner - 1, yy + mid) < 30) continue;
                                }
                                best = new FieldBadge(xx + actualSize / 2, yy + actualSize / 2, t.Tier, score, actualSize);
                            }
                        }
                }
        }
        return best;
    }

    // 整备区召唤物的数量标签：角标右侧为暗色胶囊，内部有亮色 X 与数字。
    private static bool HasSummonCount(BmpPixels px, FieldBadge badge, double scale)
    {
        int Gray(double x, double y) => px.Gray((int)(badge.X + x * scale), (int)(badge.Y + y * scale));
        int border = 0, ink = 0;
        for (int x = 30; x <= 74; x += 2)
        {
            if (Gray(x, -17) > Gray(x, -9) + 18 && Gray(x, 17) > Gray(x, 9) + 18) border++;
            for (int y = -7; y <= 7; y += 2) if (Gray(x, y) > 150) ink++;
        }
        return border >= 19 && ink >= 12;
    }

    // 夜莺的幻影继承本体角标。南瓜面具的两个深色眼孔嵌在同一块橙色脸部。
    private static bool IsPhantom(BmpPixels frame, Point foot, double tile)
    {
        int orange = 0, dark = 0;
        for (int row = 24; row <= 36; row += 3)
            for (int col = -15; col <= 15; col += 3)
            {
                int rgb = frame.Color(foot.X + (int)Math.Round(col * tile / 100), foot.Y - (int)Math.Round(row * tile / 100));
                int r = rgb >> 16, g = (rgb >> 8) & 255, b = rgb & 255;
                if (r > 135 && g > .38 * r && g < .65 * r && b > .53 * g && b < .87 * g) orange++;
                if (r < 95 && g < 80 && b < 80) dark++;
            }
        return orange >= 23 && dark >= 4;
    }

    private static bool HasUnitEvidence(BmpPixels px, FieldBadge badge, double width, double height, bool board)
    {
        double scale = width / 1920;
        int Edges(int top,int spacing=0)
        {
            int edges = 0;
            for (int row = top; row < top + 20; row += 2)
                for (int col = 18; col < 65; col += 2)
                {
                    int x = (int)(badge.X + col * scale), y = (int)(badge.Y + row * scale);
                    int x2 = x + (spacing==0 ? Math.Max(1,(int)Math.Round(2*scale)) : spacing);
                    if (x2 < width && y < height && Math.Abs(px.Gray(x, y) - px.Gray(x2, y)) > 32) edges++;
                }
            return edges;
        }
        int body = Edges(55);
        // 棋盘人物的密集轮廓可能偏上（如黑键），检查相邻的上下两段身体，沿用原门槛。
        if (board && badge.Score >= (badge.Tier == 4 ? .75 : .78) && scale < .72 && Edges(55,2)>=48 && Edges(95,2)>=45) return true;
        if (body < 22)
        {
            // 宽帽的中段可能是整块纯色；上缘和下身都清晰才按人物计。
            if (badge.Tier == 2 && badge.Score >= .8)
            {
                // A low, dark familiar has a solid silhouette below the usual torso band.
                int dark = 0;
                for (int row = 101; row <= 125; row += 8)
                for (int col = 14; col <= 68; col += 3)
                    if (px.Gray((int)(badge.X+col*scale), (int)(badge.Y+row*scale)) < 60) dark++;
                if (dark >= 44) return true;
            }
            if (!board || badge.Tier > 3 || body < 8) return false;
            if (Edges(15) >= 40 && Edges(75) >= 22 && Edges(95) >= 45) return true;
            // A turned-away unit can show a solid head in the middle strip.
            return badge.Tier == 1 && badge.Score >= .76 && body >= 12 && Edges(35) >= 28 && Edges(95) >= 38;
        }
        if (badge.Tier is not (4 or 6)) return true;
        if (Edges(15) < (board ? 12 : 22)) return false;
        int bottom = Edges(75);
        int upper = Edges(35);
        if (!board) return body + Math.Max(upper, bottom) >= 130 ||
            badge.Score >= .78 && bottom >= 40 && Edges(95) >= 44 && body + upper + bottom >= 106;
        // 增益光效使轮廓分散在三段；上下段都清晰时也能确认身体连续。
        return body + Math.Max(upper, bottom) >= 130
            || Math.Min(upper, bottom) >= 40 && body + upper + bottom >= 180;
    }

    private static double[] Sample(BmpPixels p, int x, int y, int size, int count = 10, int firstRow = 0, double[]? buffer = null)
    {
        var values = buffer ?? new double[(count - firstRow) * count];
        for (int r = firstRow; r < count; r++) for (int c = 0; c < count; c++)
            values[(r - firstRow) * count + c] = p.Gray(x + (int)((c + .5) * size / count), y + (int)((r + .5) * size / count));
        return values;
    }
    private static void Normalize(double[] a)
    {
        double mean = 0, sum = 0;
        for (int i = 0; i < a.Length; i++) mean += a[i];
        mean /= a.Length;
        for (int i = 0; i < a.Length; i++) { a[i] -= mean; sum += a[i] * a[i]; }
        double norm = Math.Sqrt(sum);
        if (norm > 0) for (int i = 0; i < a.Length; i++) a[i] /= norm;
    }
    private static double Dot(double[] a, double[] b)
    { double s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * b[i]; return s; }

}
