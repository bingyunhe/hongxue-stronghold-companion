using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using ASPAssistant.Core.Data;

namespace ASPAssistant.App.Views;

public partial class DeployView
{
    private static readonly TimeSpan RouteDuration = TimeSpan.FromSeconds(2);
    private readonly DispatcherTimer _routeTimer = new() { Interval = RouteDuration };
    private readonly Stopwatch _routePhase = new();
    private readonly MatrixTransform _routeArrowTransform = new();
    private readonly List<(Border Row, Polyline[] Lines, PathGeometry Geometry)> _routeRows = [];
    private readonly Polygon _routeArrow = new()
    {
        Points = [new(-4, -3.5), new(4, 0), new(-4, 3.5), new(-2, 0)],
        IsHitTestVisible = false, Visibility = Visibility.Hidden
    };
    private readonly Rectangle _routeGate = new() { Width = S - 2, Height = S - 2, RadiusX = 2, RadiusY = 2,
        StrokeThickness = 2.4, IsHitTestVisible = false, Visibility = Visibility.Hidden };
    private int _routeIndex;

    private void BuildRouteDetails(GarrisonRoundRouteMatch match)
    {
        var canvas = match.Grid is null ? BoardCanvas : StageRoutesCanvas;
        var lines = canvas.Children.OfType<Polyline>().ToLookup(p => p.Tag);
        foreach (var gate in match.Routes.Where(r => r.Enemies.Count > 0).GroupBy(r => r.Path.Lane))
        {
            var label = gate.Key switch { "upper" => "上口", "lower" => "下口", _ => "战区" };
            var total = match.GateTotals[gate.Key];
            var heading = new TextBlock { Text = $"{label} · {total} 只" + (match.RosterMismatch || total.Min != total.Max ? "（候选）" : ""),
                FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 2) };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            RouteDetails.Children.Add(heading);
            int number = 0;
            foreach (var route in gate)
            {
                number++;
                var items = new WrapPanel();
                if (gate.Count() > 1 || route.Path.Flying)
                    AddItem((route.Path.Flying ? "航线 " : "路线 ") + number, "TextSecondaryBrush");
                foreach (var (name, count) in route.Enemies)
                    AddItem($"{name} ×{count}", "TextPrimaryBrush");
                var row = new Border { Child = items, CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 2, 2, 2),
                    Margin = new Thickness(0, 0, 0, 2), Tag = route };
                RouteDetails.Children.Add(row);
                if (route.Overlaps)
                {
                    var strokes = lines[route].ToArray();
                    var path = new PathGeometry();
                    foreach (var stroke in strokes)
                        path.Figures.Add(new PathFigure(stroke.Points[0],
                            [new PolyLineSegment(stroke.Points.Skip(1), true)], false));
                    path.Freeze();
                    _routeRows.Add((row, strokes, path));
                }

                void AddItem(string value, string color)
                {
                    var text = new TextBlock { Text = value, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 7, 0) };
                    text.SetResourceReference(TextBlock.ForegroundProperty, color);
                    items.Children.Add(text);
                }
            }
        }
        if (_routeEnemies is { Bounties.Count: > 0 } enemies)
        {
            var bounty = new TextBlock { Text = $"悬赏 {string.Join("、", enemies.Bounties)} · 出怪口待确认",
                TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 2, 0, 4) };
            bounty.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            RouteDetails.Children.Add(bounty);
        }
        if (_routeRows.Count < 2) return;
        _routeArrow.RenderTransform = _routeArrowTransform;
        _routeArrow.SetResourceReference(Shape.StrokeProperty, "CardBackgroundBrush");
        _routeArrow.StrokeThickness = 0.8;
        canvas.Children.Add(_routeGate);
        canvas.Children.Add(_routeArrow);
    }

    private void UpdateRoutePlayback()
    {
        if (!BoardCard.IsVisible || _routeRows.Count < 2)
        {
            _routeTimer.Stop();
            _routePhase.Reset();
            _routeIndex = 0;
            _routeArrowTransform.BeginAnimation(MatrixTransform.MatrixProperty, null);
            _routeArrow.Visibility = _routeGate.Visibility = Visibility.Hidden;
            RoutePhaseNumber.Visibility = Visibility.Collapsed;
            return;
        }
        if (!_routeTimer.IsEnabled)
        {
            _routePhase.Restart();
            _routeTimer.Start();
        }
        ShowRoutePhase();
    }

    private void AdvanceRoute()
    {
        _routeIndex = (_routeIndex + 1) % _routeRows.Count;
        _routePhase.Restart();
        ShowRoutePhase();
    }

    private void ShowRoutePhase()
    {
        RoutePhaseNumber.Text = $"{_routeIndex + 1} / {_routeRows.Count}";
        RoutePhaseNumber.Visibility = Visibility.Visible;
        for (int i = 0; i < _routeRows.Count; i++)
        {
            var (row, lines, geometry) = _routeRows[i];
            bool active = i == _routeIndex;
            if (active) row.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
            else row.ClearValue(Border.BackgroundProperty);
            foreach (var line in lines) line.Opacity = active ? 1 : .22;
            if (!active) continue;
            var brush = lines[0].Stroke;
            _routeGate.Stroke = _routeArrow.Fill = brush;
            var start = geometry.Figures[0].StartPoint;
            Canvas.SetLeft(_routeGate, start.X - _routeGate.Width / 2);
            Canvas.SetTop(_routeGate, start.Y - _routeGate.Height / 2);
            _routeArrow.Visibility = _routeGate.Visibility = Visibility.Visible;
            var animation = new MatrixAnimationUsingPath
            {
                PathGeometry = geometry, Duration = RouteDuration, DoesRotateWithTangent = true,
                BeginTime = -_routePhase.Elapsed
            };
            Timeline.SetDesiredFrameRate(animation, 24);
            _routeArrowTransform.BeginAnimation(MatrixTransform.MatrixProperty, animation);
        }
    }
}
