using System.Windows;
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
        internal Rect Bounds { get; private set; } = Rect.Empty;
        private readonly RectangleGeometry _clip = new();
        private readonly Slot[] _slots;

        private sealed record Slot(Reel Reel, DrawingGroup Drawing, TranslateTransform Position,
            RectangleGeometry? Clip, Row[] Rows);
        private sealed record Row(int Index, DrawingGroup Drawing, TranslateTransform Position, double BaselineOffset, double Width);

        internal ReelScene(List<Reel> reels, NumberFlowGlyphCache cache)
        {
            var liveImages = new Dictionary<(NumberFlowGlyphCache.Style, char), NumberFlowGlyphCache.Image>();
            NumberFlowGlyphCache.Image RollingImage(Glyph glyph, int digit)
            {
                if (digit == glyph.Part.Digit) return glyph.Image;
                char character = (char)(glyph.Part.Character - glyph.Part.Digit + digit);
                if (glyph.Style.Brush.IsFrozen) return cache.Get(glyph.Style, character);
                var key = (glyph.Style, character);
                if (!liveImages.TryGetValue(key, out var image))
                    liveImages[key] = image = cache.Get(glyph.Style, character);
                return image;
            }
            Drawing.ClipGeometry = _clip;
            _slots = new Slot[reels.Count];
            for (int i = 0; i < reels.Count; i++)
            {
                var reel = reels[i];
                var glyph = reel.Glyph;
                var bounds = Bounds;
                bounds.Union(glyph.Bounds);
                bounds.Union(new Rect(reel.FromPoint, new Size(reel.FromWidth, glyph.Bounds.Height)));
                Bounds = bounds;
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
                drawing.ClipGeometry = clip;
                // Normalize the cycle to keep transforms small after repeated retargeting.
                double cycle = Math.Floor(Math.Min(reel.From, reel.To));
                int last = (int)Math.Floor(Math.Max(reel.From, reel.To) - cycle) + 1;
                var rows = new Row[last + 1];
                for (int row = 0; row <= last; row++)
                {
                    int digit = ((int)cycle + row) % glyph.Part.Radix;
                    if (digit < 0) digit += glyph.Part.Radix;
                    // The landing row is the actual shaped text, including inline
                    // baselines, kerning and OpenType numeral variants.
                    var image = RollingImage(glyph, digit);
                    var transform = new TranslateTransform((glyph.Bounds.Width - image.Width) / 2, 0);
                    var rowDrawing = new DrawingGroup { Transform = transform };
                    rowDrawing.Children.Add(image.Drawing);
                    drawing.Children.Add(rowDrawing);
                    rows[row] = new Row(row, rowDrawing, transform, glyph.Image.Baseline - image.Baseline, image.Width);
                }
                _slots[i] = new Slot(reel with { From = reel.From - cycle, To = reel.To - cycle }, drawing, position, clip, rows);
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
                double tolerance = 1 / (64 * reel.Glyph.Style.PixelsPerDip);
                SetPosition(slot.Position, Settle(Lerp(reel.FromPoint.X, bounds.X, progress), bounds.X, tolerance),
                    Settle(Lerp(reel.FromPoint.Y, bounds.Y, progress), bounds.Y, tolerance));
                double alpha = Lerp(reel.FromOpacity, 1, progress);
                if (slot.Drawing.Opacity != alpha) slot.Drawing.Opacity = alpha;
                if (slot.Rows.Length == 0) continue;
                // Preserve the current advance width when proportional digits
                // change or an in-flight transition is retargeted.
                double width = Settle(Lerp(reel.FromWidth, bounds.Width, progress), bounds.Width, tolerance);
                var slotClip = new Rect(-1, 0, width + 2, bounds.Height);
                if (slot.Clip!.Rect != slotClip) slot.Clip.Rect = slotClip;
                double value = Settle(Lerp(reel.From, reel.To, progress), reel.To, tolerance / bounds.Height);
                int first = (int)Math.Floor(value);
                foreach (var row in slot.Rows)
                {
                    alpha = row.Index == first || row.Index == first + 1
                        ? Math.Clamp(1 - Math.Abs(row.Index - value) * .65, 0, 1) : 0;
                    if (row.Drawing.Opacity != alpha) row.Drawing.Opacity = alpha;
                    // Keep the visible row near zero. Subtracting two tall strip
                    // transforms in the compositor introduced a one-pixel jump
                    // when WPF's native baseline guidelines took over at rest.
                    double y = (row.Index - value) * bounds.Height + row.BaselineOffset;
                    if (alpha > 0) SetPosition(row.Position, (width - row.Width) / 2, y);
                }
            }
        }

        private static double Settle(double value, double target, double tolerance) =>
            Math.Abs(value - target) < tolerance ? target : value;

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
        // A shorter target must not cut off the suffix while it travels from
        // the wider previous layout. Ancestor clips still apply below.
        clip.Union(_scene.Bounds);
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

}
