using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace ASPAssistant.App.Views;

public partial class BossHealthView : UserControl
{
    public static readonly DependencyProperty FitColumnsProperty =
        DependencyProperty.Register(nameof(FitColumns), typeof(bool), typeof(BossHealthView), new PropertyMetadata(false));

    public bool FitColumns
    {
        get => (bool)GetValue(FitColumnsProperty);
        set => SetValue(FitColumnsProperty, value);
    }

    public BossHealthView() => InitializeComponent();
}

public sealed class BossHealthColumnsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Math.Clamp((int)((double)value / 74), 1, 4);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
