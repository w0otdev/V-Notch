using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Services;

namespace VNotch;

public partial class SpotlightWindow
{
    private void BeginAiProviderMenuEntrance(ContextMenu menu)
    {
        // The native popup must not run a second fade/slide over our surface motion.
        if (menu.Parent is Popup popup) popup.PopupAnimation = PopupAnimation.None;
        menu.ApplyTemplate();
        if (menu.Template?.FindName("ProviderMenuSurface", menu) is not FrameworkElement surface) return;

        if (AnimationConfig.ReduceMotion || !AnimationConfig.ClientAreaAnimation ||
            InputManager.Current.MostRecentInputDevice is KeyboardDevice)
        {
            MenuSurfaceSpring.Reset(surface);
            return;
        }

        // The template starts transparent. Resolve placement before its first visible
        // frame so a menu flipped above the logo does not jump to a different origin.
        surface.Opacity = 0;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!ReferenceEquals(_aiProviderMenu, menu) || !menu.IsOpen) return;
            if (AnimationConfig.ReduceMotion || !AnimationConfig.ClientAreaAnimation)
            {
                MenuSurfaceSpring.Reset(surface);
                return;
            }
            Point anchor = AiProviderMenuAnchor(surface);
            MenuSurfaceSpring.Open(surface, anchor, 0.985, 0, anchor.Y < 0.5 ? -2 : 2, fresh: true);
        }));
    }

    private Point AiProviderMenuAnchor(FrameworkElement surface)
    {
        if (surface.ActualWidth > 0 && surface.ActualHeight > 0 &&
            PresentationSource.FromVisual(surface) != null && PresentationSource.FromVisual(AiProviderLogo) != null)
        {
            try
            {
                Point logo = AiProviderLogo.PointToScreen(new Point(AiProviderLogo.ActualWidth / 2, AiProviderLogo.ActualHeight / 2));
                Point anchor = surface.PointFromScreen(logo);
                return new Point(Math.Clamp(anchor.X / surface.ActualWidth, 0, 1), Math.Clamp(anchor.Y / surface.ActualHeight, 0, 1));
            }
            catch (InvalidOperationException) { }
        }
        return new Point(0.1, 0);
    }

    private static void ResetAiProviderMenuMotion(ContextMenu menu)
    {
        if (menu.Template?.FindName("ProviderMenuSurface", menu) is FrameworkElement surface)
            MenuSurfaceSpring.Reset(surface);
        if (menu.Parent is Popup popup) popup.PopupAnimation = PopupAnimation.None;
    }
}
