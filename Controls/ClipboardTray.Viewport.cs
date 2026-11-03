using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;
using VNotch.ViewModels;

namespace VNotch.Controls;

public sealed partial class ClipboardTray
{
    private sealed class CardViewportState(ClipboardCardViewModel card, FrameworkElement presentation)
    {
        internal ClipboardCardViewModel Card { get; } = card;
        internal FrameworkElement Presentation { get; } = presentation;
        internal TranslateTransform Translation { get; } = new();
        internal bool Visible { get; set; }
        internal double FromOpacity { get; set; }
        internal double FromY { get; set; }
        internal long MotionStarted { get; set; }
    }

    private readonly Dictionary<FrameworkElement, CardViewportState> _cardViewportStates = [];
    private readonly List<CardViewportState> _viewportMotions = [];
    private AnimationFrameClock _viewportFrameClock;
    private bool _viewportMotionHooked;

    private void RefreshCardViewport(FrameworkElement element)
        => RefreshCardViewportVisibility(element, IsCardInViewport(element));

    private void RefreshCardViewportVisibility(FrameworkElement element, bool visible)
    {
        if (element.DataContext is not ClipboardCardViewModel card || !_realizedCardElements.Contains(element)) return;
        bool inStaggerEntrance = _entranceStaggerVersion == _motionVersion && _entranceStaggerVersion > 0 && !_trayLeaving;
        if (inStaggerEntrance) visible = true;
        if (!_cardViewportStates.TryGetValue(element, out var state) || !ReferenceEquals(state.Card, card))
        {
            ForgetCardViewport(element);
            if (element is not Border { Child: FrameworkElement presentation }) return;
            state = new CardViewportState(card, presentation);
            _cardViewportStates.Add(element, state);
            presentation.Opacity = inStaggerEntrance ? 1 : 0;
            state.Translation.Y = AnimationConfig.ReduceMotion || inStaggerEntrance ? 0 : 6;
            presentation.RenderTransform = state.Translation;
        }

        if (state.Visible == visible) return;
        state.Visible = visible;
        if (!visible) CancelThumbnailLoad(element);

        var visual = state.Presentation;
        double opacity = visual.Opacity;
        double y = visual.RenderTransform.Value.OffsetY;
        visual.BeginAnimation(OpacityProperty, null);
        if (AnimationConfig.ReduceMotion || inStaggerEntrance)
        {
            CancelViewportMotion(state);
            visual.Opacity = visible ? 1 : 0;
            state.Translation.Y = 0;
            if (!visible) ReleaseCardPreview(card);
            return;
        }

        // One tray-level tick shares timing across every fade/translation.
        // Per-property clocks allocate timing interval arrays on every frame.
        state.FromOpacity = visual.Opacity = opacity;
        state.FromY = state.Translation.Y = y;
        state.MotionStarted = Stopwatch.GetTimestamp();
        if (!_viewportMotions.Contains(state)) _viewportMotions.Add(state);
        if (!_viewportMotionHooked)
        {
            _viewportMotionHooked = true;
            _viewportFrameClock.Reset(state.MotionStarted);
            CompositionTarget.Rendering += ViewportMotion_Frame;
        }
    }

    private void ViewportMotion_Frame(object? sender, EventArgs args)
    {
        if (_disposed || !IsLoaded || !IsVisible || _trayLeaving)
        {
            StopViewportMotions();
            return;
        }
        bool advance = args is RenderingEventArgs frame
            ? _viewportFrameClock.TryAdvance(frame.RenderingTime, AnimationConfig.TargetFps, out _)
            : _viewportFrameClock.TryAdvance(Stopwatch.GetTimestamp(), AnimationConfig.TargetFps, out _);
        if (!advance) return;
        long now = Stopwatch.GetTimestamp();
        for (int index = _viewportMotions.Count - 1; index >= 0; index--)
        {
            var state = _viewportMotions[index];
            double progress = AnimationConfig.ReduceMotion ? 1 : Math.Clamp(
                (now - state.MotionStarted) * 1000d / Stopwatch.Frequency / MotionStandard, 0, 1);
            if (progress >= 1)
            {
                _viewportMotions.RemoveAt(index);
                SettleViewportMotion(state);
                continue;
            }
            double eased = TrayMotion.EaseOut.Ease(progress);
            double targetY = state.Visible ? 0 : 6;
            state.Presentation.Opacity = state.FromOpacity + ((state.Visible ? 1 : 0) - state.FromOpacity) * eased;
            state.Translation.Y = state.FromY + (targetY - state.FromY) * eased;
        }
        if (_viewportMotions.Count == 0) UnhookViewportMotionFrames();
    }

    private static void SettleViewportMotion(CardViewportState state)
    {
        state.Presentation.Opacity = state.Visible ? 1 : 0;
        state.Translation.Y = state.Visible || AnimationConfig.ReduceMotion ? 0 : 6;
        if (!state.Visible) ReleaseCardPreview(state.Card);
    }

    private void CancelViewportMotion(CardViewportState state)
    {
        _viewportMotions.Remove(state);
        if (_viewportMotions.Count == 0) UnhookViewportMotionFrames();
    }

    private void StopViewportMotions()
    {
        UnhookViewportMotionFrames();
        for (int index = _viewportMotions.Count - 1; index >= 0; index--)
            SettleViewportMotion(_viewportMotions[index]);
        _viewportMotions.Clear();
    }

    private void UnhookViewportMotionFrames()
    {
        if (!_viewportMotionHooked) return;
        CompositionTarget.Rendering -= ViewportMotion_Frame;
        _viewportMotionHooked = false;
    }

    private static DoubleAnimation CreateViewportAnimation(double from, double to)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(MotionStandard))
        {
            EasingFunction = TrayMotion.EaseOut,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        return animation;
    }

    private void ForgetCardViewport(FrameworkElement element)
    {
        if (!_cardViewportStates.Remove(element, out var state)) return;
        CancelViewportMotion(state);
        ResetCardMotion(state.Presentation);
        ReleaseCardPreview(state.Card);
    }

    private static void ReleaseCardPreview(ClipboardCardViewModel card)
    {
        card.Image = null;
        card.SourceIcon = null;
        card.FileIcon = null;
        card.Hide();
        card.ReleaseMaskedPreviewSize();
    }

    private void ReleaseCardPreviews(bool personalOnly = false)
    {
        foreach (var pair in _cardViewportStates.ToArray())
            if (!personalOnly || pair.Value.Card.Entry.IsPersonal) ForgetCardViewport(pair.Key);
        foreach (var card in _cards)
            if (!personalOnly || card.Entry.IsPersonal) ReleaseCardPreview(card);
    }

    private void CardActionGlass_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            var clip = new RectangleGeometry(new Rect(e.NewSize), 6, 6);
            clip.Freeze();
            element.Clip = clip;
        }
    }

    private void CardPreview_TargetUpdated(object sender, DataTransferEventArgs e)
    {
        if (sender is not Image image || e.Property != Image.SourceProperty || ReferenceEquals(image.Tag, image.Source)) return;
        image.Tag = image.Source;
        image.BeginAnimation(OpacityProperty, null);
        image.Opacity = image.Source == null ? 0 : 1;
        if (image.Source != null && !AnimationConfig.ReduceMotion)
            image.BeginAnimation(OpacityProperty, CreateViewportAnimation(0, 1), HandoffBehavior.SnapshotAndReplace);
    }
}
