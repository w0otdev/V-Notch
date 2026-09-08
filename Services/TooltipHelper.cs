using System.Windows;
using System.Windows.Controls;

namespace VNotch.Services;

/// <summary>
/// Helper class for adding localized tooltips to UI elements
/// </summary>
public static class TooltipHelper
{
    /// <summary>
    /// Sets a localized tooltip on a UI element
    /// </summary>
    public static void SetLocalizedTooltip(FrameworkElement element, string localizationKey)
    {
        if (element == null || string.IsNullOrEmpty(localizationKey))
            return;

        var tooltipText = Loc.Get(localizationKey);
        if (!string.IsNullOrEmpty(tooltipText) && tooltipText != localizationKey)
        {
            var tooltip = new ToolTip
            {
                Content = tooltipText,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse,
                Style = CreateTooltipStyle()
            };
            element.ToolTip = tooltip;
        }
    }

    /// <summary>
    /// Sets a direct tooltip text on a UI element
    /// </summary>
    public static void SetTooltip(FrameworkElement element, string text)
    {
        if (element == null || string.IsNullOrEmpty(text))
            return;

        var tooltip = new ToolTip
        {
            Content = text,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse,
            Style = CreateTooltipStyle()
        };
        element.ToolTip = tooltip;
    }

    /// <summary>
    /// Updates all localized tooltips in the application (useful after language change)
    /// </summary>
    public static void RefreshTooltips(DependencyObject parent)
    {
        if (parent == null)
            return;

        // Refresh tooltips recursively
        int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);

            if (child is FrameworkElement element && element.ToolTip is ToolTip tooltip && tooltip.Tag is string locKey)
            {
                tooltip.Content = Loc.Get(locKey);
            }

            RefreshTooltips(child);
        }
    }

    private static Style CreateTooltipStyle()
    {
        var style = new Style(typeof(ToolTip));

        style.Setters.Add(new Setter(ToolTip.BackgroundProperty,
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(240, 30, 30, 30))));

        style.Setters.Add(new Setter(ToolTip.ForegroundProperty,
            System.Windows.Media.Brushes.White));

        style.Setters.Add(new Setter(ToolTip.BorderBrushProperty,
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(100, 255, 255, 255))));

        style.Setters.Add(new Setter(ToolTip.BorderThicknessProperty, new Thickness(1)));

        style.Setters.Add(new Setter(ToolTip.PaddingProperty, new Thickness(8, 4, 8, 4)));

        style.Setters.Add(new Setter(ToolTip.FontSizeProperty, 12.0));

        style.Setters.Add(new Setter(Control.FontFamilyProperty,
            new System.Windows.Media.FontFamily("Segoe UI")));

        style.Setters.Add(new Setter(ToolTip.HasDropShadowProperty, true));

        style.Setters.Add(new Setter(ToolTip.MaxWidthProperty, 300.0));

        return style;
    }
}
