using System.Globalization;
using System.Diagnostics;
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
    private int _resultRevealVersion;
    private Dictionary<int, CharacterReveal>? _resultRevealCharacters;
    private readonly Stopwatch _resultRevealClock = new();
    private bool _buildingReveal;
    // Instance-scoped so deterministic tests do not alter other windows' clocks.
    internal Func<DoubleAnimation, AnimationClock>? ResultRevealClockFactory { get; set; }
    private sealed record CharacterReveal(SolidColorBrush Brush, double Start);

    private void QueueResultReveal(int visiblePrefixLength = 0, bool preserveProgress = false)
    {
        if (!IsVisible || _dismissing || ResultText.Text.Length == 0 ||
            AnimationConfig.ReduceMotion || !AnimationConfig.ClientAreaAnimation)
        {
            StopResultReveal();
            return;
        }

        FrameworkElement target = MarkdownResult.Visibility == Visibility.Visible ? MarkdownResult : ResultText;
        bool append = preserveProgress || (visiblePrefixLength > 0 && target == ResultText);
        if (!append)
        {
            StopResultReveal();
            target.Opacity = 0;
            _resultRevealClock.Restart();
        }
        else if (!_resultRevealClock.IsRunning) _resultRevealClock.Start();
        // Coalesce token bursts and let normal layout finish before querying glyphs.
        // Keep the old brushes/clock alive: an append must not finish or restart a fade.
        _resultRevealStart?.Abort();
        int version = ++_resultRevealVersion;
        _resultRevealStart = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(StartReveal));

        void StartReveal()
        {
            if (version != _resultRevealVersion) return;
            _resultRevealStart = null;
            if (!IsVisible || _dismissing || AnimationConfig.ReduceMotion || !AnimationConfig.ClientAreaAnimation)
            {
                StopResultReveal();
                return;
            }
            // A cold result can arrive before the text viewport has been
            // arranged. Query caret rectangles only once layout is usable.
            if (!append || !target.IsMeasureValid || !target.IsArrangeValid ||
                !ResultScroll.IsArrangeValid || ResultScroll.ViewportHeight <= 0)
            {
                _buildingReveal = true;
                try { target.UpdateLayout(); }
                finally { _buildingReveal = false; }
                if (version != _resultRevealVersion) return;
            }
            // Measure once after layout. The complete selectable text stays in place;
            // only the alpha of characters in the visible viewport changes per frame.
            var drawing = new DrawingGroup();
            var characters = new Dictionary<int, CharacterReveal>();
            var previous = append ? _resultRevealCharacters : null;
            double elapsed = _resultRevealClock.Elapsed.TotalMilliseconds;
            int added = 0;
            double viewportHeight = ResultScroll.ViewportHeight;
            int first = 0;
            double opaqueHeight = 0;
            if (target == ResultText && append && target.Opacity > 0 && visiblePrefixLength > 0)
            {
                int activeOffset = visiblePrefixLength;
                if (previous != null)
                    foreach (var (offset, character) in previous)
                        if (character.Brush.Opacity < 1) activeOffset = Math.Min(activeOffset, offset);
                // Wrapping and bidi can reposition the current line. Rebuild it and
                // everything below it, painting completed lines as one opaque band.
                int line = ResultText.GetLineIndexFromCharacterIndex(Math.Max(0, Math.Min(activeOffset, ResultText.Text.Length) - 1));
                if (line > 0)
                {
                    first = Math.Max(0, ResultText.GetCharacterIndexFromLineIndex(line));
                    var bounds = ResultText.GetRectFromCharacterIndex(first);
                    if (bounds.IsEmpty) first = 0;
                    else opaqueHeight = Math.Clamp(bounds.Top, 0, target.ActualHeight);
                }
            }
            (int Offset, Rect Leading, Rect Trailing)[] rects;
            _buildingReveal = true;
            try
            {
                rects = ResultCharacterRects(first)
                    .TakeWhile(character => character.Leading.IsEmpty || character.Leading.Top < viewportHeight).ToArray();
            }
            finally { _buildingReveal = false; }
            if (version != _resultRevealVersion) return;
            int freshCount = rects.Count(character => (previous == null || !previous.ContainsKey(character.Offset)) &&
                (!append || character.Offset >= visiblePrefixLength || target.Opacity == 0));
            double interval = Math.Min(14, 200d / Math.Max(1, freshCount));
            using (var context = drawing.Open())
            {
                context.DrawRectangle(Brushes.Transparent, null, new Rect(target.RenderSize));
                if (opaqueHeight > 0)
                    context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, target.ActualWidth, opaqueHeight));
                foreach (var (offset, leading, trailing) in rects)
                {
                    // Caret queries can finish a pending layout and trigger cancellation.
                    if (version != _resultRevealVersion) return;
                    if (leading.IsEmpty || trailing.IsEmpty) continue;
                    if (leading.Top >= viewportHeight) break;
                    SolidColorBrush brush = Brushes.Black;
                    if (previous != null && previous.TryGetValue(offset, out var existing))
                    {
                        characters[offset] = existing;
                        brush = existing.Brush;
                    }
                    else if (!append || offset >= visiblePrefixLength || target.Opacity == 0)
                    {
                        brush = new SolidColorBrush(Colors.Black) { Opacity = 0 };
                        characters[offset] = new CharacterReveal(brush, elapsed + added++ * interval);
                    }
                    double left = Math.Min(leading.Left, trailing.Left);
                    double right = Math.Max(leading.Left, trailing.Left);
                    if (right > left)
                        context.DrawRectangle(brush, null, new Rect(left, leading.Top, right - left, leading.Height));
                }
            }
            if (characters.Count == 0) { StopResultReveal(); return; }
            _resultRevealCharacters = characters;
            target.OpacityMask = new DrawingBrush(drawing)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(target.RenderSize),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(target.RenderSize),
                Stretch = Stretch.None
            };
            target.Opacity = 1;
            double end = characters.Values.Max(character => character.Start) + CharacterFadeMilliseconds;
            if (end <= elapsed) { StopResultReveal(); return; }
            var reveal = new DoubleAnimation(elapsed, end, TimeSpan.FromMilliseconds(end - elapsed)) { FillBehavior = FillBehavior.HoldEnd };
            Timeline.SetDesiredFrameRate(reveal, Math.Min(60, AnimationConfig.TargetFps));
            reveal.Completed += (_, _) => { if (version == _resultRevealVersion) StopResultReveal(); };
            if (ResultRevealClockFactory is { } createClock)
                ApplyAnimationClock(ResultRevealTimeProperty, createClock(reveal));
            else BeginAnimation(ResultRevealTimeProperty, reveal);
        }
    }

    private IEnumerable<(int Offset, Rect Leading, Rect Trailing)> ResultCharacterRects(int first)
    {
        if (MarkdownResult.Visibility == Visibility.Visible)
        {
            int runOffset = 0;
            var cursor = MarkdownResult.Document.ContentStart;
            while (cursor != null && cursor.CompareTo(MarkdownResult.Document.ContentEnd) < 0)
            {
                if (cursor.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                {
                    string text = cursor.GetTextInRun(LogicalDirection.Forward);
                    for (int i = 0, end; i < text.Length; i = end)
                    {
                        end = i + StringInfo.GetNextTextElementLength(text.AsSpan(i));
                        if (char.IsWhiteSpace(text, i)) continue;
                        yield return (runOffset + i, cursor.GetPositionAtOffset(i)!.GetCharacterRect(LogicalDirection.Forward),
                            cursor.GetPositionAtOffset(end)!.GetCharacterRect(LogicalDirection.Backward));
                    }
                    runOffset += text.Length;
                }
                cursor = cursor.GetNextContextPosition(LogicalDirection.Forward);
            }
            yield break;
        }
        string plain = ResultText.Text;
        for (int i = first, end; i < plain.Length; i = end)
        {
            end = i + StringInfo.GetNextTextElementLength(plain.AsSpan(i));
            if (char.IsWhiteSpace(plain, i)) continue;
            yield return (i, ResultText.GetRectFromCharacterIndex(i),
                ResultText.GetRectFromCharacterIndex(end - 1, trailingEdge: true));
        }
    }

    private void UpdateCharacterFades(double elapsed)
    {
        if (_resultRevealCharacters == null) return;
        foreach (var character in _resultRevealCharacters.Values)
        {
            double progress = Math.Clamp((elapsed - character.Start) / CharacterFadeMilliseconds, 0, 1);
            // Never dim an existing glyph when a new animation clock is attached.
            if (progress > character.Brush.Opacity) character.Brush.Opacity = progress;
        }
    }

    private void ResultRevealLayoutChanged()
    {
        if (!_buildingReveal && _resultRevealCharacters != null && _resultRevealStart == null)
            QueueResultReveal(ResultText.Text.Length, preserveProgress: true);
    }

    private void StopResultReveal(bool revealImmediately = true)
    {
        ++_resultRevealVersion;
        _resultRevealStart?.Abort();
        _resultRevealStart = null;
        _resultRevealCharacters = null;
        _resultRevealClock.Reset();
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
