using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Presenters;

internal sealed class KaraokeLyricsPresenter
{
    private sealed class WordVisual(Run run, Color sung, Color pending)
    {
        internal Run Run { get; } = run;
        internal GradientStop Head { get; } = new(sung, -0.28);
        internal GradientStop Tail { get; } = new(pending, 0);
        internal LinearGradientBrush Brush { get; } = new();

        internal void Stop()
        {
            // Preserve the rendered frame when this line becomes the outgoing layer.
            double head = Head.Offset, tail = Tail.Offset;
            Head.BeginAnimation(GradientStop.OffsetProperty, null);
            Tail.BeginAnimation(GradientStop.OffsetProperty, null);
            Head.Offset = head;
            Tail.Offset = tail;
        }
    }

    private LyricWord[]? _words;
    private WordVisual[]? _visuals;
    private Brush _sung = Brushes.White, _pending = Brushes.Gray;
    private TimeSpan _lastPosition;
    private TimeSpan _scheduledPosition;
    private long _scheduledTick;
    private bool _playing, _scheduled, _reduceMotion;

    internal void SetLine(TextBlock block, LyricLine line)
    {
        Clear();
        block.Text = line.Text;
        if (line.Words is not { Length: > 0 } words) return;
        _words = words;
        _visuals = new WordVisual[words.Length];
        Color sung = (block.Foreground as SolidColorBrush)?.Color ?? Colors.White;
        sung.A = 255;
        Color pending = sung;
        pending.A = 100;
        _sung = new SolidColorBrush(sung);
        _sung.Freeze();
        _pending = new SolidColorBrush(pending);
        _pending.Freeze();
        block.Inlines.Clear();
        int offset = 0;
        for (int i = 0; i < words.Length; i++)
        {
            var word = words[i];
            if (word.Offset > offset) block.Inlines.Add(new Run(line.Text[offset..word.Offset]) { Foreground = _pending });
            var run = new Run(line.Text.Substring(word.Offset, word.Length)) { Foreground = _pending };
            var visual = new WordVisual(run, sung, pending);
            visual.Brush.StartPoint = new Point(0, 0);
            visual.Brush.EndPoint = new Point(1, 0);
            visual.Brush.GradientStops.Add(visual.Head);
            visual.Brush.GradientStops.Add(visual.Tail);
            _visuals[i] = visual;
            block.Inlines.Add(run);
            offset = word.Offset + word.Length;
        }
        if (offset < line.Text.Length) block.Inlines.Add(new Run(line.Text[offset..]) { Foreground = _pending });
    }

    internal void Update(TimeSpan position, bool playing)
    {
        if (_words == null || _visuals == null) return;
        long tick = System.Diagnostics.Stopwatch.GetTimestamp();
        bool reduceMotion = AnimationConfig.ReduceMotion;
        double elapsed = _scheduledTick == 0 ? 0 : (tick - _scheduledTick) / (double)System.Diagnostics.Stopwatch.Frequency;
        bool resync = !_scheduled || playing != _playing || reduceMotion != _reduceMotion ||
            ((!playing || reduceMotion) && position != _lastPosition) ||
            Math.Abs((position - _scheduledPosition).TotalSeconds - (_playing ? elapsed : 0)) > 0.1;
        _lastPosition = position;
        _playing = playing;
        _reduceMotion = reduceMotion;
        if (!resync) return;
        _scheduled = true;
        _scheduledTick = tick;
        _scheduledPosition = position;
        for (int i = 0; i < _visuals.Length; i++)
        {
            var visual = _visuals[i];
            var word = _words[i];
            visual.Stop();
            if (position >= word.End && position >= word.Start) { visual.Run.Foreground = _sung; continue; }
            if (position < word.Start && (!playing || reduceMotion)) { visual.Run.Foreground = _pending; continue; }
            double duration = (word.End - word.Start).TotalSeconds;
            double progress = duration <= 0 ? 0 : Math.Clamp((position - word.Start).TotalSeconds / duration, 0, 1);
            visual.Run.Foreground = visual.Brush;
            visual.Head.Offset = -0.28 + 1.28 * progress;
            visual.Tail.Offset = 1.28 * progress;
            if (!playing || reduceMotion) continue;
            TimeSpan delay = word.Start > position ? word.Start - position : TimeSpan.Zero;
            TimeSpan remaining = word.End > position ? word.End - (word.Start > position ? word.Start : position) : TimeSpan.Zero;
            // Schedule the whole line once; short syllables must not wait for the 100 ms UI timer.
            Animate(visual.Head, 1, remaining, delay);
            Animate(visual.Tail, 1.28, remaining, delay);
        }
    }

    private static void Animate(GradientStop stop, double end, TimeSpan remaining, TimeSpan delay)
    {
        var animation = new DoubleAnimation(stop.Offset, end, remaining) { BeginTime = delay };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        stop.BeginAnimation(GradientStop.OffsetProperty, animation);
    }

    internal void Clear()
    {
        Suspend();
        _words = null;
        _visuals = null;
        _scheduledTick = 0;
    }

    internal void Suspend()
    {
        if (_scheduled && _visuals != null)
            foreach (var visual in _visuals) visual.Stop();
        _scheduled = false;
    }
}
