using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;

namespace VNotch.Controls;

// A control only needs a few alphabets at its current font/size. Keep the small
// immutable drawings across updates, not a FormattedText for every digit slot.
internal sealed class NumberFlowGlyphCache
{
    internal readonly record struct Style(CultureInfo Culture, FlowDirection Direction,
        Typeface Typeface, double Size, Brush Brush, double PixelsPerDip,
        TextFormattingMode Formatting = TextFormattingMode.Ideal,
        FontNumeralAlignment NumeralAlignment = FontNumeralAlignment.Normal,
        FontNumeralStyle NumeralStyle = FontNumeralStyle.Normal, bool SlashedZero = false);
    internal sealed record Image(Drawing Drawing, double Width, double Height, double Baseline);
    private readonly Dictionary<(Style Style, char Character), Image> _images = new();
    internal int Count => _images.Count;

    internal Image Get(Style style, char character)
    {
        // A live brush can change without changing identity. Preserve that live
        // brush during this transition, but leave it out of the cross-update cache.
        bool cacheable = style.Brush.IsFrozen;
        if (cacheable && _images.TryGetValue((style, character), out var image)) return image;
        // Use the same shaper as the resting TextBlock. FormattedText does not
        // carry Typography.NumeralAlignment and changes the countdown's glyphs.
        var text = new TextBlock
        {
            Text = character.ToString(), Language = XmlLanguage.GetLanguage(style.Culture.IetfLanguageTag),
            FlowDirection = style.Direction, FontFamily = style.Typeface.FontFamily,
            FontStyle = style.Typeface.Style, FontWeight = style.Typeface.Weight,
            FontStretch = style.Typeface.Stretch, FontSize = style.Size, Foreground = style.Brush
        };
        VisualTreeHelper.SetRootDpi(text, new DpiScale(style.PixelsPerDip, style.PixelsPerDip));
        TextOptions.SetTextFormattingMode(text, style.Formatting);
        text.Typography.NumeralAlignment = style.NumeralAlignment;
        text.Typography.NumeralStyle = style.NumeralStyle;
        text.Typography.SlashedZero = style.SlashedZero;
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        text.Arrange(new Rect(text.DesiredSize));
        var drawing = VisualTreeHelper.GetDrawing(text) ?? new DrawingGroup();
        if (cacheable) drawing.Freeze();
        image = new Image(drawing, text.DesiredSize.Width, text.DesiredSize.Height, text.BaselineOffset);
        if (cacheable)
        {
            if (_images.Count >= 128) _images.Clear();
            _images[(style, character)] = image;
        }
        return image;
    }

    internal void Clear() => _images.Clear();
}
