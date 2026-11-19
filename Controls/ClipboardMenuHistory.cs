using System.Windows;
using System.Windows.Controls;

namespace VNotch.Controls;

internal sealed class ClipboardMenuHistory
{
    internal ContextMenu? OpenMenu { get; private set; }
    internal FrameworkElement? OpenTarget { get; private set; }
    internal object? OpenCard { get; private set; }
    internal ContextMenu? ClosedMenu { get; private set; }
    internal FrameworkElement? ClosedTarget { get; private set; }
    internal object? ClosedCard { get; private set; }
    private long _closedAt;

    internal void Opened(ContextMenu menu, FrameworkElement? target, object? card)
    {
        OpenMenu = menu;
        OpenTarget = target;
        OpenCard = card;
    }

    internal void Closed(ContextMenu menu, FrameworkElement? target, object? card)
    {
        ClosedMenu = menu;
        ClosedTarget = target;
        ClosedCard = card;
        _closedAt = Environment.TickCount64;
    }

    internal bool ConsumeRecentClose(ContextMenu menu, FrameworkElement card, FrameworkElement button)
    {
        if (Environment.TickCount64 - _closedAt >= 450 ||
            !(ReferenceEquals(ClosedMenu, menu) || ReferenceEquals(ClosedCard, card.DataContext) ||
              ReferenceEquals(ClosedTarget, card) || ReferenceEquals(ClosedTarget, button))) return false;
        ClearClosed();
        return true;
    }

    internal void Forget(object card)
    {
        if (ReferenceEquals(OpenCard, card))
        {
            OpenMenu = null;
            OpenTarget = null;
            OpenCard = null;
        }
        if (ReferenceEquals(ClosedCard, card)) ClearClosed();
    }

    private void ClearClosed()
    {
        ClosedMenu = null;
        ClosedTarget = null;
        ClosedCard = null;
        _closedAt = 0;
    }
}
