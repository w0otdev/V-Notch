using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch;

public partial class SpotlightWindow
{
    private bool _aiTopEdgeVisible;
    private bool _aiBottomEdgeVisible;

    private void AiTranscript_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (AiTopEdge == null || AiBottomEdge == null) return;
        double fraction = Math.Min(1, 22 / Math.Max(1, AiTranscriptScroll.ActualHeight));
        AiTopEdgeBrush.Viewbox = new Rect(0, 0, 1, fraction);
        AiBottomEdgeBrush.Viewbox = new Rect(0, 1 - fraction, 1, fraction);
        SetAiEdge(AiTopEdge, AiTranscriptScroll.VerticalOffset > 1, ref _aiTopEdgeVisible);
        SetAiEdge(AiBottomEdge,
            AiTranscriptScroll.ScrollableHeight - AiTranscriptScroll.VerticalOffset > 1,
            ref _aiBottomEdgeVisible);
    }

    private static void SetAiEdge(Border edge, bool visible, ref bool previous)
    {
        if (visible == previous) return;
        previous = visible;
        double from = edge.Opacity;
        edge.BeginAnimation(OpacityProperty, null);
        edge.Opacity = visible ? 1 : 0;
        if (!AnimationConfig.ReduceMotion)
            edge.BeginAnimation(OpacityProperty, new DoubleAnimation(from, edge.Opacity,
                TimeSpan.FromMilliseconds(200))
            { FillBehavior = FillBehavior.Stop });
    }
}
