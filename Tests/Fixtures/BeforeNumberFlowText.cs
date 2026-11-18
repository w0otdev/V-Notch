// Paired rendering/allocation reference from NumberFlowText before the resource optimization (4feb4d38).
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Services;

namespace VNotch.Tests.Fixtures;

/// <summary>
/// Native TextBlock semantics and layout with a clipped, place-value digit reel on updates.
/// Text/bindings/automation always expose the final value, never interpolated fake numbers.
/// </summary>
public class BeforeNumberFlowText : TextBlock
{
    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        "Progress", typeof(double), typeof(BeforeNumberFlowText),
        new FrameworkPropertyMetadata(1d, (d, _) => ((BeforeNumberFlowText)d)._adorner?.InvalidateVisual()));
    public static readonly DependencyProperty IsNumberFlowEnabledProperty = DependencyProperty.Register(
        nameof(IsNumberFlowEnabled), typeof(bool), typeof(BeforeNumberFlowText),
        new PropertyMetadata(true, (d, e) => { if (e.NewValue is false) ((BeforeNumberFlowText)d).Finish(); }));

    public bool IsNumberFlowEnabled
    {
        get => (bool)GetValue(IsNumberFlowEnabledProperty);
        set => SetValue(IsNumberFlowEnabledProperty, value);
    }

    private sealed record Glyph(NumberFlowValue.Part Part, Rect Bounds, FormattedText Text,
        FormattedText[]? Digits);
    private sealed record Reel(Glyph Glyph, double From, double To, Point FromPoint, double FromOpacity);
    private sealed record Position(Glyph Glyph, double Value, Point Point, double Opacity);
    private List<Glyph> _layout = [];
    private List<Reel> _reels = [];
    private Dictionary<string, Position>? _previous;
    private string? _fromText;
    private string _lastText = "";
    private bool _pending;
    private bool _queued;
    private NumberAdorner? _adorner;
    private AdornerLayer? _layer;
    private int _version;
    internal bool IsAnimating => _reels.Count > 0;
    internal double AnimationProgress => (double)GetValue(ProgressProperty);
    private bool CanAnimate => IsNumberFlowEnabled && IsLoaded && IsVisible &&
        !AnimationConfig.ReduceMotion && AnimationConfig.ClientAreaAnimation && !SystemParameters.HighContrast;

    static BeforeNumberFlowText()
    {
        TextProperty.OverrideMetadata(typeof(BeforeNumberFlowText), new FrameworkPropertyMetadata("", OnTextChanged));
    }

    public BeforeNumberFlowText()
    {
        // Derived TextBlocks otherwise miss the app's implicit typography style.
        SetResourceReference(StyleProperty, typeof(TextBlock));
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => { if (!IsVisible) Finish(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AnimationConfig.ReduceMotionChanged += OnMotionChanged;
        SystemParameters.StaticPropertyChanged += OnSystemChanged;
        LayoutUpdated += OnLayoutUpdated;
        UpdateLayout();
        _lastText = Text ?? "";
        _layout = BuildLayout(Text ?? "", "");
        if (_fromText != null) { _pending = true; QueuePresentation(); }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AnimationConfig.ReduceMotionChanged -= OnMotionChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemChanged;
        LayoutUpdated -= OnLayoutUpdated;
        Finish();
        _layout.Clear();
    }

    private void OnSystemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast)) OnMotionChanged();
    }

    private void OnMotionChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(OnMotionChanged); return; }
        if (!CanAnimate) Finish();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        // Run.Text changes defer the aggregate Text property; observe the settled inline text too.
        string text = Text ?? "";
        if (_pending || text == _lastText) return;
        if (CanAnimate && _lastText.Any(char.IsDigit) && text.Any(char.IsDigit))
        {
            _previous = Capture();
            _fromText = _lastText;
            _pending = true;
            QueuePresentation();
        }
        else { _lastText = text; _layout.Clear(); }
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (BeforeNumberFlowText)d;
        if (!view.CanAnimate)
        {
            view.Finish();
            view._lastText = view.Text ?? "";
            view._layout.Clear();
            return;
        }
        if (!view._pending)
        {
            view._previous = view.Capture();
            view._fromText = view._lastText;
        }
        view._pending = true;
        view.QueuePresentation();
    }

    // Rebuilt quota rows can carry their last presented value into the new visual.
    internal void AnimateFrom(string previous)
    {
        if (previous == Text || !previous.Any(char.IsDigit)) return;
        _fromText = previous;
        _previous = null;
        _pending = true;
        QueuePresentation();
    }

    internal void Finish()
    {
        ++_version;
        BeginAnimation(ProgressProperty, null);
        SetValue(ProgressProperty, 1d);
        if (_adorner != null) _layer?.Remove(_adorner);
        _adorner = null;
        _layer = null;
        BeginAnimation(OpacityMaskProperty, null);
        _reels.Clear();
        _previous = null;
        _fromText = null;
        _pending = false;
        InvalidateVisual();
    }

    private Dictionary<string, Position> Capture()
    {
        if (!IsAnimating) return _layout.ToDictionary(g => g.Part.Key,
            g => new Position(g, g.Part.Digit, g.Bounds.TopLeft, 1));
        double progress = AnimationProgress;
        return _reels.ToDictionary(r => r.Glyph.Part.Key, r => new Position(r.Glyph,
            Lerp(r.From, r.To, progress), new Point(Lerp(r.FromPoint.X, r.Glyph.Bounds.X, progress),
                Lerp(r.FromPoint.Y, r.Glyph.Bounds.Y, progress)), Lerp(r.FromOpacity, 1, progress)));
    }

    private void QueuePresentation()
    {
        if (_queued || !IsLoaded) return;
        _queued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _queued = false;
            if (!_pending) return;
            UpdateLayout();
            Present();
        }));
    }

    private void Present()
    {
        string text = Text ?? "";
        var layer = AdornerLayer.GetAdornerLayer(this);
        if (_pending && CanAnimate && text.Length < 512 && layer != null &&
            text != _fromText && text.Any(char.IsDigit) && (_fromText ?? "").Any(char.IsDigit))
        {
            var old = _previous is { Count: > 0 } ? _previous :
                BuildLayout(_fromText ?? _lastText, "").ToDictionary(g => g.Part.Key,
                    g => new Position(g, g.Part.Digit, g.Bounds.TopLeft, 1));
            _layout = BuildLayout(text, _fromText ?? _lastText);
            _lastText = text;
            _reels = _layout.Select(g =>
            {
                if (!old.TryGetValue(g.Part.Key, out var p))
                    return new Reel(g, g.Part.Digit, g.Part.Digit, g.Bounds.TopLeft, 0);
                bool digits = g.Part.Digit >= 0 && p.Glyph.Part.Digit >= 0;
                double to = digits ? NumberFlowValue.Destination(p.Value, g.Part.Digit, g.Part.Trend, g.Part.Radix) : g.Part.Digit;
                return new Reel(g, digits ? p.Value : g.Part.Digit, to, p.Point,
                    digits || p.Glyph.Part.Character == g.Part.Character ? p.Opacity : 0);
            }).ToList();
            _pending = false;
            _previous = null;
            _fromText = null;
            _layer = layer;
            if (_adorner == null)
            {
                _adorner = new NumberAdorner(this) { IsHitTestVisible = false };
                layer.Add(_adorner);
                var hide = new ObjectAnimationUsingKeyFrames();
                hide.KeyFrames.Add(new DiscreteObjectKeyFrame(Brushes.Transparent, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                BeginAnimation(OpacityMaskProperty, hide);
            }
            BeginAnimation(ProgressProperty, null);
            SetValue(ProgressProperty, 1d);
            int version = ++_version;
            var animation = AnimationPrimitives.MakeAnim(0, 1, AnimationPrimitives._dur500,
                AnimationPrimitives._easeUiOut, null);
            animation.Completed += (_, _) => { if (version == _version) Finish(); };
            BeginAnimation(ProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }

        else { Finish(); _lastText = text; }
    }

    private sealed class NumberAdorner(BeforeNumberFlowText owner) : Adorner(owner)
    {
        protected override void OnRender(DrawingContext drawingContext) => owner.RenderReels(drawingContext);
        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
    }

    private void RenderReels(DrawingContext dc)
    {
        // Clip in this visual, so parent transforms, opacity, glass and scroll clips still apply.
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
        if (clip.IsEmpty) return;
        dc.PushOpacity(opacity);
        dc.PushClip(new RectangleGeometry(clip));
        double progress = AnimationProgress;
        foreach (var reel in _reels)
        {
            var glyph = reel.Glyph;
            double x = Lerp(reel.FromPoint.X, glyph.Bounds.X, progress);
            double y = Lerp(reel.FromPoint.Y, glyph.Bounds.Y, progress);
            dc.PushOpacity(Lerp(reel.FromOpacity, 1, progress));
            if (glyph.Digits == null || Math.Abs(reel.To - reel.From) < .001)
                dc.DrawText(glyph.Text, new Point(x, y));
            else
            {
                double position = Lerp(reel.From, reel.To, progress);
                int first = (int)Math.Floor(position);
                double height = glyph.Bounds.Height;
                dc.PushClip(new RectangleGeometry(new Rect(x - 1, y, glyph.Bounds.Width + 2, height)));
                for (int row = first; row <= first + 1; row++)
                {
                    int radix = glyph.Digits.Length;
                    var digit = glyph.Digits[(row % radix + radix) % radix];
                    double offset = (row - position) * height;
                    // Soft edges avoid the hard slot-machine cut at the top and bottom.
                    dc.PushOpacity(Math.Clamp(1 - Math.Abs(offset) / height * .65, 0, 1));
                    dc.DrawText(digit, new Point(x + (glyph.Bounds.Width - digit.WidthIncludingTrailingWhitespace) / 2, y + offset));
                    dc.Pop();
                }
                dc.Pop();
            }
            dc.Pop();
        }
        dc.Pop();
        dc.Pop();
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
        var styles = Enumerable.Repeat((FontSize, typeface, Foreground), text.Length).ToArray();
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
                for (int j = index; j < index + length; j++) styles[j] = (run.FontSize, face, run.Foreground);
                index += length;
            }
        }
        if (text == Text || text.Length == Text?.Length) ApplyInlines(Inlines);
        var output = new List<Glyph>(text.Length);
        foreach (var part in NumberFlowValue.Split(text, previous))
        {
            if (part.Character is '\n' or '\r') continue;
            var geometry = formatted.BuildHighlightGeometry(new Point(Padding.Left, Padding.Top), part.Index, 1);
            if (geometry == null || geometry.Bounds.IsEmpty) continue;
            var (size, face, brush) = styles[part.Index];
            FormattedText Make(char value) => new(value.ToString(), culture, FlowDirection, face, size, brush, dpi);
            var glyph = Make(part.Character);
            var bounds = geometry.Bounds;
            // Highlight rectangles use the line height; align mixed-size runs on their baseline.
            bounds.Y += Math.Max(0, formatted.Baseline - glyph.Baseline);
            bounds.Height = glyph.Height;
            var digits = part.Digit >= 0
                ? Enumerable.Range(0, part.Radix).Select(n => Make((char)(part.Character - part.Digit + n))).ToArray() : null;
            output.Add(new(part, bounds, glyph, digits));
        }
        return output;
    }

    private static double Lerp(double from, double to, double progress) => from + (to - from) * progress;
}
