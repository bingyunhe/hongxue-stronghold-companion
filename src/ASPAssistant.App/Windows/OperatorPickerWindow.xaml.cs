// Modified for ASP Assistant Arknights on 2026-11-13; see NOTICE.
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;

namespace ASPAssistant.App.Windows;

/// <summary>
/// What the picker knows about the place being filled: the label (“后期 · 第 3 位”), who is there now, who else is
/// in the phase, the phase's covenant people without / with a candidate, and the lineup's targets.
/// </summary>
public sealed record OperatorPickerContext(
    string Place,
    string Current,
    IReadOnlySet<string> InLineup,
    IReadOnlyDictionary<string, int> Without,
    Func<string, IReadOnlyDictionary<string, int>> With,
    IReadOnlyList<TargetState> Targets,
    IReadOnlyList<CovenantInfo> Covenants);

/// <summary>Effect of putting an operator into the place: “迅捷 3 · 远见 1”, or “已在阵容”.</summary>
public sealed record OperatorEffect(string Text, bool Up, bool InLineup, bool HelpsTarget);

public sealed record GoalPill(string Name, string Text, bool Reached);

public partial class OperatorPickerWindow : Window
{
    private static Dictionary<string, OperatorEffect> _effects = new();

    /// <summary>Operator name → its <see cref="OperatorEffect"/> for the open picker.</summary>
    public static readonly IValueConverter EffectOf = new FuncConverter(v => v is string n && _effects.TryGetValue(n, out var e) ? e : null);
    public static readonly IValueConverter EffectUp = new FuncConverter(v => v is string n && _effects.TryGetValue(n, out var e) && e.Up);

    private readonly OperatorBrowseViewModel _filters = new();
    private readonly IReadOnlySet<string> _strategies;
    private readonly OperatorPickerContext? _context;
    private readonly bool _hasReviewCandidates;
    private bool _onlyTargets;
    private Operator? _pressedOperator;
    private Point _pressPosition;
    public string? Result { get; private set; }

    public OperatorPickerWindow(IEnumerable<Operator> operators, string current, IReadOnlySet<string> strategies,
        OperatorPickerContext? context = null, IReadOnlyList<string>? reviewCandidates = null)
    {
        _strategies = strategies;
        _context = context;
        var list = operators.OrderBy(op => op.Tier).ThenBy(op => op.Name, StringComparer.CurrentCulture).ToList();
        _effects = context is null ? new() : list.GroupBy(o => o.Name).ToDictionary(g => g.Key, g => EffectFor(g.First(), context));
        InitializeComponent();
        if (reviewCandidates is { Count: > 0 })
        {
            var ranks = reviewCandidates.Select((name, index) => (name, index)).ToDictionary(p => p.name, p => p.index);
            list = list.OrderBy(o => ranks.GetValueOrDefault(o.Name, int.MaxValue)).ToList();
            TitleMeta.Text = $"外观候选　{string.Join("、", reviewCandidates)}";
            _hasReviewCandidates = true;
        }
        WindowUiScale.PrepareCentered(this);
        var style = new Style(typeof(ListBoxItem), OperatorsList.ItemContainerStyle);
        style.Setters.Add(new Setter(IsEnabledProperty, new Binding { Converter = new FuncConverter(v => v is Operator o && Unavailable(o) is null) }));
        style.Setters.Add(new Setter(ToolTipProperty, new Binding { Converter = new FuncConverter(v => v is Operator o ? Unavailable(o) : null) }));
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.45));
        disabled.Setters.Add(new Setter(ToolTipService.ShowOnDisabledProperty, true));
        disabled.Setters.Add(new Setter(ToolTipService.InitialShowDelayProperty, 0));
        style.Triggers.Add(disabled);
        OperatorsList.ItemContainerStyle = style;
        _filters.LoadOperators(list);
        DataContext = _filters;
        OperatorsList.SelectedItem = _filters.FilteredOperators.FirstOrDefault(op => op.Name == current);

        if (context is not null)
        {
            TitleMeta.Text = context.Place;
            ContextBar.Visibility = Visibility.Visible;
            ContextPlace.Text = context.Place;
            ContextReplace.Text = context.Current.Length > 0 ? $" · 替换 {context.Current}" : " · 空位";
            GoalPills.ItemsSource = context.Targets.Select(t => new GoalPill(t.Name, $"{t.Label} {t.Count}/{t.Needed}", t.Reached)).ToList();
        }
        if (context is null || context.Targets.All(t => t.Reached)) ScopeTargets.IsEnabled = false;
        ScopeList.Visibility = Visibility.Collapsed;
        _filters.FilteredOperators.CollectionChanged += (_, _) => UpdateCount();
        Loaded += (_, _) => { ApplyScope(); SearchBox.Focus(); };
        Closed += (_, _) => _effects = new();
    }

    /// <summary>Why an operator cannot go here (null = it can).</summary>
    private string? Unavailable(Operator op)
    {
        if (op.RequiredStrategyId.Length > 0 && !_strategies.Contains(op.RequiredStrategyId))
            return op.RequiredStrategyId == PlannerStrategies.PithId ? "必须在策略中添加Pith的“优等生”策略" : "必须在策略中添加森蚺策略";
        if (_context?.InLineup.Contains(op.Name) == true) return "已在阵容";
        return null;
    }

    private static OperatorEffect EffectFor(Operator op, OperatorPickerContext c)
    {
        if (c.InLineup.Contains(op.Name)) return new("已在阵容", false, true, false);
        var after = c.With(op.Name);
        var infos = c.Covenants.ToDictionary(i => i.Name);
        var parts = new List<string>();
        var up = false;
        var helps = false;
        foreach (var name in op.CoreCovenants.Concat(op.AdditionalCovenants).Distinct())
        {
            var before = c.Without.GetValueOrDefault(name);
            var now = after.GetValueOrDefault(name);
            if (now <= before) continue;
            var activates = infos.TryGetValue(name, out var info)
                && Crosses(before, now, info);
            var target = c.Targets.FirstOrDefault(t => t.Name == name);
            if (target is not null && !target.Reached) helps = true;
            up |= activates || (target is not null && !target.Reached);
            parts.Add(activates && parts.Count == 0 ? $"{name} {now} · 激活" : $"{name} {now}");
        }
        return new(string.Join(" · ", parts.Take(2)), up, false, helps);
    }

    /// <summary>True when the count passes a level of the covenant (Ⅰ, or the people-count Ⅱ).</summary>
    private static bool Crosses(int before, int now, CovenantInfo info)
    {
        foreach (var level in new[] { 1, 2 })
        {
            var goal = LineupPlanner.CreateGoal(info, level);
            if (goal.Level == level && before < goal.Count && now >= goal.Count) return true;
        }
        return false;
    }

    private void OnScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        _onlyTargets = ScopeList.SelectedIndex == 1;
        if (IsLoaded) ApplyScope();
    }

    private void ApplyScope()
    {
        var view = CollectionViewSource.GetDefaultView(OperatorsList.ItemsSource);
        if (view is null) return;
        if (_onlyTargets || view.Filter is not null)
            view.Filter = _onlyTargets ? o => _effects.TryGetValue(((Operator)o).Name, out var e) && e.HelpsTarget : null;
        UpdateCount();
    }

    private void UpdateCount()
    {
        var view = CollectionViewSource.GetDefaultView(OperatorsList.ItemsSource);
        var count = view?.Cast<object>().Count() ?? 0;
        CountText.Text = $"{count} 位干员";
        EmptyHint.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnResetFiltersClick(object sender, RoutedEventArgs e) => _filters.ResetFilters();

    private void Pick(Operator op)
    {
        if (Unavailable(op) is not null) return;
        Result = op.Name;
        Close();
    }

    private void OnOperatorMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressedOperator = null;
        if (ItemsControl.ContainerFromElement(OperatorsList, e.OriginalSource as DependencyObject) is not ListBoxItem { IsEnabled: true } item)
            return;
        _pressedOperator = (Operator)item.Content;
        _pressPosition = e.GetPosition(OperatorsList);
    }

    private void OnOperatorClick(object sender, MouseButtonEventArgs e)
    {
        var op = _pressedOperator;
        _pressedOperator = null;
        if (op is null || ItemsControl.ContainerFromElement(OperatorsList, e.OriginalSource as DependencyObject) is not ListBoxItem)
            return;
        var movement = e.GetPosition(OperatorsList) - _pressPosition;
        if (Math.Abs(movement.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(movement.Y) > SystemParameters.MinimumVerticalDragDistance)
            return;
        Pick(op);
    }

    private void OnOperatorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (_hasReviewCandidates && OperatorsList.SelectedItem == null && string.IsNullOrWhiteSpace(SearchBox.Text)) return;
        var op = OperatorsList.SelectedItem as Operator
                 ?? CollectionViewSource.GetDefaultView(OperatorsList.ItemsSource)?.Cast<Operator>().FirstOrDefault(o => Unavailable(o) is null);
        if (op is null) return;
        Pick(op);
        e.Handled = true;
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        Result = "";
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}

/// <summary>One-way converter from a lambda.</summary>
public sealed class FuncConverter(Func<object?, object?> convert) : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => convert(value);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
