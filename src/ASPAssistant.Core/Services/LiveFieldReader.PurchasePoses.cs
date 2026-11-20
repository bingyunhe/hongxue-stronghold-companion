using System.Text.Json;

namespace ASPAssistant.Core.Services;

public sealed partial class LiveFieldReader
{
    private static Dictionary<string,byte[][]> LoadPurchasePoses(string path)
    {
        using var file = File.OpenRead(path);
        using var assets = JsonDocument.Parse(file);
        var poses = new Dictionary<string,byte[][]>();
        foreach (var skin in assets.RootElement.EnumerateArray().GroupBy(s => s.GetProperty("operator").GetString()!))
        {
            var frames = skin.SelectMany(s => s.GetProperty("poses").EnumerateObject()).Select(p => p.Value.GetBytesFromBase64()).ToArray();
            if (frames.Any(f => f.Length < 40 || f.Length % 5 != 0))
                throw new InvalidDataException("干员姿态采样点格式错误");
            foreach (var frame in frames)
                for (int i=0;i<frame.Length;i+=5)
                    if (Math.Abs((int)(sbyte)frame[i])>74 || (sbyte)frame[i+1] is < -126 or > 3)
                        throw new InvalidDataException("干员姿态采样点越出棋格截图范围");
            poses.Add(skin.Key, frames);
        }
        return poses;
    }

    private static double PoseError(int[] patch, bool bench, byte[][] frames, int stride)
    {
        double best = double.MaxValue;
        foreach (var pose in frames)
        {
            int count = (pose.Length + stride - 1) / stride;
            foreach (int flip in FieldSpriteLibrary.Directions)
            foreach (int dy in FieldSpriteLibrary.Shifts)
            foreach (int dx in FieldSpriteLibrary.Shifts)
            {
                // Reuse the foot-aligned crop from the sparse matcher; its board origin is (+6,+18).
                int origin = (130 + (bench ? 0 : 12) + dy) * 161 + 80 + (bench ? 0 : -2) + dx;
                int sum = 0;
                for (int i=0; i<pose.Length; i+=stride)
                {
                    int rgb = patch[origin + (sbyte)pose[i+1]*161 + flip*(sbyte)pose[i]];
                    sum += Math.Abs((rgb>>16)-pose[i+2]) + Math.Abs(((rgb>>8)&255)-pose[i+3]) + Math.Abs((rgb&255)-pose[i+4]);
                }
                best = Math.Min(best, sum / (3d*count));
            }
        }
        return best;
    }

    internal void SamplePurchasePose(BmpPixels pixels, FieldMark mark, string name)
    {
        if (!_purchasePoses.Value.TryGetValue(name, out var frames)) return;
        // The Spine origin is at ground level. The badge-derived anchor changes when the operator is on the bench.
        int originX = mark.Slot.Bench ? 5 : 4, originY = mark.Slot.Bench ? 4 : 30;
        double scale = mark.Tile / 100;
        double best = double.MaxValue;
        foreach (var pose in frames)
        foreach (int flip in FieldSpriteLibrary.Directions)
        foreach (int dy in FieldSpriteLibrary.Shifts)
        foreach (int dx in FieldSpriteLibrary.Shifts)
        {
            int sum = 0;
            for (int i = 0; i < pose.Length; i += 5)
            {
                int x = mark.Foot.X + (int)Math.Round((originX + dx + flip * (sbyte)pose[i]) * scale);
                int y = mark.Foot.Y + (int)Math.Round((originY + dy + (sbyte)pose[i+1]) * scale);
                int color = pixels.Color(Math.Clamp(x, 0, pixels.Width - 1), Math.Clamp(y, 0, pixels.Height - 1));
                sum += Math.Abs((color >> 16) - pose[i+2]) + Math.Abs(((color >> 8) & 255) - pose[i+3]) + Math.Abs((color & 255) - pose[i+4]);
            }
            best = Math.Min(best, sum / (pose.Length * .6));
        }
        (mark.PurchasePoseScores ??= [])[name] = best;
    }
}
