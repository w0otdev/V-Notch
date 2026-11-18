using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Services;

namespace VNotch.Controls;

/// <summary>
/// Native TextBlock semantics and layout with a clipped, place-value digit reel on updates.
/// Text/bindings/automation always expose the final value, never interpolated fake numbers.
/// </summary>
public partial class NumberFlowText : TextBlock
{
    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        "Progress", typeof(double), typeof(NumberFlowText),
        new FrameworkPropertyMetadata(1d, (d, _) => ((NumberFlowText)d).UpdateAnimationFrame()));
    public static readonly DependencyProperty IsNumberFlowEnabledProperty = DependencyProperty.Register(
        nameof(IsNumberFlowEnabled), typeof(bool), typeof(NumberFlowText),
        new PropertyMetadata(true, (d, e) => { if (e.NewValue is false) ((NumberFlowText)d).Finish(); }));

    public bool IsNumberFlowEnabled
    {
        get => (bool)GetValue(IsNumberFlowEnabledProperty);
        set => SetValue(IsNumberFlowEnabledProperty, value);
    }

    private sealed record Glyph(NumberFlowValue.Part Part, Rect Bounds, NumberFlowGlyphCache.Image Image,
        NumberFlowGlyphCache.Style Style);
    private sealed record Reel(Glyph Glyph, double From, double To, Point FromPoint, double FromOpacity, double FromWidth);
    private sealed record Position(Glyph Glyph, double Value, Point Point, double Opacity, double Width);
    private List<Glyph> _layout = [];
    private List<Reel> _reels = [];
    private Dictionary<string, Position>? _previous;
    private Point? _previousOrigin;
    private Size _layoutSize;
    private string? _fromText;
    private string _lastText = "";
    private bool _pending;
    private DispatcherOperation? _presentation;
    private NumberAdorner? _adorner;
    private AdornerLayer? _layer;
    private int _version;
    internal bool IsAnimating => _reels.Count > 0;
    internal double AnimationProgress => (double)GetValue(ProgressProperty);
    private bool CanAnimate => IsNumberFlowEnabled && IsLoaded && IsVisible &&
        !AnimationConfig.ReduceMotion && AnimationConfig.ClientAreaAnimation && !SystemParameters.HighContrast;

    static NumberFlowText()
    {
        TextProperty.OverrideMetadata(typeof(NumberFlowText), new FrameworkPropertyMetadata("", OnTextChanged));
    }

    public NumberFlowText()
    {
        // Derived TextBlocks otherwise miss the app's implicit typography style.
        SetResourceReference(StyleProperty, typeof(TextBlock));
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) return;
            Finish();
            _layout.Clear();
            _glyphCache.Clear();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        MotionListeners.Add(this);
        LayoutUpdated -= OnLayoutUpdated;
        LayoutUpdated += OnLayoutUpdated;
        _lastText = Text ?? "";
        // Remember the original width and inline formatting before the first edit.
        // Only visible characters are prepared; rolling alphabets stay lazy.
        _layout = BuildLayout(_lastText, "");
        _layoutSize = RenderSize;
        if (_fromText != null) { _pending = true; QueuePresentation(); }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        MotionListeners.Remove(this);
        LayoutUpdated -= OnLayoutUpdated;
        Finish();
        _layout.Clear();
        _glyphCache.Clear();
    }

    // Rows can be replaced while WPF is broadcasting Loaded. A static event
    // must not keep a detached row (and its owning window) alive if Unloaded
    // is missed. The weak table also makes repeated Loaded notifications safe.
    private static class MotionListeners
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NumberFlowText, object> Views = new();

        static MotionListeners()
        {
            AnimationConfig.ReduceMotionChanged += OnChanged;
            SystemParameters.StaticPropertyChanged += OnSystemChanged;
        }

        internal static void Add(NumberFlowText view) => Views.GetValue(view, static _ => new object());
        internal static void Remove(NumberFlowText view) => Views.Remove(view);

        private static void OnChanged()
        {
            foreach (var entry in Views) entry.Key.OnMotionChanged();
        }

        private static void OnSystemChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast)) OnChanged();
        }
    }

    private void OnMotionChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(OnMotionChanged); return; }
        if (!CanAnimate) Finish();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        // Run.Text changes defer the aggregate Text property; observe the settled inline text too.
        if (_pending) { QueuePresentation(); return; }
        string text = Text ?? "";
        if (text == _lastText)
        {
            if (_layoutSize != RenderSize)
            {
                Finish();
                _layout = BuildLayout(text, "");
                _layoutSize = RenderSize;
            }
            return;
        }
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
        var view = (NumberFlowText)d;
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
        _previousOrigin = null;
        _pending = true;
        QueuePresentation();
    }

    internal void Finish()
    {
        if (_adorner == null && !_pending && _presentation == null) return;
        ++_version;
        _presentation?.Abort();
        _presentation = null;
        _scene = null;
        BeginAnimation(ProgressProperty, null);
        SetValue(ProgressProperty, 1d);
        if (_adorner != null) _layer?.Remove(_adorner);
        _adorner = null;
        _layer = null;
        BeginAnimation(OpacityMaskProperty, null);
        _reels.Clear();
        _previous = null;
        _previousOrigin = null;
        _fromText = null;
        _pending = false;
        if (_lastText != Text) _layout.Clear();
        _lastText = Text ?? "";
        InvalidateVisual();
    }

    private Dictionary<string, Position>? Capture()
    {
        _previousOrigin = PresentationOrigin();
        // Inline edits can notify Text while their formatting is synchronizing.
        // Use the settled layout, never reconstruct it inside that notification.
        if (!IsAnimating && _layout.Count == 0) return null;
        if (!IsAnimating) return _layout.ToDictionary(g => g.Part.Key,
            g => new Position(g, g.Part.Digit, g.Bounds.TopLeft, 1, g.Bounds.Width));
        double progress = AnimationProgress;
        return _reels.ToDictionary(r => r.Glyph.Part.Key, r => new Position(r.Glyph,
            Lerp(r.From, r.To, progress), new Point(Lerp(r.FromPoint.X, r.Glyph.Bounds.X, progress),
                Lerp(r.FromPoint.Y, r.Glyph.Bounds.Y, progress)), Lerp(r.FromOpacity, 1, progress),
            Lerp(r.FromWidth, r.Glyph.Bounds.Width, progress)));
    }

    private Point? PresentationOrigin()
    {
        var layer = AdornerLayer.GetAdornerLayer(this);
        return layer == null ? null : TransformToVisual(layer).Transform(new Point());
    }

    private void QueuePresentation()
    {
        if (_presentation != null || !IsLoaded) return;
        // Loaded priority runs after WPF's pending layout/render work. Forcing
        // UpdateLayout here made every number flush the entire dispatcher tree.
        _presentation = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _presentation = null;
            if (!_pending) return;
            if (!IsMeasureValid || !IsArrangeValid) return;
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
                    g => new Position(g, g.Part.Digit, g.Bounds.TopLeft, 1, g.Bounds.Width));
            _layout = BuildLayout(text, _fromText ?? _lastText);
            if (_layout.Count == 0) { Finish(); return; }
            _layoutSize = RenderSize;
            _lastText = text;
            Vector shift = _previous is { Count: > 0 } && _previousOrigin is Point origin &&
                PresentationOrigin() is Point current ? origin - current : new Vector();
            _reels = _layout.Select(g =>
            {
                if (!old.TryGetValue(g.Part.Key, out var p))
                    return new Reel(g, g.Part.Digit, g.Part.Digit, g.Bounds.TopLeft, 0, g.Bounds.Width);
                bool digits = g.Part.Digit >= 0 && p.Glyph.Part.Digit >= 0;
                double to = digits ? NumberFlowValue.Destination(p.Value, g.Part.Digit, g.Part.Trend, g.Part.Radix) : g.Part.Digit;
                return new Reel(g, digits ? p.Value : g.Part.Digit, to, p.Point + shift,
                    digits || p.Glyph.Part.Character == g.Part.Character ? p.Opacity : 0, p.Width);
            }).ToList();
            _pending = false;
            _previous = null;
            _previousOrigin = null;
            _fromText = null;
            _layer = layer;
            _scene = new ReelScene(_reels, _glyphCache);
            if (_adorner == null)
            {
                _adorner = new NumberAdorner(this)
                {
                    IsHitTestVisible = false, SnapsToDevicePixels = SnapsToDevicePixels,
                    UseLayoutRounding = UseLayoutRounding
                };
                TextOptions.SetTextRenderingMode(_adorner, TextOptions.GetTextRenderingMode(this));
                TextOptions.SetTextHintingMode(_adorner, TextOptions.GetTextHintingMode(this));
                layer.Add(_adorner);
                var hide = new ObjectAnimationUsingKeyFrames();
                hide.KeyFrames.Add(new DiscreteObjectKeyFrame(Brushes.Transparent, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                BeginAnimation(OpacityMaskProperty, hide);
            }
            _adorner.InvalidateVisual();
            BeginAnimation(ProgressProperty, null);
            SetValue(ProgressProperty, 1d);
            int version = ++_version;
            var animation = AnimationPrimitives.MakeAnim(0, 1, AnimationPrimitives._dur500,
                AnimationPrimitives._easeUiOut, null);
            animation.Completed += (_, _) => { if (version == _version) Finish(); };
            BeginAnimation(ProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
            UpdateAnimationFrame();
        }

        else { Finish(); _lastText = text; }
    }

    private sealed class NumberAdorner(NumberFlowText owner) : Adorner(owner)
    {
        internal int RenderCount { get; private set; }
        protected override void OnRender(DrawingContext drawingContext)
        {
            RenderCount++;
            owner.RenderReels(drawingContext);
        }
        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
    }

    private static double Lerp(double from, double to, double progress) => from + (to - from) * progress;
}
