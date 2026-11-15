using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

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
            TranslateTransform? Scroll, Row[] Rows);
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
                    _slots[i] = new Slot(reel, drawing, position, null, []);
                    continue;
                }

                var clip = new RectangleGeometry(new Rect(-1, 0, glyph.Bounds.Width + 2, glyph.Bounds.Height));
                clip.Freeze();
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
                _slots[i] = new Slot(reel with { From = reel.From - cycle, To = reel.To - cycle }, drawing, position, scroll, rows);
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
        var formatted = new FormattedText(text, culture, FlowDirection, typeface, FontSize, Foreground, dpi)
        {
            MaxTextWidth = Math.Max(1, ActualWidth - Padding.Left - Padding.Right),
            TextAlignment = TextAlignment,
            Trimming = TextTrimming
        };
        if (!double.IsNaN(LineHeight)) formatted.LineHeight = LineHeight;
        var styles = new NumberFlowGlyphCache.Style[text.Length];
        Array.Fill(styles, new(culture, FlowDirection, typeface, FontSize, Foreground, dpi));
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
                formatted.SetFontSize(run.FontSize, index, length);
                formatted.SetFontTypeface(face, index, length);
                formatted.SetForegroundBrush(run.Foreground, index, length);
                var style = new NumberFlowGlyphCache.Style(culture, FlowDirection, face, run.FontSize, run.Foreground, dpi);
                for (int j = index; j < index + length; j++) styles[j] = style;
                index += length;
            }
        }
        if (text == Text || text.Length == Text?.Length) ApplyInlines(Inlines);
        var output = new List<Glyph>(text.Length);
        foreach (var part in NumberFlowValue.Split(text, previous))
        {
            if (part.Character is '\n' or '\r') continue;
            var bounds = formatted.BuildHighlightGeometry(new Point(Padding.Left, Padding.Top), part.Index, 1)?.Bounds ?? Rect.Empty;
            if (bounds.IsEmpty) continue;
            var style = styles[part.Index];
            var image = _glyphCache.Get(style, part.Character);
            bounds.Y += Math.Max(0, formatted.Baseline - image.Baseline);
            bounds.Height = image.Height;
            output.Add(new(part, bounds, image, style));
        }
        return output;
    }

}
