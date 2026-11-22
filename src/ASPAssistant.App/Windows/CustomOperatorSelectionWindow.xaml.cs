using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ASPAssistant.App.Controls;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.ViewModels;

namespace ASPAssistant.App.Windows;

public partial class CustomOperatorSelectionWindow : Window
{
    private readonly MissingCovenantFilters _missing;
    private readonly List<CheckBox> _choices = [];
    public IReadOnlyList<string>? Result { get; private set; }

    public CustomOperatorSelectionWindow(IEnumerable<Operator> candidates)
    {
        _missing = new(_ => FilterChoices());
        InitializeComponent();
        CovenantFilters.ItemsSource = _missing.Chips;
        WindowUiScale.PrepareCentered(this);
        foreach (var op in candidates.OrderBy(o => o.Name, StringComparer.CurrentCulture))
        {
            var name = new TextBlock { Text = op.Name, FontSize = 12, FontWeight = FontWeights.Bold };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var covenants = new TextBlock
            {
                Text = string.Join(" · ", op.CoreCovenants.Concat(op.AdditionalCovenants)),
                FontSize = 10, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
            };
            covenants.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            var body = new DockPanel { Width = 208 };
            var avatar = new Avatar { Width = 30, Height = 30, Text = op.Name, Source = op.IconPath, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(avatar, Dock.Left);
            body.Children.Add(avatar);
            body.Children.Add(new StackPanel { Children = { name, covenants } });
            var check = new CheckBox { Tag = op.Name, IsChecked = op.IsSelectedCustom, Content = body };
            System.Windows.Automation.AutomationProperties.SetName(check, op.Name);
            _choices.Add(check);
            var card = new Border
            {
                Width = 274, Padding = new Thickness(8), Margin = new Thickness(0, 0, 8, 8),
                BorderThickness = new Thickness(1), Child = check, Tag = op
            };
            card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            card.SetResourceReference(Border.CornerRadiusProperty, "RowCornerRadius");
            ChoicesPanel.Children.Add(card);
        }
        foreach (var check in _choices)
        {
            check.Checked += (_, _) => UpdateSelection();
            check.Unchecked += (_, _) => UpdateSelection();
        }
        UpdateSelection();
        FilterChoices();
        Loaded += (_, _) => SearchBox.Focus();
    }

    private void UpdateSelection()
    {
        var count = _choices.Count(check => check.IsChecked == true);
        foreach (var check in _choices) check.IsEnabled = check.IsChecked == true || count < 12;
        SelectionCount.Text = $"已选择 {count} / 12 名";
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => FilterChoices();

    private void FilterChoices()
    {
        var candidates = new List<Operator>();
        foreach (var check in _choices)
        {
            var card = (Border)check.Parent;
            var op = (Operator)card.Tag;
            bool matchesName = op.Name.Contains(SearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase);
            if (matchesName) candidates.Add(op);
            card.Visibility = matchesName && _missing.Matches(op) ? Visibility.Visible : Visibility.Collapsed;
        }
        _missing.UpdateCounts(candidates);
        EmptyHint.Visibility = ChoicesPanel.Children.Cast<Border>().Any(card => card.Visibility == Visibility.Visible)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        foreach (var check in _choices) check.IsChecked = false;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Result = _choices.Where(check => check.IsChecked == true).Select(check => (string)check.Tag).ToList();
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
