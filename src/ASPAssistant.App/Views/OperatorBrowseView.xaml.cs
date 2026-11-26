// Modified for ASP Assistant Arknights on 2026-11-13; see NOTICE.
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ASPAssistant.App.Controls;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.ViewModels;

namespace ASPAssistant.App.Views;

public sealed record CatalogTier(int Name, int ItemCount);
public sealed record CatalogRow(int Columns, Operator[] Operators);

public partial class OperatorBrowseView : UserControl
{
    public event Action<string, TrackingType>? TrackingRequested;
    public event Action<string>? UntrackingRequested;
    public event Action<string, bool>? BanToggleRequested;
    public Func<string, bool>? IsTrackedCheck { get; set; }

    public Func<string, bool>? IsBannedCheck { get; set; }
    public Func<string, bool>? IsCovenantBannedCheck { get; set; }
    public event Action? ManageCustomOperatorsRequested;

    public event Action? RecognizeCustomRequested;
    public event Action<Operator, FrameworkElement>? CarryRequested;
    public event Action? CarryAdviceRequested;
    private void OnCarryAdvice(object sender, RoutedEventArgs e) => CarryAdviceRequested?.Invoke();
    private void OnRecognizeCustom(object sender, RoutedEventArgs e) => RecognizeCustomRequested?.Invoke();
    internal void ToggleCarry(Operator op, FrameworkElement anchor) => CarryRequested?.Invoke(op, anchor);

    private void OnManageCustomOperators(object sender, RoutedEventArgs e) => ManageCustomOperatorsRequested?.Invoke();

    public OperatorBrowseView()
    {
        InitializeComponent();
        DataContextChanged += OnViewDataContextChanged;
        SizeChanged += (_, args) => LayoutLabel.Visibility = args.NewSize.Width < 320 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnViewDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is OperatorBrowseViewModel old)
            PropertyChangedEventManager.RemoveHandler(old, OnFiltered, nameof(old.MatchedCount));
        if (e.NewValue is OperatorBrowseViewModel vm)
        {
            PropertyChangedEventManager.AddHandler(vm, OnFiltered, nameof(vm.MatchedCount));
            AvailabilityFilterList.SelectedIndex = vm.SelectedAvailabilityFilter switch
            {
                AvailabilityFilter.Available => 1,
                AvailabilityFilter.Unavailable => 2,
                _ => 0
            };
        }
        RebuildRows();
    }

    private int _columns = 1;

    private void OnFiltered(object? sender, PropertyChangedEventArgs e) => RebuildRows();

    private void RebuildRows()
    {
        var rows = new List<object>();
        if (DataContext is OperatorBrowseViewModel vm)
            foreach (var tier in vm.FilteredOperators.GroupBy(op => op.Tier).OrderBy(group => group.Key))
            {
                if (vm.SelectedOperatorSource != OperatorSourceFilter.Custom)
                    rows.Add(new CatalogTier(tier.Key, tier.Count()));
                foreach (var operators in tier.Chunk(_columns))
                    rows.Add(new CatalogRow(_columns, operators));
            }
        OperatorListControl.ItemsSource = rows;
    }

    private void OnCycleLayout(object sender, RoutedEventArgs e)
    {
        var scroll = FindVisualChildren<ScrollViewer>(OperatorListControl).FirstOrDefault();
        OperatorCard? anchorCard = null;
        if (scroll is { VerticalOffset: > 0 })
            anchorCard = FindVisualChildren<OperatorCard>(scroll).FirstOrDefault(card =>
            {
                var y = card.TranslatePoint(new Point(), scroll).Y;
                return y + card.ActualHeight > 0 && y < scroll.ViewportHeight;
            });
        var anchor = anchorCard?.DataContext as Operator;
        var anchorY = anchorCard?.TranslatePoint(new Point(), scroll).Y ?? 0;
        _columns = _columns % 3 + 1;
        var (current, next) = _columns switch { 2 => ("双列", "紧凑"), 3 => ("紧凑", "单列"), _ => ("单列", "双列") };
        LayoutLabel.Text = current;
        LayoutLines.Data = _columns switch
        {
            2 => Geometry.Parse("M6.5,1 V10"),
            3 => Geometry.Parse("M4.5,1 V10 M8.5,1 V10"),
            _ => null
        };
        LayoutButton.ToolTip = $"当前{current}，点击切换到{next}";
        AutomationProperties.SetName(LayoutButton, $"干员排列，当前{current}");
        RebuildRows();
        if (anchor is null) return;
        var target = OperatorListControl.Items.OfType<CatalogRow>().First(row => row.Operators.Contains(anchor));
        var index = OperatorListControl.Items.IndexOf(target);
        FindVisualChildren<VirtualizingStackPanel>(OperatorListControl).First().BringIndexIntoViewPublic(index);
        OperatorListControl.UpdateLayout();
        if (OperatorListControl.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement row && scroll != null)
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + row.TranslatePoint(new Point(), scroll).Y - anchorY);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private void OnOperatorBanToggled(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is OperatorCard card && card.DataContext is Operator op)
            BanToggleRequested?.Invoke(op.Name, card.IsBanned);
    }

    private void OnOperatorTrackingToggled(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is OperatorCard card && card.DataContext is Operator op)
        {
            if (card.IsTracked)
                TrackingRequested?.Invoke(op.Name, TrackingType.Operator);
            else
                UntrackingRequested?.Invoke(op.Name);
        }
    }

    public void RefreshTrackingStates()
    {
        if (IsTrackedCheck == null) return;
        foreach (var card in FindVisualChildren<OperatorCard>(OperatorListControl))
        {
            if (card.DataContext is Operator op)
            {
                card.RefreshTrackedState(IsTrackedCheck(op.Name));
            }
        }
    }

    public void RefreshBannedStates()
    {
        if (IsBannedCheck == null) return;
        foreach (var card in FindVisualChildren<OperatorCard>(OperatorListControl))
        {
            if (card.DataContext is Operator op)
                card.RefreshBannedState(IsBannedCheck(op.Name));
        }
    }

    private void OnAvailabilityFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not OperatorBrowseViewModel vm) return;
        vm.SelectedAvailabilityFilter = AvailabilityFilterList.SelectedIndex switch
        {
            1 => Core.ViewModels.AvailabilityFilter.Available,
            2 => Core.ViewModels.AvailabilityFilter.Unavailable,
            _ => Core.ViewModels.AvailabilityFilter.All
        };
    }


    private void OnCollapseFiltersClicked(object sender, RoutedEventArgs e) => FilterToggle.IsChecked = false;

    /// <summary>重置: every chip, the availability switch, search and 任一/全部 back to the defaults.</summary>
    private void OnResetFiltersClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not OperatorBrowseViewModel vm) return;
        AvailabilityFilterList.SelectedIndex = 0;
        vm.ResetFilters();
    }
}
