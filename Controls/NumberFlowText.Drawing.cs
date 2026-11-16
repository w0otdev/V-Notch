using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

namespace VNotch.Controls;

public partial class NumberFlowText
{
    private readonly NumberFlowGlyphCache _glyphCache = new();
    private ReelScene? _scene;

    // WPF retains these drawings. Animation changes transforms/opacity only;
    // it never reformats text or records a fresh display list for every frame.
    private sealed class ReelScene
    {
        internal DrawingGroup Drawing { get; } = new();
        private readonly RectangleGeometry _clip = new();
        private readonly Slot[] _slots;

        private sealed record Slot(Reel Reel, DrawingGroup Drawing, TranslateTransform Position,
            TranslateTransform? Scroll, RectangleGeometry? Clip, Row[] Rows);
        private sealed record Row(int Index, DrawingGroup Drawing);

        internal ReelScene(List<Reel> reels, NumberFlowGlyphCache cache)
        {
            Drawing.ClipGeometry = _clip;
            _slots = new Slot[reels.Count];
            for (int i = 0; i < reels.Count; i++)
            {
                var reel = reels[i];
                var glyph = reel.Glyph;
                var position = new TranslateTransform();
                var drawing = new DrawingGroup { Transform = position };
                Drawing.Children.Add(drawing);
                if (glyph.Part.Digit < 0 || Math.Abs(reel.To - reel.From) < .001)
                {
                    drawing.Children.Add(glyph.Image.Drawing);
                    _slots[i] = new Slot(reel, drawing, position, null, null, []);
                    continue;
                }

                var clip = new RectangleGeometry(new Rect(-1, 0, glyph.Bounds.Width + 2, glyph.Bounds.Height));
                drawing.ClipGeometry = clip;
                var scroll = new TranslateTransform();
                var strip = new DrawingGroup { Transform = scroll };
                drawing.Children.Add(strip);
                // Normalize the cycle to keep transforms small after repeated retargeting.
                double cycle = Math.Floor(Math.Min(reel.From, reel.To));
                int last = (int)Math.Floor(Math.Max(reel.From, reel.To) - cycle) + 1;
                var rows = new Row[last + 1];
                for (int row = 0; row <= last; row++)
                {
                    int digit = ((int)cycle + row) % glyph.Part.Radix;
                    if (digit < 0) digit += glyph.Part.Radix;
                    var image = cache.Get(glyph.Style, (char)(glyph.Part.Character - glyph.Part.Digit + digit));
                    var transform = new TranslateTransform((glyph.Bounds.Width - image.Width) / 2, row * glyph.Bounds.Height);
                    transform.Freeze();
                    var rowDrawing = new DrawingGroup { Transform = transform };
                    rowDrawing.Children.Add(image.Drawing);
                    strip.Children.Add(rowDrawing);
                    rows[row] = new Row(row, rowDrawing);
                }
                _slots[i] = new Slot(reel with { From = reel.From - cycle, To = reel.To - cycle }, drawing, position, scroll, clip, rows);
            }
        }

        internal void Update(double progress, Rect clip, double opacity)
        {
            if (_clip.Rect != clip) _clip.Rect = clip;
            if (Drawing.Opacity != opacity) Drawing.Opacity = opacity;
            foreach (var slot in _slots)
            {
                var reel = slot.Reel;
                var bounds = reel.Glyph.Bounds;
                SetPosition(slot.Position, Lerp(reel.FromPoint.X, bounds.X, progress), Lerp(reel.FromPoint.Y, bounds.Y, progress));
                double alpha = Lerp(reel.FromOpacity, 1, progress);
                if (slot.Drawing.Opacity != alpha) slot.Drawing.Opacity = alpha;
                if (slot.Scroll == null) continue;
                // Center against the current width, including when an in-flight
                // reel is retargeted. Using the final width shifts the old digit
                // immediately (most visible for proportional 1 <-> 8).
                double width = Lerp(reel.FromWidth, bounds.Width, progress);
                double x = (width - bounds.Width) / 2;
                if (slot.Scroll.X != x) slot.Scroll.X = x;
                var slotClip = new Rect(-1, 0, width + 2, bounds.Height);
                if (slot.Clip!.Rect != slotClip) slot.Clip.Rect = slotClip;
                double value = Lerp(reel.From, reel.To, progress);
                double y = -value * bounds.Height;
                if (slot.Scroll.Y != y) slot.Scroll.Y = y;
                int first = (int)Math.Floor(value);
                foreach (var row in slot.Rows)
                {
                    alpha = row.Index == first || row.Index == first + 1
                        ? Math.Clamp(1 - Math.Abs(row.Index - value) * .65, 0, 1) : 0;
                    if (row.Drawing.Opacity != alpha) row.Drawing.Opacity = alpha;
                }
            }
        }

        private static void SetPosition(TranslateTransform transform, double x, double y)
        {
            if (transform.X != x) transform.X = x;
            if (transform.Y != y) transform.Y = y;
        }
    }

    private void UpdateAnimationFrame()
    {
        if (_scene == null) return;
        var clip = new Rect(RenderSize);
        double opacity = Opacity;
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is Visual visual; parent = VisualTreeHelper.GetParent(parent))
        {
            if (_layer != null && ReferenceEquals(parent, VisualTreeHelper.GetParent(_layer))) break;
            if (parent is UIElement element)
            {
                opacity *= element.Opacity;
                if (element.ClipToBounds || element.Clip != null)
                {
                    var bounds = element.Clip?.Bounds ?? new Rect(element.RenderSize);
                    clip.Intersect(visual.TransformToVisual(this).TransformBounds(bounds));
                }
            }
            if (parent is Window) break;
        }
        _scene.Update(AnimationProgress, clip.IsEmpty ? new Rect() : clip, opacity);
    }

    private void RenderReels(DrawingContext dc)
    {
        if (_scene != null) dc.DrawDrawing(_scene.Drawing);
    }

    private List<Glyph> BuildLayout(string text, string previous)
    {
        if (string.IsNullOrEmpty(text) || text.Length >= 512 || !text.Any(char.IsDigit) ||
            ActualWidth <= 0 || ActualHeight <= 0) return [];
        var culture = Language.GetEquivalentCulture();
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var mode = TextOptions.GetTextFormattingMode(this);
        var styles = new NumberFlowGlyphCache.Style[text.Length];
        Array.Fill(styles, new(culture, FlowDirection, typeface, FontSize, Foreground, dpi,
            Typography.GetNumeralAlignment(this), mode));
        int index = 0;
        void ApplyInlines(InlineCollection inlines)
        {
            foreach (var inline in inlines)
            {
                if (inline is Span span) { ApplyInlines(span.Inlines); continue; }
                if (inline is LineBreak) { index++; continue; }
                if (inline is not Run run) continue;
                int length = Math.Min(run.Text.Length, text.Length - index);
                if (length <= 0) continue;
                var face = new Typeface(run.FontFamily, run.FontStyle, run.FontWeight, run.FontStretch);
                var style = new NumberFlowGlyphCache.Style(culture, FlowDirection, face, run.FontSize, run.Foreground, dpi,
                    Typography.GetNumeralAlignment(run), mode);
                for (int j = index; j < index + length; j++) styles[j] = style;
                index += length;
            }
        }
        if (text == Text || text.Length == Text?.Length) ApplyInlines(Inlines);
        var output = new List<Glyph>(text.Length);
        var parts = NumberFlowValue.Split(text, previous);
        double width = Math.Max(1, ActualWidth - Padding.Left - Padding.Right);
        if (styles.All(style => style.NumeralAlignment == FontNumeralAlignment.Normal))
        {
            var formatted = new FormattedText(text, culture, FlowDirection, typeface, FontSize, Foreground, null, mode, dpi)
            {
                MaxTextWidth = width,
                TextAlignment = TextAlignment,
                Trimming = TextTrimming
            };
            if (!double.IsNaN(LineHeight)) formatted.LineHeight = LineHeight;
            for (int i = 0; i < styles.Length;)
            {
                int end = i + 1;
                while (end < styles.Length && styles[end] == styles[i]) end++;
                formatted.SetFontSize(styles[i].Size, i, end - i);
                formatted.SetFontTypeface(styles[i].Typeface, i, end - i);
                formatted.SetForegroundBrush(styles[i].Brush, i, end - i);
                i = end;
            }
            DrawingGroup? context = null;
            foreach (var part in parts)
            {
                if (part.Character is '\n' or '\r') continue;
                var bounds = formatted.BuildHighlightGeometry(new Point(Padding.Left, Padding.Top), part.Index, 1)?.Bounds ?? Rect.Empty;
                if (bounds.IsEmpty) continue;
                var style = styles[part.Index];
                if (part.Character == ':')
                {
                    if (context == null)
                    {
                        context = new DrawingGroup();
                        using var dc = context.Open();
                        dc.DrawText(formatted, new Point(Padding.Left, Padding.Top));
                    }
                    output.Add(new(part, bounds, ContextualImage(context, bounds), style));
                    continue;
                }
                var image = _glyphCache.Get(style, part.Character);
                bounds.Y += Math.Max(0, formatted.Baseline - image.Baseline);
                bounds.Height = image.Height;
                output.Add(new(part, bounds, image, style));
            }
            return output;
        }
        using var formatter = TextFormatter.Create(mode);
        var source = new NumberFlowTextFormatting.Source(text, styles) { PixelsPerDip = dpi };
        var paragraph = new NumberFlowTextFormatting.Paragraph(styles[0], TextAlignment, TextWrapping,
            double.IsNaN(LineHeight) ? 0 : LineHeight);
        double y = Padding.Top;
        for (int start = 0; start < text.Length;)
        {
            using var line = formatter.FormatLine(source, start, width, paragraph, null);
            // Keep WPF's native ellipsis when there is no complete row to roll.
            if (TextTrimming != TextTrimming.None && line.HasOverflowed) return [];
            if (line.Length <= 0) break;
            DrawingGroup? context = null;
            for (int i = start; i < Math.Min(text.Length, start + line.Length); i++)
            {
                var part = parts[i];
                if (part.Character is '\n' or '\r') continue;
                var bounds = line.GetTextBounds(i, 1)[0].Rectangle;
                var style = styles[i];
                if (part.Character == ':')
                {
                    if (context == null)
                    {
                        context = new DrawingGroup();
                        using var dc = context.Open();
                        line.Draw(dc, new Point(Padding.Left, y), InvertAxes.None);
                    }
                    bounds.Offset(Padding.Left, y);
                    output.Add(new(part, bounds, ContextualImage(context, bounds), style));
                    continue;
                }
                var image = _glyphCache.Get(style, part.Character);
                bounds.X += Padding.Left;
                bounds.Y = y + line.Baseline - image.Baseline;
                bounds.Height = image.Height;
                output.Add(new(part, bounds, image, style));
            }
            start += line.Length;
            y += line.Height;
        }
        return output;
    }

    private static NumberFlowGlyphCache.Image ContextualImage(Drawing context, Rect bounds)
    {
        // SF Pro positions a clock colon through contextual shaping. Extract its
        // already-shaped cell from the full line; formatting ':' alone loses
        // that placement and makes it jump at each native/reel handoff.
        var clip = new RectangleGeometry(bounds);
        clip.Freeze();
        var transform = new TranslateTransform(-bounds.X, -bounds.Y);
        transform.Freeze();
        var drawing = new DrawingGroup { ClipGeometry = clip, Transform = transform };
        drawing.Children.Add(context);
        return new(drawing, bounds.Width, bounds.Height, 0);
    }

}
