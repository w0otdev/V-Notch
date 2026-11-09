using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private int _appNotificationBlurVersion;

    private bool EnsureAppNotificationBlurCover()
    {
        if (!_settings.EnableBlurEffects || AnimationConfig.ReduceMotion)
        {
            ResetAppNotificationBlurCover();
            return false;
        }
        if (AppNotificationBlurCover.Source != null) return true;

        try
        {
            // Rasterize once per transition; only opacity changes while the fixed
            // blur is visible. The live text keeps its normal rendering settings.
            double width = AppNotificationBanner.Width;
            AppNotificationContent.Measure(new Size(width, double.PositiveInfinity));
            double height = AppNotificationContent.DesiredSize.Height;
            if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
                return false;
            AppNotificationContent.Arrange(new Rect(0, 0, width, height));
            AppNotificationContent.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(AppNotificationContent);
            var snapshot = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(width * dpi.DpiScaleX)),
                Math.Max(1, (int)Math.Ceiling(height * dpi.DpiScaleY)),
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            snapshot.Render(AppNotificationContent);
            snapshot.Freeze();
            AppNotificationBlurCover.Source = snapshot;
            AppNotificationBlurCover.Visibility = Visibility.Visible;
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug("APP-NOTIFY", $"Blur cover unavailable: {ex.GetType().Name}");
            ResetAppNotificationBlurCover();
            return false;
        }
    }

    private bool TransitionAppNotificationBlurCover(bool entering)
    {
        if (!EnsureAppNotificationBlurCover()) return false;
        int version = ++_appNotificationBlurVersion;
        int duration = entering ? NotificationTransitionMs : 120;
        if (entering)
        {
            SetAppNotificationOpacity(AppNotificationContent, 0);
            SetAppNotificationOpacity(AppNotificationBlurCover, 1);
        }
        double from = AppNotificationBlurCover.Opacity;
        SetAppNotificationOpacity(AppNotificationBlurCover, from);
        var fade = AppNotificationMotion(from, entering ? 0 : 1, duration, easing: _easePowerOut3);
        if (entering)
        {
            fade.Completed += (_, _) =>
            {
                if (version != _appNotificationBlurVersion || !_isAppNotificationBannerVisible || _isAppNotificationBannerExiting) return;
                ResetAppNotificationBlurCover();
            };
        }
        AppNotificationBlurCover.BeginAnimation(OpacityProperty, fade);
        AnimateAppNotificationOpacity(AppNotificationContent, entering ? 1 : 0, duration, easing: _easePowerOut3);
        return true;
    }

    private void ResetAppNotificationBlurCover()
    {
        ++_appNotificationBlurVersion;
        SetAppNotificationOpacity(AppNotificationContent, 1);
        SetAppNotificationOpacity(AppNotificationBlurCover, 0);
        AppNotificationBlurCover.Visibility = Visibility.Collapsed;
        AppNotificationBlurCover.Source = null;
    }
}
