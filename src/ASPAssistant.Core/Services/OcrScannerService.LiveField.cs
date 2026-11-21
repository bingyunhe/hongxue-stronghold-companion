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
    private bool _automaticLiveField;
    public bool AutomaticLiveField
    {
        get { lock (_runLock) return _automaticLiveField; }
        set { lock (_runLock) _automaticLiveField = value; }
    }
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
    public event Action<FieldOwnership>? FieldOwnershipChanged;

    private long _ownershipSequence;
    private int _boardRevision;
    private bool _personalRoster;
    private LiveFieldState? _otherViewState;
    public (Guid Game, int? Round) OwnershipContext { get { lock (_runLock) return (_fieldGame, _economy.Current?.Round ?? _liveState?.Roster.Round); } }

    public bool IsCurrentOwnership(FieldObservation frame)
    {
        lock (_runLock) return frame.Game == _fieldGame && frame.RunRevision == _runRevision &&
            frame.BoardRevision == _boardRevision && IsCurrentCapture(frame.Connection, frame.CaptureRevision);
    }

    // Called while holding _runLock so the action and the observed units refer to the same board.
    private FieldOwnership? Ownership(long? track = null, string? previous = null, string? sold = null) =>
        _fieldObservations.LastOrDefault() is { PersonalBoard: true } frame && _liveState != null
            ? new FieldOwnership(frame, _liveState.Roster.Units, track, previous, sold) { Sequence = ++_ownershipSequence } : null;

    private void PublishField(FieldOwnership? ownership)
    {
        if (ownership != null) FieldOwnershipChanged?.Invoke(ownership);
        LiveFieldChanged?.Invoke();
    }
    internal Action<Guid, RecognitionSample> SaveFieldReview { get; init; } = FieldFeedback.Save;
    private Guid _fieldGame = Guid.NewGuid();
    private FieldObservation[] _fieldObservations = [];
    private (FieldObservation Scan,LiveRoster Roster,long Track,string[] Names)? _reviewNames;
    public LiveRoster LiveRoster { get { lock (_runLock) return _liveState is null ? new(null,null,[]) : _liveState.Roster with { Observations = _fieldObservations }; } }

    public string[] GetLiveReviewCandidates(LiveUnit target)
    {
        if (target.Name != null) return [];
        BmpPixels? frame;
        BoardMatch? board;
        byte[] original;
        string[] shapeNames;
        FieldObservation scan;
        LiveRoster roster;
        FieldMark mark;
        HashSet<string> known;
        lock (_runLock)
        {
            if (_reviewBoard is not { } snapshot || _fieldObservations.Length == 0 ||
                _liveState?.Roster.Units.FirstOrDefault(u => u.Id == target.Id && u.Mark.Slot == target.Mark.Slot)
                    is not { Name: null } current || !ReferenceEquals(current.Mark,target.Mark)) return [];
            scan=_fieldObservations[^1];
            roster=_liveState.Roster;
            if (_reviewNames is {} cached && cached.Track==target.Id && ReferenceEquals(cached.Scan,scan) &&
                ReferenceEquals(cached.Roster,roster)) return cached.Names;
            mark=current.Mark;
            known=_liveState.KnownReviewNames();
            shapeNames=_liveState.ReviewShapeCandidates(mark);
            // A detail retry may have moved the camera since the roster was read.
            frame = snapshot.Marks.Any(m => ReferenceEquals(m, current.Mark)) ? snapshot.Frame : null;
            board = snapshot.Board;
            original = scan.Frame.Image;
        }
        if (frame is null)
        {
            if (original.Length>0)
            {
                frame = BmpPixels.TryRead(ImageBytes.ToBmp(original));
                board = frame==null ? null : BoardRecognizer!.Recognize(frame);
            }
        }
        var heads=frame==null || board==null ? [] : LiveReader!.RankReviewCandidates(frame,board,mark.Slot);
        var parts=frame==null || board?.Map.Id!=scan.Map ? [] : LiveReader!.ReviewVisibleParts(frame,mark,known);
        string? visible = null;
        if (heads.Length < 3 && frame != null && board?.Map.Id == scan.Map)
        {
            scan.Readings.TryGetValue(mark.Slot, out var reading);
            visible = LiveReader!.ReviewVisibleBody(frame, mark, reading.BadgeTier, scan.Units.Select(u => u.Mark.Slot));
        }
        // Reserve room for the original whole-body runner even when the two parts disagree.
        var names=heads.Concat(visible == null ? [] : new[]{visible}).Concat(parts.Take(1)).Concat(shapeNames.Take(1)).Concat(parts.Skip(1))
            .Concat(shapeNames).Distinct().Take(3).ToArray();
        lock (_runLock)
        {
            if (!ReferenceEquals(scan,_fieldObservations.LastOrDefault()) || !ReferenceEquals(roster,_liveState?.Roster)) return [];
            _reviewNames=(scan,roster,target.Id,names);
            return names;
        }
    }

    public bool ConfirmLiveReviewName(LiveUnit target,string name)
    {
        FieldOwnership? ownership;
        lock (_runLock)
        {
            if (_reviewNames is not {} candidates || candidates.Track!=target.Id ||
                !ReferenceEquals(_fieldObservations.LastOrDefault(),candidates.Scan) || !ReferenceEquals(_liveState?.Roster,candidates.Roster) ||
                !candidates.Roster.Units.Any(u=>u.Id==target.Id && ReferenceEquals(u.Mark,target.Mark)) ||
                !candidates.Names.Contains(name) || !_liveState!.ConfirmReviewName(target.Id,target.Mark.Slot,name)) return false;
            ownership = Ownership(target.Id, target.Name);
        }
        PublishField(ownership);
        return true;
    }

    public bool SetLiveIdentity(LiveUnit target, string name)
    {
        FieldOwnership? ownership;
        lock (_runLock)
        {
            var current = _liveState?.Roster.Units.FirstOrDefault(u => u.Id == target.Id && ReferenceEquals(u.Mark, target.Mark));
            if (current == null || !_liveState!.Confirm(current.Id, current.Mark.Slot, name, true)) return false;
            ownership = Ownership(current.Id, current.Name);
        }
        PublishField(ownership);
        return true;
    }

    public bool ConfirmLiveSuggestion(LiveUnit target)
    {
        FieldOwnership? ownership;
        lock (_runLock)
        {
            if (target.Suggestion is not {} hint || _liveState?.Roster.Units.Any(u => u.Id == target.Id &&
                ReferenceEquals(u.Mark, target.Mark)) != true || !_liveState.AcceptSuggestion(target.Id,target.Mark.Slot,hint.Id)) return false;
            ownership = Ownership(target.Id, target.Name);
        }
        PublishField(ownership);
        return true;
    }

    public void ForgetLiveMember(long memberId, LiveRoster displayed)
    {
        FieldOwnership? ownership;
        lock (_runLock)
        {
            if (_liveState == null || displayed.Observations.LastOrDefault() is not { } displayedFrame ||
                !IsCurrentOwnership(displayedFrame) || displayedFrame.PersonalRoster != _personalRoster) return;
            var roster = _liveState.Roster;
            var unit = roster.Units.FirstOrDefault(u => u.MemberId == memberId);
            var name = unit?.Name ??
                roster.Unlocated.FirstOrDefault(m => m.Id == memberId)?.Name;
            if (name == null || !_liveState.Forget(memberId)) return;
            ownership = Ownership(track: unit?.Id, sold: name);
        }
        PublishField(ownership);
    }

    public void LimitLiveMembers(string name,int count)
    {
        bool removed;
        lock (_runLock) removed = _liveState?.LimitMembers(name,count) == true;
        if (removed) LiveFieldChanged?.Invoke();
    }

    private void ObserveField(byte[] image, BmpPixels pixels, BoardMatch match, int round, IReadOnlyList<ShopItem> cards,
        ShopEconomy? economy, bool open, long tick, int run, GameConnectionService? connection, long revision, bool manual, string? mode, bool freshMode, bool declaredOwn, CancellationToken cancellation = default)
    {
        var clock = Stopwatch.StartNew();
        if (FieldDetailFocus.Visible(pixels)) return;
        var readings = new Dictionary<FieldSlot, SlotReading>();
        var marks = LiveReader!.ReadWithTrace(pixels, match, true, manual, cancellation, readings);
        bool changed;
        FieldOwnership? ownership;
        lock (_runLock)
        {
            if ((!manual && !_automaticLiveField) || cancellation.IsCancellationRequested ||
                run != _runRevision || !IsCurrentCapture(connection, revision)) return;
            bool solo = mode == "入门协议" || mode?.StartsWith("规程等级", StringComparison.Ordinal) == true;
            bool personal = solo || declaredOwn;
            if (_fieldObservations.LastOrDefault() is { } prior)
            {
                if (!ReferenceEquals(prior.Connection, connection) || prior.CaptureRevision != revision)
                {
                    _boardRevision++;
                    _liveState = _otherViewState = null;
                }
                else if (_personalRoster != personal)
                    (_liveState, _otherViewState) = (_otherViewState, _liveState);
            }
            _personalRoster = personal;
            _liveState ??= new(FieldTiers);
            var before = _liveState.Roster;
            if (!manual) _liveState.Shop(pixels, cards, economy, open, tick);
            _liveState.Observe(match.Map.Id, round, marks, tick, (mark, name) => LiveReader.SamplePurchasePose(pixels, mark, name));
            var after = _liveState.Roster;
            _reviewBoard = (pixels, match, after, marks);
            changed = !before.Bought.SequenceEqual(after.Bought) || before.Map != after.Map || before.Round != round || before.Units.Count != after.Units.Count ||
                !before.Unlocated.SequenceEqual(after.Unlocated) || after.Units.Any(u => !before.Units.Any(p =>
                    p.Id == u.Id && p.Mark.Slot == u.Mark.Slot && p.Name == u.Name && p.IdentitySource == u.IdentitySource && p.Suggestion == u.Suggestion));
            var frame = new SampleFrame(DateTimeOffset.UtcNow.AddMilliseconds(tick - Environment.TickCount64), $"识别原帧 {pixels.Width}×{pixels.Height} · {match.Camera}",
                image.Length <= RecognitionDiagnostics.MaximumFrameBytes ? image : []);
            bool minuteChanged = _fieldObservations.Length == 0 ||
                _fieldObservations[^1].Frame.Time.ToUnixTimeSeconds() / 60 != frame.Time.ToUnixTimeSeconds() / 60;
            var observed = new FieldObservation(_fieldGame, frame, match.Map.Id, round, after.Units, after.Bought) {
                PersonalBoard = declaredOwn || solo && freshMode, PersonalRoster = personal, RunRevision = run, CaptureRevision = revision, Connection = connection, BoardRevision = _boardRevision,
                Readings = readings, UncertainSlots = readings.Where(p => p.Value.Decision == SlotReading.Uncertain).Select(p => p.Key).ToArray()
            };
            changed |= _fieldObservations.Length > 0 && (!_fieldObservations[^1].UncertainSlots.SequenceEqual(observed.UncertainSlots) ||
                observed.PersonalBoard && !_fieldObservations[^1].PersonalBoard);
            // An unrecognised arrival may leave the roster unchanged. Keep that scan as the
            // latest evidence without displacing previous purchase/movement transitions.
            _fieldObservations = changed || _fieldObservations.Length == 0
                ? [.._fieldObservations.TakeLast(2), observed] : [.._fieldObservations[..^1], observed];
            changed |= minuteChanged;
            ownership = changed || manual ? Ownership() : null;
        }
        if (changed || manual)
        {
            AppLogger.Info("LiveField",$"round={round} cells={marks.Count} manual={manual} recognition={clock.ElapsedMilliseconds}ms capture-to-notify={Environment.TickCount64-tick}ms");
            PublishField(ownership);
        }
    }

    public async Task<string> ReadLiveFieldAsync(LiveUnit? target = null, bool ownBoard = false)
    {
        if (Interlocked.CompareExchange(ref _manualField, 1, 0) != 0) return "正在识别阵容";
        using var deadline = new CancellationTokenSource();
        if (target is null) deadline.CancelAfter(ManualRecognitionLimit);
        var token = deadline.Token;
        try
        {
            return await Task.Run(() => ReadLiveFieldCoreAsync(target, ownBoard, token), token).WaitAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return "识别阵容已超时（8 秒），可重新识别。";
        }
        finally { Interlocked.Exchange(ref _manualField, 0); }
    }

    internal static readonly TimeSpan ManualRecognitionLimit = TimeSpan.FromSeconds(8);

    private async Task<string> ReadLiveFieldCoreAsync(LiveUnit? target, bool ownBoard, CancellationToken token)
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
            long captureTick = Environment.TickCount64;
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
                ObserveField(image, pixels, board, round, [], null, false, captureTick, run, connection, revision, true, hud.Mode, true, ownBoard, token);
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
            var unit = normal.Roster.Units.FirstOrDefault(u=>u.Id==target.Id && ReferenceEquals(u.Mark,target.Mark));
            if (unit == null) return "该干员已移动，请重新选择";
            FieldDetailFocus focus;
            FieldMark? beforeClick=normal.Marks.FirstOrDefault(m=>m.Slot==unit.Mark.Slot);
            // Pick a click point only from a fresh frame in the camera that is actually visible.
            if (_captureScreen() is not { } before || ImageBytes.GetDimensions(before) != size || !Current())
                return "游戏画面已切换，请重新选择";
            var samples = RecognitionDiagnostics.Samples;
            var review = samples.Begin($"干员复核 · {unit.Mark.Slot}", round);
            var reviewGame = LiveRoster.Observations.LastOrDefault()?.Game;
            samples.Step(review, "点击前", before);
            var freshPixels=BmpPixels.TryRead(ImageBytes.ToBmp(before))!;
            Point? click=null;
            BoardMatch? clickedBoard = null;
            if (FieldDetailFocus.Visible(freshPixels))
            {
                focus=new FieldDetailFocus(normal.Frame,normal.Board.Camera,normal.Marks);
                var selected=focus.Locate(freshPixels);
                if(selected==null) return Unresolved("未能确认详情对应的格子，请收起详情重试，或右键手动更换", "focus=unknown");
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
                clickedBoard=same;
                // Aim inside the torso. An adjacent melee sprite or a summon can cover the lower edge.
                click=current.Slot.Bench ? new(current.Foot.X,current.Foot.Y-(int)Math.Round(current.Tile*.15))
                    : new(current.Foot.X+(int)Math.Round(current.Tile*.06),current.Foot.Y-(int)Math.Round(current.Tile*.30));
                // A unit immediately behind the target can intercept the torso tap before its name is known.
                if (!current.Slot.Bench && marks.Any(m => !m.Slot.Bench && m.Slot.Col == current.Slot.Col && m.Slot.Row == current.Slot.Row - 1))
                    click = LiveReader.CellCenter(current.Slot, same.Camera, freshPixels);
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
            bool mayRetry = clickedBoard != null && !unit.Mark.Slot.Bench;
            for (int attempt = 0; attempt < 4 && Current(); attempt++)
            {
                await Task.Delay(120);
                if (_captureScreen() is not { } fresh || ImageBytes.GetDimensions(fresh) != size) break;
                var freshFrame=BmpPixels.TryRead(ImageBytes.ToBmp(fresh))!;
                bool visible = FieldDetailFocus.Visible(freshFrame);
                if (visible) mayRetry = false;
                var selectedSlot = visible ? focus.Locate(freshFrame)?.Mark.Slot : null;
                if (selectedSlot != unit.Mark.Slot)
                {
                    reason = !visible ? "panel-hidden" : $"focus={selectedSlot?.ToString() ?? "unknown"}";
                    samples.Step(review, $"目标 {unit.Mark.Slot} · {reason}", fresh);
                    last=null;
                    if (mayRetry && attempt == 2)
                    {
                        mayRetry = false;
                        var retryHud = await EconomyReader.ReadPreparationAsync(fresh,size.Width,size.Height);
                        if (!Current() || !retryHud.IsPreparation || retryHud.Round != round ||
                            _captureScreen() is not { } retryFrame || ImageBytes.GetDimensions(retryFrame) != size) break;
                        freshFrame = BmpPixels.TryRead(ImageBytes.ToBmp(retryFrame))!;
                        if (FieldDetailFocus.Visible(freshFrame))
                        {
                            attempt = -1;
                            continue;
                        }
                        var board = BoardRecognizer.Recognize(freshFrame);
                        if (board?.Map.Id != clickedBoard!.Map.Id || board.Camera != clickedBoard.Camera ||
                            LiveFieldReader.IsDragging(freshFrame)) break;
                        var marks = LiveReader.Read(freshFrame,board,false);
                        var current = marks.FirstOrDefault(m => m.Slot == unit.Mark.Slot && m.Tier == beforeClick!.Tier);
                        if (current == null || LiveFieldState.Difference(beforeClick!.Appearance,current.Appearance) >= 24) break;
                        var alternate = LiveReader.CellCenter(current.Slot,board.Camera,freshFrame);
                        if (alternate == click)
                            alternate = new(current.Foot.X,current.Foot.Y-(int)Math.Round(current.Tile*.16));
                        if (!Current()) break;
                        var sent = SendCapturedClick(connection,alternate,new Size(size.Width,size.Height),revision);
                        samples.Step(review,$"目标 {current.Slot} · 补点 {alternate} · {sent}",retryFrame);
                        AppLogger.Info("LiveField",$"review slot={current.Slot} retry={sent} point={alternate}");
                        if (sent != ClickResult.Sent) break;
                        focus = new FieldDetailFocus(freshFrame,board.Camera,marks);
                        beforeClick = current;
                        attempt = -1;
                    }
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
                    FieldOwnership? ownership;
                    lock (_runLock)
                    {
                        if (!Current()) return "对局已切换";
                        var saved = _liveState?.Roster.Units.FirstOrDefault(u=>u.Id==unit.Id && u.Mark.Slot==unit.Mark.Slot);
                        if (saved == null) return "该干员已移动";
                        if (saved.Manual && saved.Name != name) return $"详情为{name} · 已保留手动记录{saved.Name}";
                        result = saved.Manual ? $"详情与手动记录一致 · {name}" : $"{unit.Mark.Slot} · 已核实为{name}";
                        if (!_liveState!.Confirm(unit.Id,unit.Mark.Slot,name,saved.Manual,beforeClick))
                            return "该干员已移动，请重新选择";
                        ownership = Ownership(unit.Id, saved.Name);
                    }
                    PublishField(ownership);
                    AppLogger.Info("LiveField",$"review slot={unit.Mark.Slot} detail={name}");
                    result += await ReturnToBoard();
                    samples.Step(review, result);
                    return result;
                }
                last = name;
            }
            AppLogger.Info("LiveField",$"review slot={unit.Mark.Slot} reason={reason}");
            return Unresolved(reason == "panel-hidden" ? "未打开角色详情，请重新复核" :
                "详情未能核实，可右键选择「更换干员并记住」", reason);

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

            string Unresolved(string message, string? failure = null)
            {
                lock (_runLock)
                {
                    if (!Current()) return "对局已切换，请重新识别";
                    _liveState?.ReviewFailed(unit.Id,unit.Mark.Slot);
                }
                samples.Step(review, message);
                if (failure != null && reviewGame.HasValue && samples.Event(review) is { } failed)
                {
                    try { SaveFieldReview(reviewGame.Value, failed with { Result = $"{target.Mark.Slot} · {failure} · {message}" }); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { AppLogger.Warn("Feedback", "复核留样失败：" + ex.GetType().Name); }
                }
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
        foreach (var line in lines)
        {
            var text = line.Text.Trim().Replace(" ", "").Replace('傑', '杰');
            if (FieldTiers.ContainsKey(text)) return text;
            var quoted = "“" + text.Trim('“','”','"') + "”";
            if (FieldTiers.ContainsKey(quoted)) return quoted;
        }
        return null;
    }
}
