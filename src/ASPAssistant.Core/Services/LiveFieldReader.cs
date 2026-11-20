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
    internal Dictionary<string,double>? PurchasePoseScores { get; set; }
}
internal sealed record FieldBadge(double X, double Y, int Tier, double Score, double Size);
public sealed partial class LiveFieldReader
{
    private readonly List<(int Tier, int Size, double[] Values, double[] Fine, double[] Lower, double[] LowerFine)> _templates = [];
    private readonly GarrisonBoardData _maps;
    private readonly Lazy<FieldSpriteLibrary> _sprites;
    private readonly Lazy<Dictionary<string,byte[][]>> _purchasePoses;
    public LiveFieldReader(string directory, GarrisonBoardData maps)
    {
        _maps = maps;
        _purchasePoses = new(() => LoadPurchasePoses(Path.Combine(directory, "template", "field-purchase-poses.json")));
        _sprites = new(() => new FieldSpriteLibrary(Path.Combine(directory, "template", "field-sprites.bin")));
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
    {
        var marks = new List<FieldMark>();
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
        for (int col = 0; col < 10; col++)
            Inspect(new(col, -1), (expanded ? 254 + col * 121.2 : 170 + col * 149.5) * scale,
                (expanded ? 609 : 798) * frame.Height / 1080d, (expanded ? 121.2 : 149.5) * scale);
        cancellation.ThrowIfCancellationRequested();
        if (identify) { _previous = marks.ToDictionary(m => m.Slot); _map = board.Map.Id; }
        return marks;

        void Inspect(FieldSlot slot, double x, double y, double tile)
        {
            cancellation.ThrowIfCancellationRequested();
            var badge = ReadBadge(frame, frame.Width, frame.Height, x, y, !slot.Bench, expanded);
            if (badge is null || badge.X + 90 * scale >= frame.Width || badge.Y + 151 * scale >= frame.Height ||
                slot.Bench && HasSummonCount(frame, badge, scale)) return;
            var foot = new Point((int)(badge.X + 35 * scale), (int)(badge.Y + (slot.Bench ? 132 : 112) * scale));
            bool body = HasUnitEvidence(frame, badge, frame.Width, frame.Height, !slot.Bench);
            bool obscured = !body || badge.Tier == 4 && badge.Score < .73;
            if (obscured && badge.Tier != 1 && (slot.Bench || badge.Tier != 4)) return;
            if (!slot.Bench && badge.Tier == 4 && IsPhantom(frame, foot, tile)) return;
            var appearance = new byte[5 * 6 * 3];
            for (int r = 0; r < 6; r++)
                for (int c = 0; c < 5; c++)
                {
                    var color = frame.Color(Math.Clamp(foot.X + (int)((c - 2) * .075 * tile), 0, frame.Width - 1),
                        Math.Clamp(foot.Y - (int)((.28 + .075 * r) * tile), 0, frame.Height - 1));
                    int i = (r * 5 + c) * 3;
                    appearance[i] = (byte)(color >> 16); appearance[i+1] = (byte)(color >> 8); appearance[i+2] = (byte)color;
                }
            if (identify && !force && _map == board.Map.Id && _previous.TryGetValue(slot, out var prior) &&
                prior.Tier == badge.Tier && LiveFieldState.Difference(prior.Appearance, appearance) < 5 &&
                (!obscured || prior.SpriteName is string known && prior.Scores![known] <= 23))
            {
                marks.Add(prior with { Foot=foot, Appearance=appearance, Tile=tile });
                return;
            }
            // Sample the center of the head and torso, away from neighboring weapons and floor edges.
            var portrait = new byte[7*8*3];
            for (int row=0; row<8; row++)
                for (int col=0; col<7; col++)
                {
                    int px=Math.Clamp(foot.X+(int)Math.Round((col-3)*.05*tile),0,frame.Width-1);
                    int py=Math.Clamp(foot.Y-(int)Math.Round((.31+row*.078)*tile),0,frame.Height-1);
                    int rgb=frame.Color(px,py), i=(row*7+col)*3;
                    portrait[i]=(byte)(rgb>>16); portrait[i+1]=(byte)(rgb>>8); portrait[i+2]=(byte)rgb;
                }
            if (!identify && !obscured)
            {
                marks.Add(new(slot,badge.Tier,foot,appearance,null) { Tile=tile, Portrait=portrait });
                return;
            }
            var name = _sprites.Value.Match(frame,foot,tile,badge.Tier,slot.Bench,out var scores,out var patch);
            if (obscured && (name == null || scores[name] > 23)) return;
            if (!identify)
            {
                marks.Add(new(slot,badge.Tier,foot,appearance,null) { Tile=tile, Portrait=portrait });
                return;
            }
            var mark = new FieldMark(slot,badge.Tier,foot,appearance,name) { Scores=scores, Tile=tile, Portrait=portrait };
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
        }
    }
    private FieldBadge? ReadBadge(BmpPixels px, int width, int height, double cx, double cy, bool board, bool expanded)
    {
        double scale = width / 1920d;
        // 展开商店时第一排角标与盟约栏重叠，只比较角标下半部。
        bool lower = board && cy < 180 * scale;
        var values = new double[lower ? 50 : 100];
        var fine = new double[lower ? 200 : 400];
        FieldBadge? best = null;
        foreach (int size in new[] { 34, 40, 44 })
        {
            var templates = _templates.Where(t => t.Size == size).ToArray();
            int actualSize = Math.Max(10, (int)Math.Round(size * scale));
            int radius = (int)Math.Ceiling((board ? 9 : 12) * scale);
            int horizontal = (int)Math.Ceiling((!board && expanded ? 18 : 12) * scale);
            // 横向扩展按完整的两像素步长进行，保留原搜索窗的粗采样位置。
            horizontal += (horizontal - radius) % 2;
            int left = (int)Math.Round(cx) - actualSize / 2;
            int top = (int)Math.Round(cy) - actualSize / 2;
            // Search the full vertical interval between the normal and elevated badge.
            int step = board ? 2 : 1;
            int firstY = (int)Math.Round(cy - (board ? 26 * scale : expanded ? 20 * scale : 0)) - actualSize / 2 - radius;
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
