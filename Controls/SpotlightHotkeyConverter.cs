using System.Globalization;
using System.Windows.Data;

namespace VNotch.Controls;

public sealed class SpotlightHotkeyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int index and >= 0 and < 9 ? $"Ctrl+{index + 1}" : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
