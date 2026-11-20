namespace ASPAssistant.Core.Services;

public sealed record SampleFrame(DateTimeOffset Time, string Step, byte[] Image)
{
    public string Label => $"{Time.LocalDateTime:HH:mm:ss.fff} · {Step}";
    public string Extension => Image is [0x42, 0x4d, ..] ? "bmp" : Image is [0xff, 0xd8, ..] ? "jpg" : "png";
}

public sealed record RecognitionSample(int? Round, string Kind, string Result, DateTimeOffset Started, SampleFrame[] Frames)
{
    public sealed record MarkedCase(string Label, int? Round, string Result, int? InputFrame, string Expected);
    public MarkedCase[] Cases { get; init; } = [];
    public int? InputFrame { get; init; }
    public string[] Actions { get; init; } = [];
    public bool IsGameReport { get; init; }
    public Guid? Game { get; init; }
    public string Label => $"{Started.LocalDateTime:HH:mm:ss} · {(Round is int r ? $"第 {r} 回合 · " : "")}{Kind}";
}

public sealed class RecognitionSampleHistory
{
    private const int MiB = 1024 * 1024;
    internal const int RecentBytes = 24 * MiB, EventBytes = 72 * MiB;
    private readonly object _gate = new();
    private readonly List<SampleFrame> _recent = [];
    private readonly List<(long Id, RecognitionSample Sample)> _events = [];
    private long _sequence;
    private int? _recentRound;

    public void ClearRecent()
    {
        lock (_gate) { _recent.Clear(); _recentRound = null; }
    }

    public void Observe(byte[] image, int? round, DateTimeOffset time)
    {
        if (image.Length == 0 || image.Length > RecognitionDiagnostics.MaximumFrameBytes) return;
        lock (_gate)
        {
            if (_recentRound != round) _recent.Clear();
            _recentRound = round;
            _recent.Add(new(time, "游戏扫描", image));
            while (_recent.Count > 8 || _recent.Sum(f => f.Image.Length) > RecentBytes) _recent.RemoveAt(0);
        }
    }

    public long Begin(string kind, int? round, bool includeRecent = false, Guid? game = null)
    {
        lock (_gate)
        {
            var frames = includeRecent && round == _recentRound ? _recent.TakeLast(3).ToArray() : [];
            var id = ++_sequence;
            _events.Add((id, new RecognitionSample(round, kind, "进行中", DateTimeOffset.Now, frames) { Game = game }));
            Trim();
            return id;
        }
    }

    public void Step(long id, string result, byte[]? image = null)
    {
        lock (_gate)
        {
            int index = _events.FindIndex(e => e.Id == id);
            if (index < 0) return;
            var sample = _events[index].Sample;
            var frames = sample.Frames;
            if (image is { Length: > 0 and <= RecognitionDiagnostics.MaximumFrameBytes })
            {
                var next = new SampleFrame(DateTimeOffset.Now, result, image);
                // Keep the starting view and the newest observations of this action.
                frames = frames.Length < 7 ? [..frames, next] : [frames[0], ..frames[2..], next];
            }
            _events[index] = (id, sample with { Result = result, Frames = frames,
                Actions = [..sample.Actions.TakeLast(19), $"{DateTimeOffset.Now:O} · {result}"] });
            Trim();
        }
    }

    private void Trim()
    {
        while (_events.Count > 12 || _events.Sum(e => e.Sample.Frames.Sum(f => (long)f.Image.Length)) > EventBytes)
        {
            if (_events.Count > 1) { _events.RemoveAt(0); continue; }
            var (id, sample) = _events[0];
            _events[0] = (id, sample with { Frames = [sample.Frames[0], ..sample.Frames[2..]] });
        }
    }

    public RecognitionSample[] Freeze()
    {
        lock (_gate)
        {
            var frozen = _events.AsEnumerable().Reverse().Select(e => e.Sample).ToList();
            if (_recent.Count > 0)
                frozen.Add(new(_recentRound, "最近游戏画面", "按采集顺序排列", _recent[^1].Time, [.._recent]));
            return [..frozen];
        }
    }

    internal RecognitionSample? Event(long id)
    {
        lock (_gate) return _events.FirstOrDefault(e => e.Id == id).Sample;
    }
}
