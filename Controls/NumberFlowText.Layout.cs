using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

namespace VNotch.Controls;

public partial class NumberFlowText
{
    private List<Glyph> BuildLayout(string text, string previous)
    {
        if (string.IsNullOrEmpty(text) || text.Length >= 512 || !text.Any(char.IsDigit) ||
            ActualWidth <= 0 || ActualHeight <= 0) return [];

        // Read the layout WPF actually rendered. Independently formatting the
        // same string can wrap a trailing % and ignores tabular numeral styles.
        TextBlock source = text == Text ? this : CreatePreviousLayout(text);
        var drawing = VisualTreeHelper.GetDrawing(source);
        if (drawing == null) return [];
        var parts = NumberFlowValue.Split(text, previous);
        var output = new List<Glyph>(text.Length);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var styles = new NumberFlowGlyphCache.Style[text.Length];
        Array.Fill(styles, new(source.Language.GetEquivalentCulture(), source.FlowDirection,
            new Typeface(source.FontFamily, source.FontStyle, source.FontWeight, source.FontStretch),
            source.FontSize, source.Foreground, dpi, TextOptions.GetTextFormattingMode(source),
            source.Typography.NumeralAlignment, source.Typography.NumeralStyle, source.Typography.SlashedZero));
        int styleIndex = 0;
        void ReadStyles(InlineCollection inlines)
        {
            foreach (var inline in inlines)
            {
                if (inline is Span span) { ReadStyles(span.Inlines); continue; }
                if (inline is LineBreak)
                {
                    if (styleIndex < text.Length && text[styleIndex] == '\r') styleIndex++;
                    if (styleIndex < text.Length && text[styleIndex] == '\n') styleIndex++;
                    continue;
                }
                if (inline is not Run run) continue;
                var style = new NumberFlowGlyphCache.Style(run.Language.GetEquivalentCulture(), run.FlowDirection,
                    new Typeface(run.FontFamily, run.FontStyle, run.FontWeight, run.FontStretch),
                    run.FontSize, run.Foreground, dpi, TextOptions.GetTextFormattingMode(source),
                    Typography.GetNumeralAlignment(run), Typography.GetNumeralStyle(run), Typography.GetSlashedZero(run));
                for (int offset = 0; offset < run.Text.Length && styleIndex < styles.Length; offset++)
                    styles[styleIndex++] = style;
            }
        }
        ReadStyles(source.Inlines);

        int searchStart = 0;
        bool supported = true;
        void ReadGlyphs(Drawing node, Matrix transform)
        {
            if (node is DrawingGroup group)
            {
                if (group.Transform != null) transform = group.Transform.Value * transform;
                foreach (var child in group.Children) ReadGlyphs(child, transform);
            }
            else if (node is GlyphRunDrawing glyphDrawing && glyphDrawing.GlyphRun is { Characters: not null } run)
            {
                string characters = new(run.Characters.ToArray());
                int index = text.IndexOf(characters, searchStart, StringComparison.Ordinal);
                if (index < 0) { supported = false; return; }
                searchStart = index + characters.Length;
                Rect alignment = run.ComputeAlignmentBox();
                for (int offset = 0; offset < characters.Length; offset++)
                {
                    var part = parts[index + offset];
                    if (part.Character is '\n' or '\r') continue;
                    double start = run.GetDistanceFromCaretCharacterHit(new CharacterHit(offset, 0));
                    double end = run.GetDistanceFromCaretCharacterHit(new CharacterHit(offset, 1));
                    if ((run.BidiLevel & 1) != 0) { start = -start; end = -end; }
                    var bounds = Rect.Transform(new Rect(
                        run.BaselineOrigin.X + Math.Min(start, end), run.BaselineOrigin.Y + alignment.Y,
                        Math.Abs(end - start), alignment.Height), transform);
                    if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) continue;
                    var clip = new RectangleGeometry(bounds);
                    clip.Freeze();
                    var slice = new DrawingGroup { ClipGeometry = clip };
                    slice.Children.Add(drawing);
                    var local = new DrawingGroup { Transform = new TranslateTransform(-bounds.X, -bounds.Y) };
                    local.Children.Add(slice);
                    // Keep live foreground brushes live throughout a color flash.
                    var image = new NumberFlowGlyphCache.Image(local, bounds.Width, bounds.Height, -alignment.Y);
                    output.Add(new Glyph(part, bounds, image, styles[index + offset]));
                }
            }
        }
        ReadGlyphs(drawing, Matrix.Identity);
        return supported ? output : [];
    }

    private TextBlock CreatePreviousLayout(string text)
    {
        // Rebuilt views have no old visual to capture. Shape the carried value
        // with native TextBlock layout as well, without constraining a longer
        // previous value to the new value's width.
        var source = new TextBlock
        {
            Text = text, Language = Language, FlowDirection = FlowDirection,
            FontFamily = FontFamily, FontStyle = FontStyle, FontWeight = FontWeight,
            FontStretch = FontStretch, FontSize = FontSize, Foreground = Foreground,
            Padding = Padding, TextAlignment = TextAlignment, TextWrapping = TextWrapping,
            LineHeight = LineHeight, LineStackingStrategy = LineStackingStrategy
        };
        VisualTreeHelper.SetRootDpi(source, VisualTreeHelper.GetDpi(this));
        TextOptions.SetTextFormattingMode(source, TextOptions.GetTextFormattingMode(this));
        source.Typography.NumeralAlignment = Typography.NumeralAlignment;
        source.Typography.NumeralStyle = Typography.NumeralStyle;
        source.Typography.SlashedZero = Typography.SlashedZero;
        source.Measure(new Size(TextWrapping == TextWrapping.NoWrap ? double.PositiveInfinity : ActualWidth,
            double.PositiveInfinity));
        source.Arrange(new Rect(new Size(Math.Max(ActualWidth, source.DesiredSize.Width),
            Math.Max(ActualHeight, source.DesiredSize.Height))));
        return source;
    }
}
