namespace ASPAssistant.Core.Models;

public sealed record GuideWindowSettings(int? Left = null, int? Top = null, int Width = 510, int Height = 410,
    int FontSize = 16, bool Pinned = true);
