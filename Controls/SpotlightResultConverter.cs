using System.Globalization;
using System.Windows;
using System.Windows.Data;
using VNotch.Models;
using VNotch.Services.Spotlight;

namespace VNotch.Controls;

public sealed class SpotlightResultConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not SpotlightSearchItem item) return DependencyProperty.UnsetValue;
        var result = new SpotlightResultPresentation(item);
        return parameter switch
        {
            nameof(result.DisplayTitle) => result.DisplayTitle,
            nameof(result.DisplaySubtitle) => result.DisplaySubtitle,
            nameof(result.SolidIcon) => result.SolidIcon,
            nameof(result.Glyph) => result.Glyph,
            nameof(result.SectionTitle) => result.SectionTitle,
            _ => DependencyProperty.UnsetValue
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
