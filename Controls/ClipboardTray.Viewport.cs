using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;
using VNotch.ViewModels;

namespace VNotch.Controls;

public partial class ClipboardTray
{
    private sealed class CardViewportState(ClipboardCardViewModel card, FrameworkElement presentation)
    {
        internal ClipboardCardViewModel Card { get; } = card;
        internal FrameworkElement Presentation { get; } = presentation;
        internal bool Visible { get; set; }
    }

    private readonly Dictionary<FrameworkElement, CardViewportState> _cardViewportStates = [];

    private void RefreshCardViewport(FrameworkElement element)
        => RefreshCardViewportVisibility(element, IsCardInViewport(element));

    private void RefreshCardViewportVisibility(FrameworkElement element, bool visible)
    {
        if (element.DataContext is not ClipboardCardViewModel card || !_realizedCardElements.Contains(element)) return;
        if (!_cardViewportStates.TryGetValue(element, out var state) || !ReferenceEquals(state.Card, card))
        {
            ForgetCardViewport(element);
            if (element is not Border { Child: FrameworkElement presentation }) return;
            state = new CardViewportState(card, presentation);
            _cardViewportStates.Add(element, state);
            presentation.Opacity = 0;
            presentation.RenderTransform = new TranslateTransform(0, AnimationConfig.ReduceMotion ? 0 : 6);
        }

        if (state.Visible == visible) return;
        state.Visible = visible;
        if (!visible) CancelThumbnailLoad(element);

        var visual = state.Presentation;
        double opacity = visual.Opacity;
        double y = visual.RenderTransform.Value.OffsetY;
        visual.BeginAnimation(OpacityProperty, null);
        visual.Opacity = visible ? 1 : 0;
        var translate = new TranslateTransform(0, visible || AnimationConfig.ReduceMotion ? 0 : 6);
        visual.RenderTransform = translate;
        if (AnimationConfig.ReduceMotion)
        {
            if (!visible) ReleaseCardPreview(card);
            return;
        }

        // The same duration/curve in both directions; interrupt from the
        // rendered values when the user quickly scrolls back to a card.
        var fade = CreateViewportAnimation(opacity, visual.Opacity);
        if (!visible) fade.Completed += (_, _) =>
        {
            if (_cardViewportStates.TryGetValue(element, out var current) && ReferenceEquals(current, state) && !state.Visible)
                ReleaseCardPreview(card);
        };
        visual.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        translate.BeginAnimation(TranslateTransform.YProperty, CreateViewportAnimation(y, translate.Y), HandoffBehavior.SnapshotAndReplace);
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
        ResetCardMotion(state.Presentation);
        ReleaseCardPreview(state.Card);
    }

    private static void ReleaseCardPreview(ClipboardCardViewModel card)
    {
        card.Image = null;
        card.SourceIcon = null;
        card.FileIcon = null;
    }

    private void ReleaseCardPreviews(bool personalOnly = false)
    {
        foreach (var pair in _cardViewportStates.ToArray())
            if (!personalOnly || pair.Value.Card.Entry.IsPersonal) ForgetCardViewport(pair.Key);
        foreach (var card in _cards)
            if (!personalOnly || card.Entry.IsPersonal) ReleaseCardPreview(card);
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
