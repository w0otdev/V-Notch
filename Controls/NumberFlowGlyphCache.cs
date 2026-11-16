using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

namespace VNotch.Controls;

// A control only needs a few alphabets at its current font/size. Keep the small
// immutable drawings across updates, not a FormattedText for every digit slot.
internal sealed class NumberFlowGlyphCache
{
    internal readonly record struct Style(CultureInfo Culture, FlowDirection Direction,
        Typeface Typeface, double Size, Brush Brush, double PixelsPerDip,
        FontNumeralAlignment NumeralAlignment = FontNumeralAlignment.Normal,
        TextFormattingMode FormattingMode = TextFormattingMode.Ideal);
    internal sealed record Image(Drawing Drawing, double Width, double Height, double Baseline);
    private readonly Dictionary<(Style Style, char Character), Image> _images = new();
    internal int Count => _images.Count;

    internal Image Get(Style style, char character)
    {
        // A live brush can change without changing identity. Preserve that live
        // brush during this transition, but leave it out of the cross-update cache.
        bool cacheable = style.Brush.IsFrozen;
        if (cacheable && _images.TryGetValue((style, character), out var image)) return image;
        var drawing = new DrawingGroup();
        if (style.NumeralAlignment == FontNumeralAlignment.Normal)
        {
            var text = new FormattedText(character.ToString(), style.Culture, style.Direction,
                style.Typeface, style.Size, style.Brush, null, style.FormattingMode, style.PixelsPerDip);
            using (var dc = drawing.Open()) dc.DrawText(text, new Point());
            image = new Image(drawing, text.WidthIncludingTrailingWhitespace, text.Height, text.Baseline);
        }
        else
        {
            using var formatter = TextFormatter.Create(style.FormattingMode);
            var source = new NumberFlowTextFormatting.Source(character.ToString(), [style]) { PixelsPerDip = style.PixelsPerDip };
            using var line = formatter.FormatLine(source, 0, 10000, new NumberFlowTextFormatting.Paragraph(style), null);
            using (var dc = drawing.Open()) line.Draw(dc, new Point(), InvertAxes.None);
            image = new Image(drawing, line.WidthIncludingTrailingWhitespace, line.Height, line.Baseline);
        }
        if (cacheable) drawing.Freeze();
        if (cacheable)
        {
            if (_images.Count >= 128) _images.Clear();
            _images[(style, character)] = image;
        }
        return image;
    }

    internal void Clear() => _images.Clear();
}
