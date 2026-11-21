// Modified for ASP Assistant Arknights on 2026-11-12; see NOTICE.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ASPAssistant.App.Views;
using ASPAssistant.Core.Models;

namespace ASPAssistant.App.Controls;

public partial class OperatorCard : UserControl
{
    public static readonly RoutedEvent TrackingToggledEvent =
        EventManager.RegisterRoutedEvent(
            "TrackingToggled", RoutingStrategy.Bubble,
            typeof(RoutedEventHandler), typeof(OperatorCard));

    public event RoutedEventHandler TrackingToggled
    {
        add => AddHandler(TrackingToggledEvent, value);
        remove => RemoveHandler(TrackingToggledEvent, value);
    }

    public static readonly RoutedEvent BanToggledEvent =
        EventManager.RegisterRoutedEvent(
            "BanToggled", RoutingStrategy.Bubble,
            typeof(RoutedEventHandler), typeof(OperatorCard));

    public event RoutedEventHandler BanToggled
    {
        add => AddHandler(BanToggledEvent, value);
        remove => RemoveHandler(BanToggledEvent, value);
    }

    public Func<string, bool>? IsTrackedCheck { get; set; }
    public bool IsTracked { get; set; }

    public Func<string, bool>? IsBannedCheck { get; set; }
    public Func<string, bool>? IsCovenantBannedCheck { get; set; }
    public bool IsBanned { get; set; }
    private bool IsCovenantBlocked => DataContext is Operator op && IsCovenantBannedCheck?.Invoke(op.Name) == true;

    private bool _isTrackingList;
    public bool IsTrackingList
    {
        get => _isTrackingList;
        set
        {
            _isTrackingList = value;
            RefreshOperatorState();
        }
    }

    public static readonly RoutedEvent AcquiredEvent = EventManager.RegisterRoutedEvent(
        "Acquired", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(OperatorCard));

    public event RoutedEventHandler Acquired
    {
        add => AddHandler(AcquiredEvent, value);
        remove => RemoveHandler(AcquiredEvent, value);
    }

    public static readonly DependencyProperty IsAcquiredProperty = DependencyProperty.Register(
        nameof(IsAcquired), typeof(bool), typeof(OperatorCard),
        new PropertyMetadata(false, (d, _) => ((OperatorCard)d).UpdateTrackButton()));

    public bool IsAcquired
    {
        get => (bool)GetValue(IsAcquiredProperty);
        set => SetValue(IsAcquiredProperty, value);
    }

    private void OnAcquiredClick(object sender, RoutedEventArgs e)
        => RaiseEvent(new RoutedEventArgs(AcquiredEvent, this));

    public static readonly DependencyProperty CatalogColumnsProperty = DependencyProperty.Register(
        nameof(CatalogColumns), typeof(int), typeof(OperatorCard),
        new PropertyMetadata(1, (card, args) => ((OperatorCard)card).ApplyCatalogLayout((int)args.NewValue)));

    public int CatalogColumns
    {
        get => (int)GetValue(CatalogColumnsProperty);
        set => SetValue(CatalogColumnsProperty, value);
    }

    private void ApplyCatalogLayout(int columns)
    {
        bool compact = columns == 3;
        CardBorder.Height = columns switch { 1 => 53, 2 => 53, _ => 51 };
        TierRail.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        TrackingPortrait.Width = TrackingPortrait.Height = columns switch { 1 => 34, 2 => 26, _ => 30 };
        TrackingPortrait.Margin = columns == 1 ? new Thickness(7, 0, 9, 0) : new Thickness(4, 0, 4, 0);
        Grid.SetRowSpan(TrackingPortrait, compact ? 2 : 1);
        NameText.FontSize = columns switch { 1 => 13, 2 => 12, _ => 11 };
        Identity.VerticalAlignment = compact ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        CovenantChips.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(ActionButtons, compact ? 1 : 0);
        Grid.SetColumn(ActionButtons, compact ? 2 : 3);
        ActionButtons.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        ActionButtons.Margin = compact ? new Thickness(0, 0, 0, 2) : new Thickness(0, 0, 4, 0);
        TrackButton.Width = BanButton.Width = columns == 1 ? 28 : 22;
        TrackButton.Height = BanButton.Height = compact ? 20 : 28;
    }

    public OperatorCard()
    {
        InitializeComponent();
        HoverToolTipBehavior.Attach(CardBorder, DetailsToolTip);
        // Details are built on demand and dropped when the pop-up closes.
        CardBorder.ToolTipOpening += (_, _) => DetailsHost.Content = DataContext;
        DetailsToolTip.Closed += (_, _) => DetailsHost.Content = null;
        DataContextChanged += (_, _) => RefreshOperatorState();
        Loaded += (_, _) => RefreshOperatorState();
    }

    private void RefreshOperatorState()
    {
        DetailsToolTip.IsOpen = false;
        if (DataContext is not Operator op) return;

        CovenantChips.ItemsSource = op.CoreCovenants.Select(c => new CovenantChipItem(c, true))
            .Concat(op.AdditionalCovenants.Select(c => new CovenantChipItem(c, false)))
            .ToList();

        // Inherit IsTrackedCheck from parent view if not explicitly set
        if (IsTrackedCheck == null)
            IsTrackedCheck = FindAncestor<OperatorBrowseView>()?.IsTrackedCheck;
        IsTracked = IsTrackingList ? !IsAcquired : IsTrackedCheck != null && IsTrackedCheck(op.Name);

        // Inherit IsBannedCheck from parent view if not explicitly set
        if (IsBannedCheck == null)
            IsBannedCheck = FindAncestor<OperatorBrowseView>()?.IsBannedCheck;
        if (IsCovenantBannedCheck == null)
            IsCovenantBannedCheck = FindAncestor<OperatorBrowseView>()?.IsCovenantBannedCheck;
        IsBanned = IsBannedCheck?.Invoke(op.Name) == true || IsCovenantBlocked;

        TierRail.SetResourceReference(Border.BackgroundProperty,
            op.Tier >= 5 ? "EliteBrush" : op.Tier == 4 ? "AccentSolidBrush" : "BorderBrush");


        UpdateTrackButton();
        UpdateBanButton();
        UpdateBannedDisplay();
    }

    private T? FindAncestor<T>() where T : DependencyObject
    {
        DependencyObject? current = VisualTreeHelper.GetParent(this);
        while (current != null)
        {
            if (current is T t) return t;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        DetailsToolTip.IsOpen = false;
    }

    private void OnTrackClick(object sender, RoutedEventArgs e)
    {
        IsTracked = !IsTracked;
        UpdateTrackButton();
        RaiseEvent(new RoutedEventArgs(TrackingToggledEvent, this));
    }

    public void RefreshTrackedState(bool isTracked)
    {
        IsTracked = isTracked;
        UpdateTrackButton();
    }

    public void RefreshBannedState(bool isBanned)
    {
        IsBanned = isBanned || IsCovenantBlocked;
        UpdateBanButton();
        UpdateBannedDisplay();
    }

    private void OnBanClick(object sender, RoutedEventArgs e)
    {
        if (IsCovenantBlocked) return;
        IsBanned = !IsBanned;
        UpdateBanButton();
        UpdateBannedDisplay();
        RaiseEvent(new RoutedEventArgs(BanToggledEvent, this));
    }

    private void UpdateTrackButton()
    {
        if (IsTrackingList) IsTracked = !IsAcquired;
        AcquiredButton.Tag = IsAcquired ? "acquired" : "";
        var tracked = IsTracked && !IsTrackingList;
        TrackButton.Tag = TryFindResource(IsTrackingList ? "IconClose" : "IconStar");
        TrackButton.ToolTip = IsTrackingList || IsTracked ? "取消追踪" : "追踪干员";
        if (tracked)
        {
            TrackButton.SetResourceReference(ForegroundProperty, "FinalBrush");
            TrackButton.SetResourceReference(BackgroundProperty, "FinalTintBrush");
        }
        else
        {
            TrackButton.ClearValue(ForegroundProperty);
            TrackButton.ClearValue(BackgroundProperty);
        }
        if (tracked)
        {
            TrackingPortrait.SetResourceReference(Avatar.RingProperty, "FinalBrush");
            CardBorder.SetResourceReference(Border.BorderBrushProperty, "FinalBrush");
        }
        else
        {
            TrackingPortrait.ClearValue(Avatar.RingProperty);
            CardBorder.ClearValue(Border.BorderBrushProperty);
        }
        UpdateButtonVisibility();
    }

    /// <summary>Single-column rows have room for ☆ and ⊘ all the time; the tracking list only removes (✕).</summary>
    private void UpdateButtonVisibility()
    {
        TrackButton.Visibility = Visibility.Visible;
        BanButton.Visibility = IsTrackingList ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateBanButton()
    {
        var covenantBlocked = IsCovenantBlocked;
        BanButton.IsEnabled = !covenantBlocked;
        BanButton.ToolTip = covenantBlocked ? "本局盟约禁用，取消对应盟约 Ban 后恢复" : IsBanned ? "取消禁用" : "标记为本局禁用";
        UpdateButtonVisibility();
        if (IsBanned)
        {
            BanButton.SetResourceReference(ForegroundProperty, "DangerBrush");
            BanButton.SetResourceReference(BackgroundProperty, "DangerTintBrush");
        }
        else
        {
            BanButton.ClearValue(ForegroundProperty);
            BanButton.ClearValue(BackgroundProperty);
        }
    }

    private void UpdateBannedDisplay()
    {
        BanBadge.Visibility = IsBanned ? Visibility.Visible : Visibility.Collapsed;
        NameText.TextDecorations = IsBanned ? TextDecorations.Strikethrough : null;
        // Banned operators stay listed but fade back
        CardBorder.Opacity = IsBanned ? 0.5 : 1.0;
    }
}

/// <summary>One covenant label on an operator row.</summary>
public sealed record CovenantChipItem(string Name, bool IsCore);
