using System.Text.Json;

namespace ASPAssistant.Core.Services;

public sealed record RecognitionSnapshot(DateTimeOffset CapturedAt, string Source, byte[] Frame, string Result);

public static class RecognitionDiagnostics
{
    public const int MaximumFrameBytes = 16 * 1024 * 1024;
    private static RecognitionSnapshot? _latest;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public static RecognitionSampleHistory Samples { get; } = new();
    public static RecognitionSnapshot? Latest => Volatile.Read(ref _latest);

    // Frames are already encoded by the capture service and are not modified by readers.
    public static void Record(DateTimeOffset capturedAt, string source, byte[] frame, object result)
    {
        var next = new RecognitionSnapshot(capturedAt, source,
            frame.Length <= MaximumFrameBytes ? frame : [],
            JsonSerializer.Serialize(result, JsonOptions));
        RecognitionSnapshot? previous;
        do
        {
            previous = Latest;
            if (previous is not null && previous.CapturedAt > capturedAt) return;
        } while (!ReferenceEquals(Interlocked.CompareExchange(ref _latest, next, previous), previous));
    }
}
