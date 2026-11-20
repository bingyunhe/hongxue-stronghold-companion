using System.Diagnostics;
using System.Drawing;
using ASPAssistant.Core.Interop;
using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Services;

public partial class OcrScannerService
{
    public LiveFieldReader? LiveReader { get; init; }
    public IReadOnlyDictionary<string, int> FieldTiers { get; init; } = new Dictionary<string,int>();
    private LiveFieldState? _liveState;
    private (BmpPixels Frame, BoardMatch Board, LiveRoster Roster, IReadOnlyList<FieldMark> Marks)? _reviewBoard;
    private int _manualField;
    private int _fieldOpeningRevision = -1;

    // On the first opening animation, the funds badge is drawn before the card names.
    // Reacquire just this transition before paying for OCR on an unreadable first frame.
    private static bool OpeningWithoutCards(BmpPixels p)
    {
        int gold = 0, light = 0;
        for (int row = 0; row < 16; row++)
            for (int col = 0; col < 48; col++)
            {
                int color = p.Color((int)(p.Width*(.87+col*.0022)),(int)(p.Height*(.73+row*.004)));
                int r=color>>16, g=(color>>8)&255, b=color&255;
                if (r>145 && g>85 && r>g+27 && g>b*1.5) gold++;
            }
        if (gold < 25) return false;
        for (int row=0;row<8;row++)
            for (int col=0;col<40;col++)
            {
                int color=p.Color((int)(p.Width*(.42+col*.01)),(int)(p.Height*(.91+row*.008)));
                if ((color>>16)>140 && ((color>>8)&255)>140 && (color&255)>140) light++;
            }
        return light < 10;
    }
    public event Action? LiveFieldChanged;
    public LiveRoster LiveRoster { get { lock (_runLock) return _liveState?.Roster ?? new(null,null,[]); } }

    public bool SetLiveIdentity(long id, FieldSlot slot, string name)
    {
        bool applied;
        lock (_runLock) applied = _liveState?.Confirm(id, slot, name, true) == true;
        if (applied) LiveFieldChanged?.Invoke();
        return applied;
    }

    private void ObserveField(BmpPixels pixels, BoardMatch match, int round, IReadOnlyList<ShopItem> cards,
        ShopEconomy? economy, bool open, long tick, int run, GameConnectionService? connection, long revision, bool manual, CancellationToken cancellation = default)
    {
        var clock = Stopwatch.StartNew();
        if (FieldDetailFocus.Visible(pixels)) return;
        var marks = LiveReader!.Read(pixels, match, true, manual, cancellation);
        bool changed;
        lock (_runLock)
        {
            if (cancellation.IsCancellationRequested || run != _runRevision || !IsCurrentCapture(connection, revision)) return;
            _liveState ??= new(FieldTiers);
            var before = _liveState.Roster;
            if (!manual) _liveState.Shop(pixels, cards, economy, open, tick);
            _liveState.Observe(match.Map.Id, round, marks, tick, (mark, name) => LiveReader.SamplePurchasePose(pixels, mark, name));
            var after = _liveState.Roster;
            _reviewBoard = (pixels, match, after, marks);
            changed = !before.Bought.SequenceEqual(after.Bought) || before.Map != after.Map || before.Round != round || before.Units.Count != after.Units.Count ||
                after.Units.Any(u => !before.Units.Any(p => p.Id == u.Id && p.Mark.Slot == u.Mark.Slot && p.Name == u.Name && p.IdentitySource == u.IdentitySource));
        }
        if (changed || manual)
        {
            AppLogger.Info("LiveField",$"round={round} cells={marks.Count} manual={manual} recognition={clock.ElapsedMilliseconds}ms capture-to-notify={Environment.TickCount64-tick}ms");
            LiveFieldChanged?.Invoke();
        }
    }

    public async Task<string> ReadLiveFieldAsync(LiveUnit? target = null)
    {
        if (Interlocked.CompareExchange(ref _manualField, 1, 0) != 0) return "正在识别阵容";
        using var deadline = new CancellationTokenSource();
        if (target is null) deadline.CancelAfter(ManualRecognitionLimit);
        var token = deadline.Token;
        try
        {
            return await Task.Run(() => ReadLiveFieldCoreAsync(target, token), token).WaitAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return "识别阵容已超时（8 秒），可重新识别。";
        }
        finally { Interlocked.Exchange(ref _manualField, 0); }
    }

    internal static readonly TimeSpan ManualRecognitionLimit = TimeSpan.FromSeconds(8);

    private async Task<string> ReadLiveFieldCoreAsync(LiveUnit? target, CancellationToken token)
    {
        bool acquired = false;
        try
        {
            var connection = _getConnection?.Invoke();
            if (connection is null || LiveReader is null || EconomyReader is null || BoardRecognizer is null) return "请先连接游戏";
            var revision = connection.CaptureRevision;
            int run = RunRevision;
            bool Current() => !token.IsCancellationRequested && !_disposed && !_pauseBanScreenDetection && !_banScreenActive &&
                run == RunRevision && IsCurrentCapture(connection, revision);
            if (!connection.IsAdb)
            {
                var game = connection.FindGameWindow();
                var foreground = User32.GetForegroundWindow();
                User32.GetWindowThreadProcessId(foreground, out var pid);
                if (game == IntPtr.Zero || foreground != game && (pid != Environment.ProcessId || !User32.SetForegroundWindow(game)))
                    return "请回到游戏的休整画面";
            }
            var timer = Stopwatch.StartNew();
            while (!(acquired = Interlocked.CompareExchange(ref _scanning, 1, 0) == 0))
            {
                token.ThrowIfCancellationRequested();
                if (!Current() || target is not null && timer.ElapsedMilliseconds > 6000) return "当前识别仍在进行，请稍后重试";
                await Task.Delay(35, token);
            }
            if (!Current() || _captureScreen() is not { } image) return "未取得当前游戏画面";
            token.ThrowIfCancellationRequested();
            var size = ImageBytes.GetDimensions(image);
            var pixels = BmpPixels.TryRead(ImageBytes.ToBmp(image))!;
            bool detailOpen = FieldDetailFocus.Visible(pixels);
            var hud = await EconomyReader.ReadPreparationAsync(image, size.Width, size.Height);
            token.ThrowIfCancellationRequested();
            if (!hud.IsPreparation || hud.Round is not int round || !Current()) return "请在休整阶段识别阵容";
            if (target == null)
            {
                if (detailOpen) return "请先收起干员详情，再识别全场";
                var board = BoardRecognizer.Recognize(pixels);
                if (board == null || LiveFieldReader.IsDragging(pixels)) return "请回到可见完整棋盘的休整画面";
                ObserveField(pixels, board, round, [], null, false, Environment.TickCount64, run, connection, revision, true, token);
                token.ThrowIfCancellationRequested();
                var roster = LiveRoster;
                if (!Current() || roster.Map != board.Map.Id || roster.Round != round) return "对局已切换，请重新识别";
                var units = roster.Units;
                int bench = units.Count(u => u.Mark.Slot.Bench);
                var pending = units.Where(u => u.Name == null).Select(u => u.Mark.Slot.ToString()).ToArray();
                var summary = $"上场 {units.Count-bench} 人 · 整备区 {bench} 人 · 已认出 {units.Count-pending.Length} 人";
                if (pending.Length > 0) summary += $" · {pending.Length} 人待复核（{string.Join("、",pending)}）";
                AppLogger.Info("LiveField",$"round={round} 阵容识别：{summary}");
                return summary;
            }
            (BmpPixels Frame, BoardMatch Board, LiveRoster Roster, IReadOnlyList<FieldMark> Marks)? reference;
            lock (_runLock) reference = _reviewBoard;
            if (reference is not { } normal || normal.Roster.Round != round ||
                normal.Frame.Width != size.Width || normal.Frame.Height != size.Height || !Current()) return "请先返回当前棋盘再复核";
            var unit = normal.Roster.Units.FirstOrDefault(u=>u.Id==target.Id && u.Mark.Slot==target.Mark.Slot);
            if (unit == null) return "该干员已移动，请重新选择";
            FieldDetailFocus focus;
            FieldMark? beforeClick=normal.Marks.FirstOrDefault(m=>m.Slot==unit.Mark.Slot);
            // Pick a click point only from a fresh frame in the camera that is actually visible.
            if (_captureScreen() is not { } before || ImageBytes.GetDimensions(before) != size || !Current())
                return "游戏画面已切换，请重新选择";
            var samples = RecognitionDiagnostics.Samples;
            var review = samples.Begin($"干员复核 · {unit.Mark.Slot}", round);
            samples.Step(review, "点击前", before);
            var freshPixels=BmpPixels.TryRead(ImageBytes.ToBmp(before))!;
            Point? click=null;
            if (FieldDetailFocus.Visible(freshPixels))
            {
                focus=new FieldDetailFocus(normal.Frame,normal.Board.Camera,normal.Marks);
                var selected=focus.Locate(freshPixels);
                if(selected==null) return Unresolved("未能确认详情对应的格子，请收起详情重试，或右键手动更换");
                if(selected.Value.Mark.Slot!=unit.Mark.Slot)
                {
                    var other=selected.Value.Mark.Foot;
                    var destination=normal.Marks.FirstOrDefault(m=>m.Slot==unit.Mark.Slot && m.Tier==unit.Mark.Tier);
                    if(destination==null) return Unresolved($"{unit.Mark.Slot} · 未检测到对应的占用角标");
                    double zoom=selected.Value.Zoom;
                    int x=(int)(size.Width*.5+(destination.Foot.X-other.X)*zoom);
                    int y=(int)(size.Height*selected.Value.CenterY+(destination.Foot.Y-other.Y)*zoom-size.Width*.021);
                    // In the open detail camera only use the unobstructed band below the action buttons.
                    if (x<size.Width*.40 || x>size.Width*.81 || y<size.Height*.61 || y>size.Height*.75)
                        return "目标被详情控件遮挡，请先收起详情再复核";
                    click=new(x,y);
                }
            }
            else
            {
                var same=BoardRecognizer.Recognize(freshPixels);
                if (same?.Map.Id != normal.Board.Map.Id ||
                    LiveFieldReader.IsDragging(freshPixels)) return "画面已切换，请重新选择";
                var marks=LiveReader.Read(freshPixels,same,false);
                var current=marks.FirstOrDefault(m=>m.Slot==unit.Mark.Slot && m.Tier==unit.Mark.Tier);
                if(current==null) return Unresolved($"{unit.Mark.Slot} · 未检测到对应的占用角标");
                focus=new FieldDetailFocus(freshPixels,same.Camera,marks);
                lock (_runLock)
                {
                    if (!Current()) return "对局已切换";
                    _reviewBoard=(freshPixels,same,normal.Roster,marks);
                }
                beforeClick=current;
                // Aim inside the torso. An adjacent melee sprite or a summon can cover the lower edge.
                click=current.Slot.Bench ? new(current.Foot.X,current.Foot.Y-(int)Math.Round(current.Tile*.15))
                    : new(current.Foot.X+(int)Math.Round(current.Tile*.06),current.Foot.Y-(int)Math.Round(current.Tile*.30));
            }
            if (click is Point point && (!Current() || SendCapturedClick(connection, point, new Size(size.Width,size.Height), revision) != ClickResult.Sent))
            {
                samples.Step(review, $"游戏未接收点击 {click}");
                return "游戏未接收复核点击";
            }
            samples.Step(review, $"目标 {unit.Mark.Slot} · 点击 {click} · 画面 {size.Width}×{size.Height}");
            AppLogger.Info("LiveField",$"review slot={unit.Mark.Slot} click={click.HasValue} point={click} frame={size.Width}x{size.Height} detailWasOpen={detailOpen}");
            string? last = null;
            string reason = "capture-changed";
            for (int attempt = 0; attempt < 4 && Current(); attempt++)
            {
                await Task.Delay(120);
                if (_captureScreen() is not { } fresh || ImageBytes.GetDimensions(fresh) != size) break;
                var freshFrame=BmpPixels.TryRead(ImageBytes.ToBmp(fresh))!;
                bool visible = FieldDetailFocus.Visible(freshFrame);
                var selectedSlot = visible ? focus.Locate(freshFrame)?.Mark.Slot : null;
                if (selectedSlot != unit.Mark.Slot)
                {
                    reason = !visible ? "panel-hidden" : $"focus={selectedSlot?.ToString() ?? "unknown"}";
                    samples.Step(review, $"目标 {unit.Mark.Slot} · {reason}", fresh);
                    last=null;
                    continue;
                }
                var name = await DetailName(fresh);
                reason = name == null ? "name-unreadable" : "name-awaiting-agreement";
                samples.Step(review, $"选中 {selectedSlot} · 姓名 {name ?? "未读出"}", fresh);
                if (name != null && name == last)
                {
                    var checkedHud = await EconomyReader.ReadPreparationAsync(fresh,size.Width,size.Height);
                    if (!checkedHud.IsPreparation || checkedHud.Round != round) return "回合或阶段已变化，请重新选择";
                    string result;
                    lock (_runLock)
                    {
                        if (!Current()) return "对局已切换";
                        var saved = _liveState?.Roster.Units.FirstOrDefault(u=>u.Id==unit.Id && u.Mark.Slot==unit.Mark.Slot);
                        if (saved == null) return "该干员已移动";
                        if (saved.Manual && saved.Name != name) return $"详情为{name} · 已保留手动记录{saved.Name}";
                        result = saved.Manual ? $"详情与手动记录一致 · {name}" : $"{unit.Mark.Slot} · 已核实为{name}";
                        if (!saved.Manual) _liveState!.Confirm(unit.Id,unit.Mark.Slot,name,false,beforeClick);
                    }
                    LiveFieldChanged?.Invoke();
                    AppLogger.Info("LiveField",$"review slot={unit.Mark.Slot} detail={name}");
                    result += await ReturnToBoard();
                    samples.Step(review, result);
                    return result;
                }
                last = name;
            }
            AppLogger.Info("LiveField",$"review slot={unit.Mark.Slot} reason={reason}");
            return Unresolved("详情未能核实，可右键选择「更换干员并记住」");

            async Task<string> ReturnToBoard()
            {
                if (!Current() || _captureScreen() is not { } latest || ImageBytes.GetDimensions(latest) != size) return "";
                var panel = BmpPixels.TryRead(ImageBytes.ToBmp(latest))!;
                if (!FieldDetailFocus.Visible(panel) || focus.Locate(panel)?.Mark.Slot != unit.Mark.Slot) return "";
                var closingHud = await EconomyReader.ReadPreparationAsync(latest,size.Width,size.Height);
                if (!closingHud.IsPreparation || closingHud.Round != round || !Current()) return "";
                // Unoccupied background between the round HUD and the ready button, above the detail actions.
                var background = new Point((int)(size.Width*.76),(int)(size.Height*.12));
                if (SendCapturedClick(connection,background,new Size(size.Width,size.Height),revision) != ClickResult.Sent)
                    return " · 请手动收起详情";
                for (int i=0; i<2; i++)
                {
                    await Task.Delay(150);
                    if (!Current() || _captureScreen() is not { } after || ImageBytes.GetDimensions(after) != size) return "";
                    var screen = BmpPixels.TryRead(ImageBytes.ToBmp(after))!;
                    samples.Step(review, "收起详情后", after);
                    if (!FieldDetailFocus.Visible(screen))
                        return BoardRecognizer.Recognize(screen)?.Map.Id == normal.Board.Map.Id ? " · 已返回棋盘" : "";
                }
                return " · 请手动收起详情";
            }

            string Unresolved(string message)
            {
                lock (_runLock)
                {
                    if (!Current()) return "对局已切换，请重新识别";
                    _liveState?.ReviewFailed(unit.Id,unit.Mark.Slot);
                }
                samples.Step(review, message);
                AppLogger.Info("LiveField",$"review slot={unit.Mark.Slot} unresolved={message}");
                LiveFieldChanged?.Invoke();
                return message;
            }
        }
        finally
        {
            if (acquired) Interlocked.Exchange(ref _scanning, 0);
        }
    }

    private async Task<string?> DetailName(byte[] frame)
    {
        var (w,h) = ImageBytes.GetDimensions(frame);
        // The name is the single row between the class icon and the level and stats.
        var lines = await _ocrEngine.RecognizeRegionAsync(frame, (int)(w*.005), (int)(h*.247), (int)(w*.22), (int)(h*.054));
        return lines.Select(l => l.Text.Trim().Replace(" ", "").Replace('傑', '杰')).FirstOrDefault(FieldTiers.ContainsKey);
    }
}
