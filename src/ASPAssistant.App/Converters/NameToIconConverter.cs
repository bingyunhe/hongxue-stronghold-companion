using System.Globalization;
using System.Windows.Data;

namespace ASPAssistant.App.Converters;

/// <summary>
/// Maps an item name to its icon under <c>data/icons/&lt;parameter&gt;/&lt;name&gt;.png</c>
/// (e.g. ConverterParameter=equipment) and loads it like <see cref="LocalPathToImageConverter"/>.
/// </summary>
public sealed class NameToIconConverter : IValueConverter
{
    private static readonly LocalPathToImageConverter Loader = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string name || string.IsNullOrWhiteSpace(name) || parameter is not string folder
            || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            return null;
        return Loader.Convert($"icons/{folder}/{name}.png", targetType, parameter, culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
