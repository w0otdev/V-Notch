using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Services;

namespace VNotch;

public partial class TranslationWindow
{
    private const double CharacterFadeMilliseconds = 100;
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
        FrameworkElement target = MarkdownResult.Visibility == Visibility.Visible ? MarkdownResult : ResultText;
        target.Opacity = 0;
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
            double viewportHeight = ResultScroll.ViewportHeight;
            using (var context = drawing.Open())
            {
                context.DrawRectangle(Brushes.Transparent, null, new Rect(target.RenderSize));
                foreach (var (leading, trailing) in ResultCharacterRects())
                {
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
            _resultRevealInterval = Math.Min(14, 1200d / brushes.Count);
            target.OpacityMask = new DrawingBrush(drawing)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(target.RenderSize),
                Stretch = Stretch.Fill
            };
            target.Opacity = 1;
            double duration = (brushes.Count - 1) * _resultRevealInterval + CharacterFadeMilliseconds;
            var reveal = new DoubleAnimation(0, duration, TimeSpan.FromMilliseconds(duration)) { FillBehavior = FillBehavior.Stop };
            Timeline.SetDesiredFrameRate(reveal, Math.Min(60, AnimationConfig.TargetFps));
            reveal.Completed += (_, _) => { if (version == _resultRevealVersion) StopResultReveal(); };
            BeginAnimation(ResultRevealTimeProperty, reveal);
        }));
    }

    private IEnumerable<(Rect Leading, Rect Trailing)> ResultCharacterRects()
    {
        if (MarkdownResult.Visibility == Visibility.Visible)
        {
            var cursor = MarkdownResult.Document.ContentStart;
            while (cursor != null && cursor.CompareTo(MarkdownResult.Document.ContentEnd) < 0)
            {
                if (cursor.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                {
                    string text = cursor.GetTextInRun(LogicalDirection.Forward);
                    int[] starts = StringInfo.ParseCombiningCharacters(text);
                    for (int i = 0; i < starts.Length; i++)
                    {
                        if (char.IsWhiteSpace(text, starts[i])) continue;
                        int end = i + 1 < starts.Length ? starts[i + 1] : text.Length;
                        yield return (cursor.GetPositionAtOffset(starts[i])!.GetCharacterRect(LogicalDirection.Forward),
                            cursor.GetPositionAtOffset(end)!.GetCharacterRect(LogicalDirection.Backward));
                    }
                }
                cursor = cursor.GetNextContextPosition(LogicalDirection.Forward);
            }
            yield break;
        }
        int[] plainStarts = StringInfo.ParseCombiningCharacters(ResultText.Text);
        for (int i = 0; i < plainStarts.Length; i++)
        {
            if (char.IsWhiteSpace(ResultText.Text, plainStarts[i])) continue;
            int end = i + 1 < plainStarts.Length ? plainStarts[i + 1] : ResultText.Text.Length;
            yield return (ResultText.GetRectFromCharacterIndex(plainStarts[i]),
                ResultText.GetRectFromCharacterIndex(end - 1, trailingEdge: true));
        }
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
            if (Math.Abs(progress - 1) < 1e-6) _resultRevealCompleted = i + 1;
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
        foreach (FrameworkElement target in new FrameworkElement[] { ResultText, MarkdownResult })
        {
            double opacity = revealImmediately ? 1 : target.Opacity;
            target.BeginAnimation(OpacityProperty, null);
            target.Opacity = opacity;
            target.OpacityMask = null;
            target.Clip = null;
        }
    }

    private void ResultRevealMotionChanged()
    {
        if (AnimationConfig.ReduceMotion) StopResultReveal();
    }
}
