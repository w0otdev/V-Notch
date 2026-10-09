using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Services;

namespace VNotch;

public partial class TranslationWindow
{
    private const double CharacterFadeMilliseconds = 160;
    private static readonly DependencyProperty ResultRevealTimeProperty = DependencyProperty.Register(
        "ResultRevealTime", typeof(double), typeof(TranslationWindow),
        new PropertyMetadata(0d, (owner, e) => ((TranslationWindow)owner).UpdateCharacterFades((double)e.NewValue)));
    private DispatcherOperation? _resultRevealStart;
    private int _resultRevealVersion, _resultRevealCompleted;
    private List<SolidColorBrush>? _resultRevealBrushes;
    private double _resultRevealInterval;

    private void QueueResultReveal()
    {
        StopResultReveal();
        if (!IsVisible || _dismissing || ResultText.Text.Length == 0 ||
            AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation) return;

        int version = _resultRevealVersion;
        ResultText.Opacity = 0;
        _resultRevealStart = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (version != _resultRevealVersion) return;
            _resultRevealStart = null;
            if (!IsVisible || _dismissing || AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation)
            {
                StopResultReveal();
                return;
            }
            // Measure once after layout. The complete selectable text stays in place;
            // only the alpha of characters in the visible viewport changes per frame.
            var drawing = new DrawingGroup();
            var brushes = new List<SolidColorBrush>();
            int[] starts = StringInfo.ParseCombiningCharacters(ResultText.Text);
            double viewportHeight = ResultScroll.ViewportHeight;
            using (var context = drawing.Open())
            {
                context.DrawRectangle(Brushes.Transparent, null, new Rect(ResultText.RenderSize));
                for (int i = 0; i < starts.Length; i++)
                {
                    if (char.IsWhiteSpace(ResultText.Text, starts[i])) continue;
                    int end = i + 1 < starts.Length ? starts[i + 1] : ResultText.Text.Length;
                    var leading = ResultText.GetRectFromCharacterIndex(starts[i]);
                    var trailing = ResultText.GetRectFromCharacterIndex(end - 1, trailingEdge: true);
                    // Caret queries can finish a pending layout and trigger cancellation.
                    if (version != _resultRevealVersion) return;
                    if (leading.IsEmpty || trailing.IsEmpty) continue;
                    if (leading.Top >= viewportHeight) break;
                    var brush = new SolidColorBrush(Colors.Black) { Opacity = 0 };
                    brushes.Add(brush);
                    double left = Math.Min(leading.Left, trailing.Left);
                    double right = Math.Max(leading.Left, trailing.Left);
                    if (right > left)
                        context.DrawRectangle(brush, null, new Rect(left, leading.Top, right - left, leading.Height));
                }
            }
            if (brushes.Count == 0) { StopResultReveal(); return; }
            _resultRevealBrushes = brushes;
            _resultRevealCompleted = 0;
            _resultRevealInterval = Math.Min(24, 3200d / brushes.Count);
            ResultText.OpacityMask = new DrawingBrush(drawing)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(ResultText.RenderSize),
                Stretch = Stretch.Fill
            };
            ResultText.Opacity = 1;
            double duration = (brushes.Count - 1) * _resultRevealInterval + CharacterFadeMilliseconds;
            var reveal = new DoubleAnimation(0, duration, TimeSpan.FromMilliseconds(duration)) { FillBehavior = FillBehavior.Stop };
            Timeline.SetDesiredFrameRate(reveal, Math.Min(60, AnimationConfig.TargetFps));
            reveal.Completed += (_, _) => { if (version == _resultRevealVersion) StopResultReveal(); };
            BeginAnimation(ResultRevealTimeProperty, reveal);
        }));
    }

    private void UpdateCharacterFades(double elapsed)
    {
        var brushes = _resultRevealBrushes;
        if (brushes == null) return;
        int activeEnd = Math.Min(brushes.Count, (int)(elapsed / _resultRevealInterval) + 1);
        for (int i = _resultRevealCompleted; i < activeEnd; i++)
        {
            double progress = Math.Clamp((elapsed - i * _resultRevealInterval) / CharacterFadeMilliseconds, 0, 1);
            brushes[i].Opacity = 1 - (1 - progress) * (1 - progress);
            if (progress == 1) _resultRevealCompleted = i + 1;
        }
    }

    private void StopResultReveal(bool revealImmediately = true)
    {
        ++_resultRevealVersion;
        _resultRevealStart?.Abort();
        _resultRevealStart = null;
        _resultRevealBrushes = null;
        BeginAnimation(ResultRevealTimeProperty, null);
        SetValue(ResultRevealTimeProperty, 0d);
        double opacity = revealImmediately ? 1 : ResultText.Opacity;
        ResultText.BeginAnimation(OpacityProperty, null);
        ResultText.Opacity = opacity;
        ResultText.OpacityMask = null;
        ResultText.Clip = null;
    }

    private void ResultRevealMotionChanged()
    {
        if (AnimationConfig.ReduceMotion) StopResultReveal();
    }
}
