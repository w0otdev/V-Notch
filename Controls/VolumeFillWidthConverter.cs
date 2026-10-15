using System;
using System.Globalization;
using System.Windows.Data;

namespace VNotch.Controls;

public sealed class VolumeFillWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length == 2 && values[0] is double width && values[1] is double level &&
           double.IsFinite(width) && double.IsFinite(level)
            ? Math.Max(0, width) * Math.Clamp(level, 0, 1)
            : 0d;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
