using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch.Controls;

internal sealed class MarqueeController
{
    #region Dependencies

    private readonly MarqueeTargets _title;
    private readonly MarqueeTargets _artist;
    private readonly TextBlock _compactTitleText;
    private readonly TranslateTransform _compactTitleTranslate;
    private readonly Func<double, double> _getVisibleMediaTextWidth;
    private readonly DispatcherTimer _titleMorphTimer;
    private readonly DispatcherTimer _artistMorphTimer;

    #endregion

    #region State

    private static readonly TimeSpan MorphThrottleInterval = TimeSpan.FromMilliseconds(400);
    private double _titleScrollDistance;
    private double _artistScrollDistance;
    private string _lastTitleText;
    private string _lastArtistText;
    private string? _pendingTitleText;
    private string? _pendingArtistText;
    private bool _isTitleActiveA = true;
    private bool _isArtistActiveA = true;
    private DateTime _lastTitleMorphTimeUtc = DateTime.MinValue;
    private DateTime _lastArtistMorphTimeUtc = DateTime.MinValue;

    #endregion

    public MarqueeController(
        UIElement titleLayerA, TextBlock titleA, TranslateTransform titleMarqueeA, TranslateTransform titleMorphA,
        UIElement titleLayerB, TextBlock titleB, TranslateTransform titleMarqueeB, TranslateTransform titleMorphB,
        UIElement artistLayerA, TextBlock artistA, TranslateTransform artistMarqueeA, TranslateTransform artistMorphA,
        UIElement artistLayerB, TextBlock artistB, TranslateTransform artistMarqueeB, TranslateTransform artistMorphB,
        TextBlock compactTitleText, TranslateTransform compactTitleTranslate,
        Func<double, double> getVisibleMediaTextWidth)
    {
        _title = new MarqueeTargets(titleLayerA, titleA, titleMarqueeA, titleMorphA,
            titleLayerB, titleB, titleMarqueeB, titleMorphB);
        _artist = new MarqueeTargets(artistLayerA, artistA, artistMarqueeA, artistMorphA,
            artistLayerB, artistB, artistMarqueeB, artistMorphB);
        _compactTitleText = compactTitleText;
        _compactTitleTranslate = compactTitleTranslate;
        _getVisibleMediaTextWidth = getVisibleMediaTextWidth;
        _lastTitleText = titleA.Text ?? string.Empty;
        _lastArtistText = artistA.Text ?? string.Empty;

        _titleMorphTimer = new DispatcherTimer(DispatcherPriority.Normal, titleA.Dispatcher);
        _titleMorphTimer.Tick += TitleMorphTimer_Tick;
        _artistMorphTimer = new DispatcherTimer(DispatcherPriority.Normal, artistA.Dispatcher);
        _artistMorphTimer.Tick += ArtistMorphTimer_Tick;
    }

    #region Public API
    public void RefreshMediaMarquee()
    {
        RestartMarqueeFor(_title, _isTitleActiveA, isTitle: true);
        RestartMarqueeFor(_artist, _isArtistActiveA, isTitle: false);
    }

    public void TransitionTrackText(string title, string artist)
    {
        title ??= string.Empty;
        artist ??= string.Empty;

        _pendingTitleText = null;
        _pendingArtistText = null;
        _titleMorphTimer.Stop();
        _artistMorphTimer.Stop();

        // A track change is one visual event. Bypass the independent metadata
        // throttles so both rows swap layers on the same dispatcher frame.
        ApplyTitleText(title);
        ApplyArtistText(artist);
    }

    public void UpdateTitleText(string newText)
    {
        newText ??= string.Empty;
        if (newText == _lastTitleText)
        {
            _pendingTitleText = null;
            _titleMorphTimer.Stop();
            return;
        }

        var remaining = GetRemainingThrottle(_lastTitleMorphTimeUtc);
        if (remaining > TimeSpan.Zero)
        {
            _pendingTitleText = newText;
            RestartTimer(_titleMorphTimer, remaining);
            return;
        }

        ApplyTitleText(newText);
    }
    public void UpdateArtistText(string newText)
    {
        newText ??= string.Empty;
        if (newText == _lastArtistText)
        {
            _pendingArtistText = null;
            _artistMorphTimer.Stop();
            return;
        }

        var remaining = GetRemainingThrottle(_lastArtistMorphTimeUtc);
        if (remaining > TimeSpan.Zero)
        {
            _pendingArtistText = newText;
            RestartTimer(_artistMorphTimer, remaining);
            return;
        }

        ApplyArtistText(newText);
    }
    public static void StartMarqueeAnimation(TranslateTransform transform, double distance, double durationPerPixel = 40)
    {
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;

        if (distance <= 1) return;

        const double pauseMs = 900;
        const double minTravelMs = 2200;
        const double maxTravelMs = 14000;
        var travelMs = Math.Clamp(distance * durationPerPixel, minTravelMs, maxTravelMs);

        var t0 = TimeSpan.Zero;
        var t1 = t0 + TimeSpan.FromMilliseconds(pauseMs);
        var t2 = t1 + TimeSpan.FromMilliseconds(travelMs);
        var t3 = t2 + TimeSpan.FromMilliseconds(pauseMs);
        var t4 = t3 + TimeSpan.FromMilliseconds(travelMs);
        var t5 = t4 + TimeSpan.FromMilliseconds(pauseMs);

        var keyAnim = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(keyAnim, VNotch.Services.AnimationConfig.TargetFps);

        keyAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, t0));
        keyAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, t1));
        keyAnim.KeyFrames.Add(new LinearDoubleKeyFrame(-distance, t2));
        keyAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(-distance, t3));
        keyAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0, t4));
        keyAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, t5));

        transform.BeginAnimation(TranslateTransform.XProperty, keyAnim, HandoffBehavior.SnapshotAndReplace);
    }

    #endregion

    #region Implementation

    private static TimeSpan GetRemainingThrottle(DateTime lastMorphTimeUtc)
    {
        if (lastMorphTimeUtc == DateTime.MinValue)
        {
            return TimeSpan.Zero;
        }

        var remaining = MorphThrottleInterval - (DateTime.UtcNow - lastMorphTimeUtc);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static void RestartTimer(DispatcherTimer timer, TimeSpan interval)
    {
        timer.Stop();
        timer.Interval = interval > TimeSpan.Zero ? interval : TimeSpan.FromMilliseconds(1);
        timer.Start();
    }

    private void TitleMorphTimer_Tick(object? sender, EventArgs e)
    {
        _titleMorphTimer.Stop();
        var pending = _pendingTitleText;
        _pendingTitleText = null;

        if (pending != null && pending != _lastTitleText)
        {
            ApplyTitleText(pending);
        }
    }

    private void ArtistMorphTimer_Tick(object? sender, EventArgs e)
    {
        _artistMorphTimer.Stop();
        var pending = _pendingArtistText;
        _pendingArtistText = null;

        if (pending != null && pending != _lastArtistText)
        {
            ApplyArtistText(pending);
        }
    }

    private void ApplyTitleText(string newText)
    {
        _pendingTitleText = null;
        _titleMorphTimer.Stop();
        _lastTitleText = newText;
        _lastTitleMorphTimeUtc = DateTime.UtcNow;

        MorphAndRestart(_title, ref _isTitleActiveA, newText,
            setDistance: d => _titleScrollDistance = d);
    }

    private void ApplyArtistText(string newText)
    {
        _pendingArtistText = null;
        _artistMorphTimer.Stop();
        _lastArtistText = newText;
        _lastArtistMorphTimeUtc = DateTime.UtcNow;

        MorphAndRestart(_artist, ref _isArtistActiveA, newText,
            setDistance: d => _artistScrollDistance = d);
    }

    private void RestartMarqueeFor(MarqueeTargets t, bool activeA, bool isTitle)
    {
        var activeText = activeA ? t.TextA : t.TextB;
        var activeTranslate = activeA ? t.MarqueeA : t.MarqueeB;

        activeText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = activeText.DesiredSize.Width;
        double containerWidth = _getVisibleMediaTextWidth(340);
        double distance = textWidth - containerWidth + 15;

        if (distance > 1)
        {
            if (isTitle) _titleScrollDistance = distance;
            else _artistScrollDistance = distance;
            StartMarqueeAnimation(activeTranslate, distance);
        }
        else
        {
            if (isTitle) _titleScrollDistance = 0;
            else _artistScrollDistance = 0;
            activeTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            activeTranslate.X = 0;
        }
    }

    private void MorphAndRestart(MarqueeTargets t, ref bool activeA, string newText, Action<double> setDistance)
    {
        if (activeA)
        {
            AnimateTextMorph(t.LayerA, t.TextA, t.LayerB, t.TextB, t.MorphA, t.MorphB, newText);
            activeA = false;
        }
        else
        {
            AnimateTextMorph(t.LayerB, t.TextB, t.LayerA, t.TextA, t.MorphB, t.MorphA, newText);
            activeA = true;
        }

        t.MarqueeA.X = 0;
        t.MarqueeB.X = 0;

        var newActiveText = activeA ? t.TextA : t.TextB;
        var newActiveTranslate = activeA ? t.MarqueeA : t.MarqueeB;

        newActiveText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = newActiveText.DesiredSize.Width;
        double containerWidth = _getVisibleMediaTextWidth(340);

        if (textWidth > containerWidth)
        {
            double distance = textWidth - containerWidth + 15;
            setDistance(distance);
            StartMarqueeAnimation(newActiveTranslate, distance);
        }
        else
        {
            setDistance(0);
            newActiveTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            newActiveTranslate.X = 0;
        }
    }

    private static void AnimateTextMorph(
        UIElement currentLayer,
        TextBlock current,
        UIElement nextLayer,
        TextBlock next,
        TranslateTransform currentMorph,
        TranslateTransform nextMorph,
        string newText)
    {
        double currentOpacity = Math.Clamp(currentLayer.Opacity, 0, 1);
        currentLayer.BeginAnimation(UIElement.OpacityProperty, null);
        nextLayer.BeginAnimation(UIElement.OpacityProperty, null);
        currentLayer.Opacity = currentOpacity;
        nextLayer.Opacity = 0;
        next.SetCurrentValue(TextBlock.TextProperty, newText);

        int animFps = VNotch.Services.AnimationConfig.TargetFps;
        var easeOut = _easeExpOut6;

        currentMorph.BeginAnimation(TranslateTransform.XProperty, null);
        nextMorph.BeginAnimation(TranslateTransform.XProperty, null);
        currentMorph.BeginAnimation(TranslateTransform.YProperty, null);
        nextMorph.BeginAnimation(TranslateTransform.YProperty, null);
        currentMorph.X = 0;
        nextMorph.X = 20;
        currentMorph.Y = 0;
        nextMorph.Y = 0;

        var slideOut = MakeAnim(0, -18, _dur400, _easeQuadIn, animFps);
        currentMorph.BeginAnimation(TranslateTransform.XProperty, slideOut);

        var slideIn = MakeAnim(20, 0, _dur600, easeOut, animFps);
        nextMorph.BeginAnimation(TranslateTransform.XProperty, slideIn);

        var fadeOut = MakeAnim(currentOpacity, 0, _dur350, _easeQuadIn, animFps);
        currentLayer.BeginAnimation(UIElement.OpacityProperty, fadeOut);

        var fadeIn = MakeAnim(0, 1, _dur500, easeOut, TimeSpan.FromMilliseconds(70));
        Timeline.SetDesiredFrameRate(fadeIn, animFps);
        nextLayer.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    #endregion
    private sealed record MarqueeTargets(
            UIElement LayerA, TextBlock TextA, TranslateTransform MarqueeA, TranslateTransform MorphA,
            UIElement LayerB, TextBlock TextB, TranslateTransform MarqueeB, TranslateTransform MorphB);
}
