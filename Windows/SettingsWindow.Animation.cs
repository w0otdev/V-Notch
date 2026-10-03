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

    private void PlayEntranceAnimation()
    {
        var easeOut = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
        var easeOutStrong = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 7 };
        var itemEase = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        var totalDur = TimeSpan.FromMilliseconds(650);

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

        ShellScale.ScaleX = startScaleX;
        ShellScale.ScaleY = startScaleY;
        ShellTranslate.Y = 0;
        MainShell.CornerRadius = new CornerRadius(startRadius);
        FooterBar.CornerRadius = new CornerRadius(0, 0, startRadius, startRadius);

        double finalLeft = Left;
        double finalTop = Top;

        double targetStartLeft = notchLeft + notchW / 2.0 - ActualWidth / 2.0;
        Left = targetStartLeft;
        Top = notchTop;

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

        var moveTop = new DoubleAnimation(Top, finalTop, totalDur)
        {
            EasingFunction = easeOutStrong
        };
        Timeline.SetDesiredFrameRate(moveTop, fps);

        // Release the HoldEnd fill once the fly-in finishes so Top/Left track
        moveTop.Completed += (s, e) =>
        {
            if (_isClosing) return;
            Top = finalTop;
            this.BeginAnimation(TopProperty, null);
        };

        if (Math.Abs(targetStartLeft - finalLeft) >= 0.5)
        {
            var moveLeft = new DoubleAnimation(Left, finalLeft, totalDur)
            {
                EasingFunction = easeOutStrong
            };
            Timeline.SetDesiredFrameRate(moveLeft, fps);
            moveLeft.Completed += (s, e) =>
            {
                if (_isClosing) return;
                Left = finalLeft;
                this.BeginAnimation(LeftProperty, null);
            };
            this.BeginAnimation(LeftProperty, moveLeft);
        }
        else
        {
            Left = finalLeft;
        }

        expandX.Completed += (s, e) =>
        {
            if (_isClosing) return;

            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                if (_isClosing) return;

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
            }));
        };

        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, expandX);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, expandY);
        this.BeginAnimation(ShellCornerRadiusProperty, cornerAnim);
        this.BeginAnimation(TopProperty, moveTop);

        // Entrance: cascade top → bottom (Header → Social → Nav → Card → Footer)
        // Items slide in from -10px (above) to 0 — opposite of the close direction.
        int contentDelay = 250;
        AnimateEntranceItem(SettingsHeader, HeaderTranslate, contentDelay);

        int socialDelay = contentDelay + 60;
        AnimateSocialIcon(SocialWebsite, SocialWebsiteTranslate, socialDelay);
        AnimateSocialIcon(SocialGitHub, SocialGitHubTranslate, socialDelay + 50);
        AnimateSocialIcon(SocialFacebook, SocialFacebookTranslate, socialDelay + 100);
        AnimateSocialIcon(SocialDiscord, SocialDiscordTranslate, socialDelay + 150);

        AnimateEntranceItem(NavPanel, NavPanelTranslate, contentDelay + 80);

        // Pass entranceDirection so the card slides in from above on first open
        AnimateActivePanel(_activeNav, entranceDirection: true);

        AnimateEntranceItem(FooterBar, FooterTranslate, contentDelay + 200);

        void AnimateSocialIcon(UIElement element, TranslateTransform translate, int delayMs)
        {
            element.Opacity = 0;
            translate.Y = -6;
            var fade = CreateAnimation(0, 1, 320, itemEase);
            fade.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            element.BeginAnimation(OpacityProperty, fade);

            // Slide in from above (-6px → 0)
            var slide = CreateAnimation(-6, 0, 380, itemEase);
            slide.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            translate.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        void AnimateEntranceItem(UIElement element, TranslateTransform translate, int delayMs)
        {
            element.Opacity = 0;
            translate.Y = -10;
            var fade = CreateAnimation(0, 1, 380, itemEase);
            fade.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            element.BeginAnimation(OpacityProperty, fade);

            // Slide in from above (-10px → 0) to match the top-down cascade direction
            var slide = CreateAnimation(-10, 0, 480, itemEase);
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

        Timeline.SetDesiredFrameRate(animation, VNotch.Services.AnimationConfig.TargetFps);
        return animation;
    }

    #endregion
}
