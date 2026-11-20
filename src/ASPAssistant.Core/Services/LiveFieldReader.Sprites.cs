using System.Drawing;
using System.Text;

namespace ASPAssistant.Core.Services;

public sealed partial class LiveFieldReader
{
    // Sparse masked samples are expressed relative to the skeletal foot, at a 100-pixel tile.
    private sealed class FieldSpriteLibrary
    {
        private sealed record Pose(string Name, byte[] Samples);
        private readonly Dictionary<int, List<Pose>> _tiers = [];
        public FieldSpriteLibrary(string path)
        {
            using var stream = File.OpenRead(path);
            using var file = new BinaryReader(stream, Encoding.UTF8);
            if (file.ReadInt32() != 0x31465348) throw new InvalidDataException("干员模板版本不符");
            int count = file.ReadUInt16();
            for (int i = 0; i < count; i++)
            {
                string name = Encoding.UTF8.GetString(file.ReadBytes(file.ReadByte()));
                int tier = file.ReadByte(), length = file.ReadByte() * 5;
                var samples = file.ReadBytes(length);
                if (samples.Length != length) throw new EndOfStreamException();
                if (!_tiers.TryGetValue(tier, out var poses)) _tiers[tier] = poses = [];
                poses.Add(new(name, samples));
            }
        }

        public string? Match(BmpPixels pixels, Point foot, double tile, int tier, bool bench, out IReadOnlyDictionary<string,double> result, out int[] patch)
        {
            // Quantise the local crop once. All skins, facing directions and poses reuse its pixels.
            const int width = 161, height = 150;
            patch = new int[height * width];
            int index = 0;
            for (int y = -130; y < 20; y++)
                for (int x = -80; x <= 80; x++)
                    patch[index++] = pixels.Color(Math.Clamp(foot.X + (int)Math.Round((x + (bench ? 5 : 6)) * tile / 100), 0, pixels.Width - 1),
                        Math.Clamp(foot.Y + (int)Math.Round((y + (bench ? 4 : 18)) * tile / 100), 0, pixels.Height - 1));
            var scores = new Dictionary<string, double>();
            result = scores;
            if (!_tiers.TryGetValue(tier, out var poses)) return null;
            foreach (var pose in poses)
            {
                var samples = pose.Samples;
                double best = scores.GetValueOrDefault(pose.Name, double.MaxValue);
                foreach (int flip in Directions)
                    foreach (int dy in Shifts)
                        foreach (int dx in Shifts)
                        {
                            int sum = 0;
                            for (int k = 0; k < samples.Length; k += 5)
                            {
                                int x = (sbyte)samples[k] * flip, y = (sbyte)samples[k+1];
                                int rgb = patch[(130 + y + dy) * width + 80 + x + dx];
                                sum += Math.Abs((rgb >> 16) - samples[k+2]) + Math.Abs(((rgb >> 8) & 255) - samples[k+3])
                                    + Math.Abs((rgb & 255) - samples[k+4]);
                            }
                            best = Math.Min(best, sum / (samples.Length * .6));
                        }
                scores[pose.Name] = best;
            }
            var top = scores.OrderBy(p => p.Value).Take(2).ToArray();
            return top.Length == 2 && top[0].Value < 34 && top[1].Value - top[0].Value >= 5 ? top[0].Key : null;
        }
        internal static readonly int[] Directions = [-1, 1], Shifts = [-4, 0, 4];
    }
}
