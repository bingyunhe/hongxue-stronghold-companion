// Modified for ASP Assistant Arknights on 2026-11-11; see NOTICE.
using System.Text.Json.Serialization;

namespace ASPAssistant.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<GameCaptureSource>))]
public enum GameCaptureSource { Pc, Adb }

[JsonConverter(typeof(JsonStringEnumConverter<EmulatorKind>))]
public enum EmulatorKind { Unknown, MuMu, LDPlayer }

public sealed record GameConnectionSettings
{
    public GameCaptureSource Source { get; init; }
    public string AdbPath { get; init; } = "";
    public string AdbSerial { get; init; } = "";
    public string WindowTitle { get; init; } = "MuMu安卓设备";
    public EmulatorKind Emulator { get; init; }
    public string AndroidPackageName { get; init; } = "";
    public int? AndroidDisplayId { get; init; }
    public bool FollowFocusedGame { get; init; } = true;
    public bool PreferMuMuEnhancedCapture { get; init; }
}

public sealed record AdbGameDisplay(int DisplayId, string? PhysicalDisplayId, string PackageName, bool IsFocused)
{
    public string Label => $"{(PackageName.EndsWith(".bilibili", StringComparison.Ordinal) ? "B服" : "官服")} · 屏幕 {DisplayId}"
        + (IsFocused ? " · 当前操作" : "");
}
