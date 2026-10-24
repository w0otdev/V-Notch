using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace VNotch.Controls;

public sealed partial class VolumeFillWidthConverter : IMultiValueConverter
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_animation_compute_volume_fill_width(
        double width, double level, out double outFillWidth);

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2) return 0d;

        double width = double.NaN;
        double level = double.NaN;
        FrameworkElement? self = null;

        if (values[0] is double w && double.IsFinite(w) && w > 0d)
            width = w;

        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] is FrameworkElement fe)
                self = fe;
            else if (values[i] is double l && double.IsFinite(l))
                level = l;
        }

        if (self != null)
        {
            if (!double.IsFinite(width) || width <= 0d)
            {
                if (self.Parent is AccessibleVolumePanel avp)
                    width = avp.ActualWidth;
                else if (self.Parent is FrameworkElement parent && parent.Parent is FrameworkElement grandParent && grandParent.ActualWidth > 0d)
                    width = grandParent.ActualWidth;
                else if (self.Parent is FrameworkElement p)
                    width = p.ActualWidth;
            }

            if (self.Tag is ScaleTransform st && double.IsFinite(st.ScaleX))
                level = st.ScaleX;
        }

        if (!double.IsFinite(width) || !double.IsFinite(level))
            return 0d;

        return vn_animation_compute_volume_fill_width(width, level, out double fillWidth) == 0 ? fillWidth : 0d;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
