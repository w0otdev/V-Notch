using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Presenters;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    #region Entrance Animation

    private static readonly ExponentialEase EntranceEaseOut = CreateFrozenExponentialEase(6);
    private static readonly ExponentialEase EntranceEaseOutStrong = CreateFrozenExponentialEase(7);

    private static ExponentialEase CreateFrozenExponentialEase(double exponent)
    {
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = exponent };
        ease.Freeze();
        return ease;
    }

    private void PlayEntranceAnimation()
    {
        var easeOut = EntranceEaseOut;
        var easeOutStrong = EntranceEaseOutStrong;
        var itemEase = EntranceEaseOut;
        int fps = Math.Min(60, VNotch.Services.AnimationConfig.TargetFps);

        var totalDur = TimeSpan.FromMilliseconds(520);

        double notchLeft = 0, notchTop = 0, notchW = 230, notchH = 32, notchRadius = 8;
        if (Owner is MainWindow mainWindow)
        {
            var rect = mainWindow.GetNotchScreenRect();
            notchLeft = rect.Left;
            notchTop = rect.Top;
            notchW = rect.Width;
            notchH = rect.Height;
            notchRadius = rect.CornerRadius;
        }

        double shellWidth = ActualWidth > 0 ? ActualWidth - 36 : 824;
        double shellHeight = ActualHeight > 0 ? ActualHeight - 36 : 584;
        double startScaleX = Math.Max(0.02, notchW / shellWidth);
        double startScaleY = Math.Max(0.02, notchH / shellHeight);
        double startRadius = Math.Max(notchRadius, 12);

        MainShell.Opacity = 1.0;
        MainShell.RenderTransformOrigin = new Point(0.5, 0.0);
        MainShell.Effect = null;
        MainShell.SnapsToDevicePixels = false;
        MainShell.UseLayoutRounding = false;
        this.SnapsToDevicePixels = false;
        this.UseLayoutRounding = false;
        ShellContent.SnapsToDevicePixels = false;
        ShellContent.UseLayoutRounding = false;

        double finalLeft = Left;
        double finalTop = Top;

        double targetStartLeft = notchLeft + notchW / 2.0 - ActualWidth / 2.0;
        double startOffsetX = Math.Abs(targetStartLeft - finalLeft) >= 0.5 ? targetStartLeft - finalLeft : 0;
        double startOffsetY = notchTop - finalTop;

        ShellScale.ScaleX = startScaleX;
        ShellScale.ScaleY = startScaleY;
        ShellTranslate.X = startOffsetX;
        ShellTranslate.Y = startOffsetY;
        MainShell.CornerRadius = new CornerRadius(startRadius);
        FooterBar.CornerRadius = new CornerRadius(0, 0, startRadius, startRadius);

        var expandX = new DoubleAnimation(startScaleX, 1.0, totalDur)
        {
            EasingFunction = easeOutStrong
        };
        Timeline.SetDesiredFrameRate(expandX, fps);

        var expandY = new DoubleAnimation(startScaleY, 1.0, totalDur)
        {
            EasingFunction = easeOutStrong
        };
        Timeline.SetDesiredFrameRate(expandY, fps);

        var cornerAnim = new DoubleAnimation(startRadius, 24, totalDur)
        {
            EasingFunction = easeOut
        };
        Timeline.SetDesiredFrameRate(cornerAnim, fps);

        var moveY = new DoubleAnimation(startOffsetY, 0.0, totalDur)
        {
            EasingFunction = easeOutStrong
        };
        Timeline.SetDesiredFrameRate(moveY, fps);
        moveY.Completed += (s, e) =>
        {
            if (_isClosing) return;
            ShellTranslate.Y = 0;
            ShellTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        };

        if (Math.Abs(startOffsetX) >= 0.5)
        {
            var moveX = new DoubleAnimation(startOffsetX, 0.0, totalDur)
            {
                EasingFunction = easeOutStrong
            };
            Timeline.SetDesiredFrameRate(moveX, fps);
            moveX.Completed += (s, e) =>
            {
                if (_isClosing) return;
                ShellTranslate.X = 0;
                ShellTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            };
            ShellTranslate.BeginAnimation(TranslateTransform.XProperty, moveX);
        }
        else
        {
            ShellTranslate.X = 0;
        }

        expandX.Completed += (s, e) =>
        {
            if (_isClosing) return;

            void AttachEntranceShadow()
            {
                if (_isClosing) return;
                if (Mouse.LeftButton == MouseButtonState.Pressed)
                {
                    PreviewMouseLeftButtonUp += OnMouseReleasedBeforeShadow;
                    return;
                }

                var shadow = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = System.Windows.Media.Colors.Black,
                    BlurRadius = 30,
                    ShadowDepth = 0,
                    Opacity = 0.0
                };
                MainShell.Effect = shadow;

                var shadowFade = new DoubleAnimation(0.0, 0.42, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = easeOut
                };
                Timeline.SetDesiredFrameRate(shadowFade, fps);

                shadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, shadowFade);
            }

            void OnMouseReleasedBeforeShadow(object sender, MouseButtonEventArgs args)
            {
                PreviewMouseLeftButtonUp -= OnMouseReleasedBeforeShadow;
                if (_isClosing) return;
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(AttachEntranceShadow));
            }

            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(AttachEntranceShadow));
        };

        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, expandX);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, expandY);
        this.BeginAnimation(ShellCornerRadiusProperty, cornerAnim);
        ShellTranslate.BeginAnimation(TranslateTransform.YProperty, moveY);

        // Entrance: cascade top → bottom (Header → Social → Nav → Card → Footer)
        // Items slide in from -10px (above) to 0 — opposite of the close direction.
        int contentDelay = 180;
        AnimateEntranceItem(SettingsHeader, HeaderTranslate, contentDelay);

        int socialDelay = contentDelay + 50;
        AnimateSocialIcon(SocialWebsite, SocialWebsiteTranslate, socialDelay);
        AnimateSocialIcon(SocialGitHub, SocialGitHubTranslate, socialDelay + 40);
        AnimateSocialIcon(SocialFacebook, SocialFacebookTranslate, socialDelay + 80);
        AnimateSocialIcon(SocialDiscord, SocialDiscordTranslate, socialDelay + 120);

        AnimateEntranceItem(NavPanel, NavPanelTranslate, contentDelay + 60);

        // Pass entranceDirection so the card slides in from above on first open
        AnimateActivePanel(_activeNav, entranceDirection: true);

        AnimateEntranceItem(FooterBar, FooterTranslate, contentDelay + 120);

        void AnimateSocialIcon(UIElement element, TranslateTransform translate, int delayMs)
        {
            element.Opacity = 0;
            translate.Y = -6;
            var fade = CreateAnimation(0, 1, 280, itemEase);
            fade.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            element.BeginAnimation(OpacityProperty, fade);

            // Slide in from above (-6px → 0)
            var slide = CreateAnimation(-6, 0, 320, itemEase);
            slide.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            translate.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        void AnimateEntranceItem(UIElement element, TranslateTransform translate, int delayMs)
        {
            element.Opacity = 0;
            translate.Y = -10;
            var fade = CreateAnimation(0, 1, 320, itemEase);
            fade.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            element.BeginAnimation(OpacityProperty, fade);

            // Slide in from above (-10px → 0) to match the top-down cascade direction
            var slide = CreateAnimation(-10, 0, 360, itemEase);
            slide.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            translate.BeginAnimation(TranslateTransform.YProperty, slide);
        }
    }

    private static DoubleAnimation CreateAnimation(double from, double to, int durationMs, IEasingFunction easing)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = easing
        };

        Timeline.SetDesiredFrameRate(animation, Math.Min(60, VNotch.Services.AnimationConfig.TargetFps));
        return animation;
    }

    #endregion
}
