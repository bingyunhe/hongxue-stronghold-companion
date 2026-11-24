// Modified for ASP Assistant Arknights on 2026-11-12; see NOTICE.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using ASPAssistant.App.Windows;

namespace ASPAssistant.App.Controls;

/// <summary>
/// Controls ordinary hover hints and anchors detail cards to the preferred side of their window.
/// </summary>
public static class PanelToolTip
{
    private const double Gap = 8;
    public const int CovenantInitialDelay = 250;
    public const int CovenantSwitchInterval = 800;

    private static readonly HashSet<ToolTip> OpenTips = [];
    internal static bool Enabled { get; private set; } = true;
    internal static bool PreferRight { get; private set; }

    public static readonly DependencyProperty CovenantProperty = DependencyProperty.RegisterAttached(
        "Covenant", typeof(string), typeof(PanelToolTip), new PropertyMetadata(null, (d, e) =>
        {
            if (e.NewValue is not string { Length: > 0 })
            {
                d.ClearValue(ToolTipService.ToolTipProperty);
                d.ClearValue(ToolTipService.InitialShowDelayProperty);
                d.ClearValue(ToolTipService.BetweenShowDelayProperty);
                return;
            }
            ToolTipService.SetInitialShowDelay(d, CovenantInitialDelay);
            ToolTipService.SetBetweenShowDelay(d, CovenantSwitchInterval);
            if (d is FrameworkContentElement inline) inline.SetResourceReference(FrameworkContentElement.ToolTipProperty, "PanelCovenantTip");
            else ((FrameworkElement)d).SetResourceReference(FrameworkElement.ToolTipProperty, "PanelCovenantTip");
        }));
    public static string? GetCovenant(DependencyObject owner) => (string?)owner.GetValue(CovenantProperty);
    public static void SetCovenant(DependencyObject owner, string? value) => owner.SetValue(CovenantProperty, value);

    internal static void AppendNames(TextBlock text, IEnumerable<string> names, string separator)
    {
        bool first = true;
        foreach (var name in names)
        {
            if (!first) text.Inlines.Add(new Run(separator));
            var run = new Run(name);
            SetCovenant(run, name);
            text.Inlines.Add(run);
            first = false;
        }
    }

    public static readonly DependencyProperty NoteProperty = DependencyProperty.RegisterAttached(
        "Note", typeof(string), typeof(PanelToolTip));
    public static string? GetNote(DependencyObject owner) => (string?)owner.GetValue(NoteProperty);
    public static void SetNote(DependencyObject owner, string? value) => owner.SetValue(NoteProperty, value);

    public static void RegisterForAllElements()
    {
        foreach (var type in new[] { typeof(FrameworkElement), typeof(FrameworkContentElement) })
            EventManager.RegisterClassHandler(type, ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler(OnToolTipOpening));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler((s, _) => OpenTips.Add((ToolTip)s)));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.ClosedEvent, new RoutedEventHandler((s, _) => OpenTips.Remove((ToolTip)s)));
    }

    internal static void SetPreferences(bool enabled, bool right)
    {
        if (Enabled == enabled && PreferRight == right) return;
        Enabled = enabled;
        PreferRight = right;
        foreach (var tip in OpenTips.ToArray())
            if (tip.PlacementTarget is not { } target || Window.GetWindow(target) is not TrackingGuideWindow)
                tip.IsOpen = false;
    }

    private static void OnToolTipOpening(object sender, ToolTipEventArgs e)
    {
        var window = Window.GetWindow((DependencyObject)sender);
        if (window is TrackingGuideWindow) return;
        if (!Enabled) { e.Handled = true; return; }
        var source = (DependencyObject)sender;
        var owner = sender as FrameworkElement ?? (sender as Run)?.Parent as FrameworkElement;
        if (owner is null || ToolTipService.GetToolTip(source) is not ToolTip tip) return;
        if (GetCovenant(source) is { Length: > 0 } name)
        {
            var panel = window;
            while (panel != null && panel is not SidePanelWindow) panel = panel.Owner;
            var item = (panel as SidePanelWindow)?.PlannerVm?.CovenantDetails.FirstOrDefault(c => c.Name == name);
            tip.Tag = GetNote(source);
            tip.ContentTemplate = (DataTemplate)owner.FindResource(item is null ? "CovenantNameOnly" : "ScrollableCovenantDetails");
            tip.Content = (object?)item ?? name;
        }
        Configure(tip);
    }

    internal static CustomPopupPlacement[] Sides(double left, double right) =>
        [new(new Point(PreferRight ? right : left, 0), PopupPrimaryAxis.Vertical),
         new(new Point(PreferRight ? left : right, 0), PopupPrimaryAxis.Vertical)];

    private static void Configure(ToolTip tip)
    {
        if (tip.Placement is not (PlacementMode.Left or PlacementMode.Custom)) return;
        tip.Placement = PlacementMode.Custom;
        tip.HorizontalOffset = 0;
        tip.VerticalOffset = 0;
        tip.CustomPopupPlacementCallback = (popupSize, _, _) =>
        {
            if (tip.PlacementTarget is not FrameworkElement { IsVisible: true, ActualWidth: > 0 } target || Window.GetWindow(target) is not { } window)
                return [new CustomPopupPlacement(new Point(-popupSize.Width - Gap, 0), PopupPrimaryAxis.Horizontal)];
            // The callback uses device pixels. Anchor to the window even when its content is scaled.
            var originX = target.PointToScreen(new Point()).X;
            var gap = Gap * VisualTreeHelper.GetDpi(window).DpiScaleX;
            var left = window.PointToScreen(new Point()).X - originX - gap - popupSize.Width;
            var right = window.PointToScreen(new Point(window.ActualWidth, 0)).X - originX + gap;
            return Sides(left, right);
        };
    }
}
