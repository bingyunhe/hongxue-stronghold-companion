// Modified for ASP Assistant Arknights on 2026-11-12; see NOTICE.
namespace ASPAssistant.Core.Models;

/// <summary>Left/Top 是物理桌面坐标；侧栏保存的 WidthDip 以100%界面大小为基准，HeightDip 为实际WPF高度。</summary>
public sealed record SidePanelPlacement(double Left, double Top, double WidthDip, double HeightDip);

/// <summary>相对游戏右上角的偏移，使用侧栏所在屏幕的 WPF DIP。</summary>
public sealed record SidePanelOffset(double X, double Y);

public sealed record SidePanelPositionSettings
{
    /// <summary>
    /// On by default: the panel is docked beside the game once (no <see cref="Placement"/> yet), then stays
    /// where the player leaves it.
    /// </summary>
    public bool FreeMovement { get; init; } = true;
    public bool HoverEnabled { get; init; } = true;
    public bool HoverOnRight { get; init; }
    public bool ForceTopmost { get; init; }
    public bool HideOverlayWithAssistant { get; init; }
    public bool FocusLossEnabled { get; init; }
    public bool TrayOnFocusLoss { get; init; }
    public SidePanelPlacement? Placement { get; init; }
    public SidePanelOffset? FollowOffsetDip { get; init; }
}
