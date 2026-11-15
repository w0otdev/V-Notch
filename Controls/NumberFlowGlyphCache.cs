using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace VNotch.Controls;

// A control only needs a few alphabets at its current font/size. Keep the small
// immutable drawings across updates, not a FormattedText for every digit slot.
internal sealed class NumberFlowGlyphCache
{
    internal readonly record struct Style(CultureInfo Culture, FlowDirection Direction,
        Typeface Typeface, double Size, Brush Brush, double PixelsPerDip);
    internal sealed record Image(Drawing Drawing, double Width, double Height, double Baseline);
    private readonly Dictionary<(Style Style, char Character), Image> _images = new();
    internal int Count => _images.Count;

    internal Image Get(Style style, char character)
    {
        // A live brush can change without changing identity. Preserve that live
        // brush during this transition, but leave it out of the cross-update cache.
        bool cacheable = style.Brush.IsFrozen;
        if (cacheable && _images.TryGetValue((style, character), out var image)) return image;
        var text = new FormattedText(character.ToString(), style.Culture, style.Direction,
            style.Typeface, style.Size, style.Brush, style.PixelsPerDip);
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open()) dc.DrawText(text, new Point());
        if (cacheable) drawing.Freeze();
        image = new Image(drawing, text.WidthIncludingTrailingWhitespace, text.Height, text.Baseline);
        if (cacheable)
        {
            if (_images.Count >= 128) _images.Clear();
            _images[(style, character)] = image;
        }
        return image;
    }

    internal void Clear() => _images.Clear();
}
