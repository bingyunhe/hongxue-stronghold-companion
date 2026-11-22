using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ASPAssistant.App.Controls;
using ASPAssistant.Core.Interop;
using ASPAssistant.Core.Models;

namespace ASPAssistant.App.Windows;

internal static class WindowUiScale
{
    internal static readonly DependencyProperty PageTargetProperty = DependencyProperty.RegisterAttached(
        "PageTarget", typeof(UiScaleTarget?), typeof(WindowUiScale));
    private static readonly DependencyProperty ComboHookProperty = DependencyProperty.RegisterAttached(
        "ComboHook", typeof(bool), typeof(WindowUiScale), new PropertyMetadata(false));
    private static readonly ConditionalWeakTable<Window, State> States = new();
    private static SidebarAppearance? _startupAppearance;
    private static bool _registered;

    public static void Initialize(SidebarAppearance appearance)
    {
        _startupAppearance = appearance;
        Register();
    }

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler((sender, _) =>
        {
            var tip = (ToolTip)sender;
            ScalePopup(tip, tip.PlacementTarget);
        }));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((sender, _) =>
        {
            var menu = (ContextMenu)sender;
            ScalePopup(menu, menu.PlacementTarget);
        }));
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler((sender, e) =>
        {
            if (!ReferenceEquals(sender, e.OriginalSource)) return;
            var item = (MenuItem)sender;
            var popup = (Popup)item.Template.FindName("PART_Popup", item);
            ScalePopup((FrameworkElement)popup.Child, item);
        }));
        EventManager.RegisterClassHandler(typeof(ComboBox), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            var box = (ComboBox)sender;
            if ((bool)box.GetValue(ComboHookProperty)) return;
            box.SetValue(ComboHookProperty, true);
            box.DropDownOpened += (_, _) =>
            {
                var popup = (Popup)box.Template.FindName("PART_Popup", box);
                ScalePopup((FrameworkElement)popup.Child, box);
            };
        }));
    }

    internal static void PrepareCentered(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            if (window.WindowStartupLocation != WindowStartupLocation.CenterOwner || window.Owner is not { } owner) return;
            var opacity = window.Opacity;
            window.Opacity = 0;
            window.ContentRendered += CenterAfterLayout;
            void CenterAfterLayout(object? sender, EventArgs args)
            {
                window.ContentRendered -= CenterAfterLayout;
                var hwnd = new WindowInteropHelper(window).Handle;
                var ownerHwnd = new WindowInteropHelper(owner).Handle;
                // A modal dialog can close while its first ContentRendered callback is still queued.
                if (!User32.IsWindow(hwnd) || !User32.IsWindow(ownerHwnd)) return;
                var ownerBounds = User32.ReadWindowRect(ownerHwnd);
                var bounds = User32.ReadWindowRect(hwnd);
                var left = ownerBounds.Left + (ownerBounds.Width - bounds.Width) / 2;
                var top = ownerBounds.Top + (ownerBounds.Height - bounds.Height) / 2;
                var work = User32.GetMonitorGeometry(ownerBounds.Left + ownerBounds.Width / 2, ownerBounds.Top + ownerBounds.Height / 2).WorkArea;
                User32.SetWindowBounds(hwnd, Core.Services.WindowGeometry.ClampBounds(new RECT
                { Left = left, Top = top, Right = left + bounds.Width, Bottom = top + bounds.Height }, work));
                window.Opacity = opacity;
            }
        };
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        var window = (Window)sender;
        if (window is SidePanelWindow or OverlayWindow || window.GetType().Assembly != typeof(SidePanelWindow).Assembly || States.TryGetValue(window, out _)) return;
        var main = window.Owner;
        while (main is not null && main is not SidePanelWindow) main = main.Owner;
        var side = main as SidePanelWindow;
        var appearance = side?.Appearance ?? _startupAppearance;
        if (appearance is null) return; // Standalone test/tool windows have no application appearance context.
        var state = new State(window, appearance, side);
        States.Add(window, state);
        window.SizeChanged += (_, _) =>
        {
            if (state.Updating || window.WindowState != WindowState.Normal) return;
            if (Math.Abs(window.ActualWidth - state.ExpectedWidth) > 1) state.Width = window.ActualWidth / state.Scale;
            if (Math.Abs(window.ActualHeight - state.ExpectedHeight) > 1) state.Height = window.ActualHeight / state.Scale;
        };
        window.DpiChanged += (_, _) => window.Dispatcher.BeginInvoke(() => Apply(state, true));
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Normal) window.Dispatcher.BeginInvoke(() => Apply(state, true));
        };
        Apply(state, false);
    }

    private static double Effective(State state)
    {
        var inherited = state.Appearance.UiScale;
        if (state.Window.Owner is SidePanelWindow)
            inherited = state.SourceTarget is { } source ? state.Appearance.ScaleFor(source, inherited) : inherited;
        else if (state.Window.Owner is { } owner && States.TryGetValue(owner, out var parent)) inherited = Effective(parent);
        return state.Target switch
        {
            UiScaleTarget.OperatorPicker => state.Appearance.ScaleFor(UiScaleTarget.OperatorPicker, inherited),
            { } target => state.Appearance.ScaleFor(target, state.Appearance.UiScale),
            _ => inherited,
        };
    }

    internal static double ScaleOf(Window window) => States.TryGetValue(window, out var state) ? Effective(state) : 1;

    public static void Refresh(SidebarAppearance appearance)
    {
        foreach (Window window in Application.Current.Windows)
            if (States.TryGetValue(window, out var state) && ReferenceEquals(state.Appearance, appearance)) Apply(state, false);
    }

    private static void Apply(State state, bool force)
    {
        if (!state.Window.IsLoaded || state.Window.WindowState == WindowState.Minimized) return;
        var scale = Effective(state);
        if (scale == state.Scale && !force) return;
        var window = state.Window;
        var hwnd = new WindowInteropHelper(window).Handle;
        var bounds = User32.ReadWindowRect(hwnd);
        var work = User32.GetMonitorGeometry(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2).WorkArea;
        var dpi = User32.GetWindowDpiScale(hwnd);
        state.Updating = true;
        try
        {
            state.Scale = scale;
            window.SizeToContent = SizeToContent.Manual;
            window.MinWidth = window.MinHeight = 0;
            window.MaxWidth = Math.Min(state.MaxWidth * scale, work.Width / dpi);
            window.MaxHeight = Math.Min(state.MaxHeight * scale, work.Height / dpi);
            window.MinWidth = Math.Min(state.MinWidth * scale, window.MaxWidth);
            window.MinHeight = Math.Min(state.MinHeight * scale, window.MaxHeight);
            window.Width = Math.Clamp(state.Width * scale, window.MinWidth, window.MaxWidth);
            window.Height = Math.Clamp(state.Height * scale, window.MinHeight, window.MaxHeight);
            state.ExpectedWidth = window.Width;
            state.ExpectedHeight = window.Height;
            if (scale == 1 && state.Viewport is { } viewport)
            {
                state.Root.Child = null;
                state.Root.Child = viewport.Detach();
                state.Viewport = null;
                window.SizeToContent = state.SizeToContent;
            }
            else if (scale != 1)
            {
                if (state.Viewport is null)
                {
                    var child = (FrameworkElement)state.Root.Child;
                    state.Root.Child = null;
                    state.Viewport = new ScaledViewport(child,
                        Math.Max(0, (state.MinWidth > 0 ? state.MinWidth : state.Width) - 2),
                        Math.Max(0, (state.MinHeight > 0 ? state.MinHeight : state.Height) - 2));
                    state.Root.Child = state.Viewport;
                }
                state.Viewport.ContentScale = scale;
            }
            if (window.WindowState == WindowState.Normal)
                User32.SetWindowBounds(hwnd, Core.Services.WindowGeometry.ClampBounds(User32.ReadWindowRect(hwnd), work));
        }
        finally { state.Updating = false; }
    }

    private static double SourceScale(UIElement? target)
    {
        UiScaleTarget? page = null;
        DependencyObject? current = target;
        while (current is not null)
        {
            page ??= (UiScaleTarget?)current.GetValue(PageTargetProperty);
            if (current is ToolTip { PlacementTarget: { } tipTarget }) return SourceScale(tipTarget);
            if (current is ContextMenu { PlacementTarget: { } menuTarget }) return SourceScale(menuTarget);
            current = VisualTreeHelper.GetParent(current);
        }
        var window = target is null ? null : Window.GetWindow(target);
        if (window is SidePanelWindow side)
            return page is { } value ? side.Appearance.ScaleFor(value, side.Appearance.UiScale) : side.Appearance.UiScale;
        return window is not null ? ScaleOf(window) : _startupAppearance?.UiScale ?? 1;
    }

    private static void ScalePopup(FrameworkElement child, UIElement? target)
    {
        child.Dispatcher.BeginInvoke(() =>
        {
            if (PresentationSource.FromVisual(child) is null) return; // Popup may close before the queued layout runs.
            child.UpdateLayout();
            if (child.ActualWidth <= 0) return; // An empty dropdown has no measurable surface.
            var start = child.PointToScreen(new Point());
            var end = child.PointToScreen(new Point(child.ActualWidth, 0));
            var natural = (end.X - start.X) / child.ActualWidth / VisualTreeHelper.GetDpi(child).DpiScaleX;
            var own = SourceScale(target) * child.LayoutTransform.Value.M11 / natural;
            if (Math.Abs(own - child.LayoutTransform.Value.M11) > 0.0001)
                child.LayoutTransform = new ScaleTransform(own, own);
        }, DispatcherPriority.Loaded);
    }

    private sealed class State(Window window, SidebarAppearance appearance, SidePanelWindow? side)
    {
        public Window Window { get; } = window;
        public SidebarAppearance Appearance { get; } = appearance;
        public UiScaleTarget? SourceTarget { get; } = (UiScaleTarget?)window.GetValue(PageTargetProperty)
            ?? (window is CustomOperatorSelectionWindow ? UiScaleTarget.Codex : side?.ActiveScaleTarget);
        public UiScaleTarget? Target { get; } = window switch { LineupEditorWindow => UiScaleTarget.LineupEditor, OperatorPickerWindow or CustomOperatorSelectionWindow => UiScaleTarget.OperatorPicker, _ => null };
        public Border Root { get; } = (Border)window.Content;
        public double Width { get; set; } = window.ActualWidth;
        public double Height { get; set; } = window.ActualHeight;
        public double MinWidth { get; } = window.MinWidth;
        public double MinHeight { get; } = window.MinHeight;
        public double MaxWidth { get; } = window.MaxWidth;
        public double MaxHeight { get; } = window.MaxHeight;
        public SizeToContent SizeToContent { get; } = window.SizeToContent;
        public double Scale { get; set; } = 1;
        public double ExpectedWidth { get; set; } = window.ActualWidth;
        public double ExpectedHeight { get; set; } = window.ActualHeight;
        public bool Updating { get; set; }
        public ScaledViewport? Viewport { get; set; }
    }
}
