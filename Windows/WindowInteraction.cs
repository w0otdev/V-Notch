using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace VNotch;

internal static class WindowInteraction
{
    internal static DependencyObject? Parent(DependencyObject child) => child is Visual or Visual3D
        ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);

    internal static void TryDragMove(this Window window)
    {
        try { window.DragMove(); }
        catch (InvalidOperationException) { /* Mouse capture ended before the drag started. */ }
    }
}
