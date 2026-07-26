using System.Globalization;
using System.Windows.Data;

namespace VNotch.Controls;

/// <summary>
/// Maps a result row's alternation index to its quick-launch chord (Ctrl+1..9);
/// rows past the ninth get no badge.
/// </summary>
public sealed class SpotlightHotkeyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int index and >= 0 and < 9 ? $"Ctrl+{index + 1}" : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
