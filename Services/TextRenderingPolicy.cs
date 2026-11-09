using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace VNotch.Services;

internal static class TextRenderingPolicy
{
    static TextRenderingPolicy()
    {
        // Control includes windows, inputs, menus and tooltips. Text primitives
        // also need their own handler because custom popups can open without a
        // placement window. Inline text uses its containing text renderer.
        foreach (Type type in new[] { typeof(Control), typeof(Popup), typeof(TextBlock), typeof(AccessText), typeof(ContentPresenter) })
            EventManager.RegisterClassHandler(type, FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnRootLoaded));
    }

    private static void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        var root = (FrameworkElement)sender;
        SetDefault(root, TextOptions.TextFormattingModeProperty, TextFormattingMode.Ideal);
        // Grayscale antialiasing works on transparent glass. Explicit ClearType
        // settings on opaque setup pages remain in control of their own surface.
        SetDefault(root, TextOptions.TextRenderingModeProperty, TextRenderingMode.Grayscale);
        SetDefault(root, TextOptions.TextHintingModeProperty, TextHintingMode.Animated);
        // Let glyph origins retain fractional positioning. Snapping small labels
        // to the pixel grid makes diagonal strokes and accents look harsher.
        if (root is TextBlock or AccessText)
        {
            root.SetCurrentValue(FrameworkElement.UseLayoutRoundingProperty, false);
            root.SetCurrentValue(UIElement.SnapsToDevicePixelsProperty, false);
        }
    }

    private static void SetDefault(DependencyObject root, DependencyProperty property, object value)
    {
        if (DependencyPropertyHelper.GetValueSource(root, property).BaseValueSource == BaseValueSource.Default)
            root.SetCurrentValue(property, value);
    }

    // The CLR registers the class handlers once, before any UI is constructed.
    internal static void Initialize() { }
}
