using System.IO.Compression;
using System.Text.Json;

namespace ASPAssistant.Core.Services;

internal readonly record struct SlotReading(System.Drawing.Rectangle Bounds, string Decision);
public sealed record FieldObservation(Guid Game, SampleFrame Frame, string Map, int Round,
    IReadOnlyList<LiveUnit> Units, IReadOnlyList<string> Bought)
{
    internal IReadOnlyDictionary<FieldSlot,SlotReading> Readings { get; init; } = new Dictionary<FieldSlot,SlotReading>();
    internal string Summary => $"{Map} 第{Round}回合 · 购入[{string.Join("、", Bought)}] · " +
        string.Join("；", Units.Select(u => $"{u.Mark.Slot} #{u.Id} {u.Name ?? "待复核"}（{u.IdentitySource}）"));
}

public static class FieldFeedback
{
    private static readonly object Gate = new();
    private static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HongxueCompanion", "feedback-marks");
    internal const long Capacity = 96L * 1024 * 1024;

    public static void Save(Guid game, RecognitionSample sample) => Save(DirectoryPath, game, sample);

    public static RecognitionSample Mark(LiveRoster roster, FieldSlot slot, string recommendation, RecognitionSample? purchase)
    {
        var current = roster.Observations[^1];
        var unit = roster.Units.FirstOrDefault(u => u.Mark.Slot == slot);
        var result = $"{slot} · {(unit == null ? "显示为空" : $"#{unit.Id} {unit.Name ?? "待复核"} · {unit.IdentitySource}")}";
        var actions = roster.Observations.Select(o => $"{o.Frame.Time:O} · {o.Summary}").ToList();
        for (int i = 1; i < roster.Observations.Length; i++)
        {
            var previous = roster.Observations[i-1];
            var next = roster.Observations[i];
            foreach (var u in next.Units)
            {
                var old = previous.Units.FirstOrDefault(p => p.Id == u.Id);
                if (old == null) actions.Add($"新增 #{u.Id} {u.Name ?? "待复核"} → {u.Mark.Slot} · {u.IdentitySource}");
                else if (old.Mark.Slot != u.Mark.Slot)
                    actions.Add($"沿用 #{u.Id} {u.Name ?? "待复核"} · {old.Mark.Slot} → {u.Mark.Slot} · {u.IdentitySource}");
            }
        }
        actions.Add(current.Readings.TryGetValue(slot, out var read)
            ? $"角标搜索框（原图像素）{read.Bounds} · {read.Decision}" : "此格未进入可部署格扫描范围");
        actions.Add(recommendation);
        var frames = roster.Observations.Select(o => o.Frame).Where(f => f.Image.Length > 0).ToList();
        if (current.Frame.Image.Length == 0) actions.Add("当前原帧超过留样上限，仅保留识别结果");
        if (purchase != null)
        {
            actions.Add($"同回合购买记录：{purchase.Label} · {purchase.Result}");
            actions.AddRange(purchase.Actions);
            foreach (var f in purchase.Frames.Reverse())
                if (frames.All(x => !ReferenceEquals(x.Image,f.Image)) &&
                    frames.Sum(x => (long)x.Image.Length) + f.Image.Length <= Capacity / 2) frames.Add(f);
        }
        var ordered = frames.OrderBy(f => f.Time).ToArray();
        return new(current.Round, $"棋盘标记 · {slot}", result, DateTimeOffset.Now, ordered)
            { Actions = [..actions], InputFrame = current.Frame.Image.Length == 0 ? null : Array.IndexOf(ordered,current.Frame) + 1 };
    }

    internal static void Save(string directory, Guid game, RecognitionSample sample)
    {
        lock (Gate)
        {
            var root = Directory.CreateDirectory(directory);
            var path = Path.Combine(root.FullName, $"{sample.Started.UtcTicks:D19}-{game:N}-{Guid.NewGuid():N}.mark");
            var temp = path + ".tmp";
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
                {
                    using (var metadata = archive.CreateEntry("mark.json").Open())
                        JsonSerializer.Serialize(metadata, sample with { Frames = sample.Frames.Select(f => f with { Image = [] }).ToArray() });
                    for (int i = 0; i < sample.Frames.Length; i++)
                    {
                        using var output = archive.CreateEntry($"frame-{i}", CompressionLevel.NoCompression).Open();
                        output.Write(sample.Frames[i].Image);
                    }
                }
                if (new FileInfo(temp).Length > Capacity) throw new IOException("这次标记超过本地容量上限。");
                File.Move(temp, path);
            }
            finally { File.Delete(temp); }
            long total = 0;
            int count = 0;
            foreach (var mark in root.EnumerateFiles("*.mark").OrderByDescending(f => f.Name, StringComparer.Ordinal))
            {
                total += mark.Length;
                if (++count > 48 || total > Capacity) mark.Delete();
            }
        }
    }

    internal static RecognitionSample[] LoadGames() => LoadGames(DirectoryPath);

    internal static RecognitionSample[] LoadGames(string directory)
    {
        lock (Gate)
        {
            if (!Directory.Exists(directory)) return [];
            var games = new Dictionary<string,List<RecognitionSample>>();
            foreach (var path in Directory.GetFiles(directory, "*.mark").Order(StringComparer.Ordinal))
            {
                try
                {
                    using var archive = ZipFile.OpenRead(path);
                    using var metadata = (archive.GetEntry("mark.json") ?? throw new InvalidDataException("标记说明缺失")).Open();
                    var sample = JsonSerializer.Deserialize<RecognitionSample>(metadata) ?? throw new InvalidDataException("标记说明为空");
                    for (int i = 0; i < sample.Frames.Length; i++)
                    {
                        var entry = archive.GetEntry($"frame-{i}") ?? throw new InvalidDataException("标记画面缺失");
                        if (entry.Length > RecognitionDiagnostics.MaximumFrameBytes) throw new InvalidDataException("标记画面过大");
                        using var input = entry.Open();
                        var image = new byte[entry.Length];
                        input.ReadExactly(image);
                        sample.Frames[i] = sample.Frames[i] with { Image = image };
                    }
                    var parts = Path.GetFileNameWithoutExtension(path).Split('-');
                    if (parts.Length != 3 || !Guid.TryParseExact(parts[1],"N",out _)) throw new InvalidDataException("标记文件名称无效");
                    var key = parts[1];
                    if (!games.TryGetValue(key, out var list)) games.Add(key, list = []);
                    list.Add(sample);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
                { AppLogger.Warn("Feedback", "无法读取一份棋盘标记：" + ex.GetType().Name); }
            }
            return games.Values.OrderByDescending(g => g[^1].Started).Select(g => new RecognitionSample(null,
                $"整局棋盘标记 · {g[0].Started.LocalDateTime:MM-dd} · {g.Count} 条", "可选择标记，补充实际姓名或站位后导出。",
                g[0].Started, g.SelectMany((m,i) => m.Frames.Select(f => f with { Step = $"标记{i+1} · {m.Kind} · {f.Step}" })).ToArray())
                { IsGameReport = true, Cases = g.Select((m,i) => new RecognitionSample.MarkedCase($"标记{i+1} · {m.Label}",m.Round,m.Result,
                    m.InputFrame + g.Take(i).Sum(s => s.Frames.Length), "")).ToArray(),
                    Actions = g.SelectMany((m,i) => new[] { $"标记{i+1} · {m.Label} · {m.Result}" }.Concat(m.Actions)).ToArray() }).ToArray();
        }
    }
}
