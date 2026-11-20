using System.Drawing;
using ASPAssistant.Core.Interop;

namespace ASPAssistant.Core.Services;

public partial class OcrScannerService
{
    public bool StartupInformationEnabled { get; init; }
    public Func<Task<bool>>? RecordStartupInformationAsync { get; set; }
    private volatile bool _startupOpportunityConsumed;
    private volatile bool _startupCancelled;
    public void CancelStartupInformation() => _startupCancelled = true;

    internal async Task<bool> TryStartupInformationAsync(byte[] observed,
        GameConnectionService? connection, int run, long capture)
    {
        if (!StartupInformationEnabled || _startupOpportunityConsumed || _startupCancelled ||
            connection is null ||
            RecordStartupInformationAsync is not { } record || EconomyReader is not { } economy) return false;
        bool Current() => !_disposed && !_startupCancelled && !_pauseBanScreenDetection && !_banScreenActive &&
            RunRevision == run && IsCurrentCapture(connection, capture);
        if (!Current() || InformationControl(observed, false) is null || _captureScreen() is not { } fresh ||
            InformationControl(fresh, false) is not { } lens) return false;
        var currentHud = await economy.ReadPreparationAsync(fresh, lens.Size.Width, lens.Size.Height);
        // A compact round badge may evade OCR. The ready button is visible only on the preparation board.
        if (!currentHud.IsPreparation || currentHud.Round == null)
        {
            var size = lens.Size;
            var ready = await _ocrEngine.RecognizeRegionAsync(fresh, size.Width * 84 / 100, size.Height / 10,
                size.Width * 16 / 100, size.Height / 10);
            if (!ready.Any(t => t.Text.Replace(" ", "").Contains("准备就绪"))) return false;
        }
        if (!Current() || _startupOpportunityConsumed) return false;
        var round = currentHud.Round;
        _startupOpportunityConsumed = true;
        var sample = RecognitionDiagnostics.Samples.Begin("中途启动录入", round);
        void Step(string message, byte[]? image = null)
        {
            RecognitionDiagnostics.Samples.Step(sample, message, image);
            AppLogger.Info("StartupInfo", $"round={round} {message}");
        }
        var opened = SendCapturedClick(connection, lens.Point, lens.Size, capture);
        Step($"蓝色放大镜 {lens.Point} · {opened}", fresh);
        if (opened != ClickResult.Sent) return true;

        for (int attempt = 0; attempt < 4; attempt++)
        {
            await Task.Delay(200);
            if (!Current()) return true;
            if (_captureScreen() is not { } modal || InformationControl(modal, true) is not { } close ||
                !await _informationDetector.IsInformationScreenAsync(modal, close.Size.Width, close.Size.Height)) continue;
            if (!Current()) return true;
            Step("已打开对局信息", modal);
            if (!await record()) { Step("未完整录入，保留信息页"); return true; }
            if (!Current() || _captureScreen() is not { } latest || InformationControl(latest, true) is not { } target ||
                !await _informationDetector.IsInformationScreenAsync(latest, target.Size.Width, target.Size.Height) || !Current())
            { Step("关闭前未确认当前信息页"); return true; }
            var closed = SendCapturedClick(connection, target.Point, target.Size, capture);
            Step($"关闭对局信息 {target.Point} · {closed}", latest);
            if (closed == ClickResult.Sent)
            {
                for (int verify = 0; verify < 4; verify++)
                {
                    await Task.Delay(200);
                    if (!Current()) return true;
                    if (_captureScreen() is { } after && InformationControl(after, false) != null)
                    { Step("已返回棋盘", after); return true; }
                }
                Step("未确认返回棋盘");
            }
            return true;
        }
        Step("未确认信息页打开");
        return true;
    }

    internal static (Point Point, Size Size)? InformationControl(byte[] bytes, bool close)
    {
        using var stream = new MemoryStream(bytes);
        using var frame = new Bitmap(stream);
        double scale = Math.Min(frame.Width / 1434d, frame.Height / 807d);
        int left = (int)((frame.Width - 1434 * scale) / 2);
        int top = (int)((frame.Height - 807 * scale) / 2);
        bool Lit(int x, int y)
        {
            var c = frame.GetPixel(left + (int)(x * scale), top + (int)(y * scale));
            return close ? Math.Min(c.R, Math.Min(c.G, c.B)) > 180 :
                c.G > 115 && c.G - c.R > 35 && c.B - c.R > 30;
        }
        // The cyan lens has a dark center and a diagonal handle. The modal uses a white X.
        int cx = close ? 1348 : 378, cy = close ? 49 : 28;
        Point? best = null;
        int nearest = int.MaxValue;
        for (int y = cy - 12; y <= cy + 12; y++)
        for (int x = cx - 22; x <= cx + 22; x++)
        {
            int distance = (x-cx)*(x-cx) + (y-cy)*(y-cy);
            if (distance >= nearest) continue;
            bool matches = close
                ? Lit(x,y) && Lit(x-11,y-11) && Lit(x+11,y+11) && Lit(x-11,y+11) && Lit(x+11,y-11) &&
                    !Lit(x,y-12) && !Lit(x+12,y) && !Lit(x-12,y) && !Lit(x,y+12)
                : !Lit(x,y) && Lit(x,y-7) && Lit(x-8,y) && Lit(x+8,y) && Lit(x,y+7) &&
                    Lit(x+6,y+13) && Lit(x-14,y-3);
            if (!matches) continue;
            nearest = distance;
            best = new Point(left + (int)(x*scale), top + (int)(y*scale));
        }
        return best is { } point ? (point, frame.Size) : null;
    }
}
