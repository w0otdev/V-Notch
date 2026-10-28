using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
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

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct VnProgressiveBlurStep
    {
        public int DelayMs;
        public int FadeDurationMs;
        public int BlurDurationMs;
        public int SlideDurationMs;
        public double StartBlurRadius;
        public double StartOpacity;
        public double StartTranslateY;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_ui_compute_progressive_blur_cascade(
        int itemCount,
        int baseDelayMs,
        int direction,
        byte isMenuList,
        VnProgressiveBlurStep* outSteps,
        int outCap,
        out int outWritten);

    private readonly List<(UIElement Element, double TargetOpacity)> _activeBlurredElements = new();
    private readonly List<(UIElement Element, double TargetOpacity)> _activeMenuBlurredElements = new();
    private int _progressiveContentBlurVersion;
    private int _progressiveMenuBlurVersion;

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

        var totalDur = TimeSpan.FromMilliseconds(640);

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

        AnimateEntranceItem(NavPanel, NavPanelTranslate, contentDelay + 40, applyBlur: false);
        AnimateSidebarMenuProgressiveBlur(contentDelay + 55, fps);

        // Pass entranceDirection so the card slides in from above on first open
        AnimateActivePanel(_activeNav, entranceDirection: true);

        AnimateEntranceItem(FooterBar, FooterTranslate, contentDelay + 120, applyBlur: true);

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

        void AnimateEntranceItem(UIElement element, TranslateTransform translate, int delayMs, bool applyBlur = true)
        {
            element.Opacity = 0;
            translate.Y = -10;
            var fade = CreateAnimation(0, 1, 440, itemEase);
            fade.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            element.BeginAnimation(OpacityProperty, fade);

            // Slide in from above (-10px → 0) to match the top-down cascade direction
            var slide = CreateAnimation(-10, 0, 500, itemEase);
            slide.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            translate.BeginAnimation(TranslateTransform.YProperty, slide);

            if (applyBlur && !VNotch.Services.AnimationConfig.ReduceMotion)
            {
                var blur = new BlurEffect { Radius = 14, RenderingBias = RenderingBias.Performance };
                element.Effect = blur;
                var blurAnim = CreateAnimation(14, 0, 540, itemEase);
                blurAnim.BeginTime = TimeSpan.FromMilliseconds(delayMs);
                blurAnim.Completed += (_, _) =>
                {
                    if (ReferenceEquals(element.Effect, blur))
                    {
                        element.Effect = null;
                    }
                };
                blur.BeginAnimation(BlurEffect.RadiusProperty, blurAnim);
            }
        }
    }

    private void AnimateSidebarMenuProgressiveBlur(int baseDelayMs, int fps)
    {
        var menuItems = new List<UIElement>(_navOrder.Length);
        foreach (string key in _navOrder)
        {
            if (_navButtons.TryGetValue(key, out var btn) && btn.Visibility == Visibility.Visible)
            {
                menuItems.Add(btn);
            }
        }

        ApplyProgressiveBlurCascade(menuItems, baseDelayMs, direction: -1, isMenuList: true, fps);
    }

    internal static TranslateTransform EnsureElementTranslateTransform(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform tt && !tt.IsFrozen)
        {
            return tt;
        }

        if (element.RenderTransform is TransformGroup group && !group.IsFrozen)
        {
            for (int i = 0; i < group.Children.Count; i++)
            {
                if (group.Children[i] is TranslateTransform childTt && !childTt.IsFrozen)
                {
                    return childTt;
                }
            }

            var added = new TranslateTransform(0, 0);
            group.Children.Add(added);
            return added;
        }

        var created = new TranslateTransform(0, 0);
        element.RenderTransform = created;
        return created;
    }

    internal void ResetProgressiveBlurElements(bool isMenuList)
    {
        var list = isMenuList ? _activeMenuBlurredElements : _activeBlurredElements;
        for (int i = 0; i < list.Count; i++)
        {
            var (el, targetOpacity) = list[i];
            el.BeginAnimation(OpacityProperty, null);
            el.Opacity = targetOpacity;
            if (el.Effect is BlurEffect blur && !blur.IsFrozen)
            {
                blur.BeginAnimation(BlurEffect.RadiusProperty, null);
            }
            el.Effect = null;
            var tt = EnsureElementTranslateTransform(el);
            tt.BeginAnimation(TranslateTransform.YProperty, null);
            tt.Y = 0;
        }
        list.Clear();
    }

    internal static double ResolveElementRestingOpacity(UIElement el)
    {
        double current = el.Opacity;
        if (current > 0.05 && current < 0.99)
        {
            return current;
        }
        return !el.IsEnabled ? 0.4 : 1.0;
    }

    internal unsafe void ApplyProgressiveBlurCascade(
        IReadOnlyList<UIElement> items,
        int baseDelayMs,
        int direction,
        bool isMenuList,
        int fps)
    {
        ResetProgressiveBlurElements(isMenuList);
        if (items.Count == 0) return;

        if (VNotch.Services.AnimationConfig.ReduceMotion)
        {
            for (int i = 0; i < items.Count; i++)
            {
                double rest = ResolveElementRestingOpacity(items[i]);
                items[i].BeginAnimation(OpacityProperty, null);
                items[i].Opacity = rest;
                items[i].Effect = null;
            }
            return;
        }

        const int MaxCascadeItems = 32;
        int count = Math.Min(items.Count, MaxCascadeItems);
        VnProgressiveBlurStep* steps = stackalloc VnProgressiveBlurStep[MaxCascadeItems];
        if (vn_ui_compute_progressive_blur_cascade(
                count,
                baseDelayMs,
                direction,
                isMenuList ? (byte)1 : (byte)0,
                steps,
                MaxCascadeItems,
                out int written) != 0 || written <= 0)
        {
            return;
        }

        int version = isMenuList ? ++_progressiveMenuBlurVersion : ++_progressiveContentBlurVersion;
        var trackedList = isMenuList ? _activeMenuBlurredElements : _activeBlurredElements;
        var ease = EntranceEaseOut;

        for (int i = 0; i < written; i++)
        {
            UIElement el = items[i];
            VnProgressiveBlurStep step = steps[i];
            double targetOpacity = ResolveElementRestingOpacity(el);
            trackedList.Add((el, targetOpacity));

            var translate = EnsureElementTranslateTransform(el);
            el.BeginAnimation(OpacityProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            el.Opacity = step.StartOpacity;
            translate.Y = step.StartTranslateY;

            var blur = new BlurEffect
            {
                Radius = step.StartBlurRadius,
                RenderingBias = RenderingBias.Performance
            };
            el.Effect = blur;

            var delay = TimeSpan.FromMilliseconds(step.DelayMs);
            var fadeAnim = new DoubleAnimation(step.StartOpacity, targetOpacity, TimeSpan.FromMilliseconds(step.FadeDurationMs))
            {
                BeginTime = delay,
                EasingFunction = ease
            };
            var slideAnim = new DoubleAnimation(step.StartTranslateY, 0.0, TimeSpan.FromMilliseconds(step.SlideDurationMs))
            {
                BeginTime = delay,
                EasingFunction = ease
            };
            var blurAnim = new DoubleAnimation(step.StartBlurRadius, 0.0, TimeSpan.FromMilliseconds(step.BlurDurationMs))
            {
                BeginTime = delay,
                EasingFunction = ease
            };

            Timeline.SetDesiredFrameRate(fadeAnim, fps);
            Timeline.SetDesiredFrameRate(slideAnim, fps);
            Timeline.SetDesiredFrameRate(blurAnim, fps);

            blurAnim.Completed += (_, _) =>
            {
                int currentVer = isMenuList ? _progressiveMenuBlurVersion : _progressiveContentBlurVersion;
                if (version != currentVer) return;

                el.BeginAnimation(OpacityProperty, null);
                el.Opacity = targetOpacity;
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.Y = 0.0;
                if (ReferenceEquals(el.Effect, blur))
                {
                    el.Effect = null;
                }
            };

            el.BeginAnimation(OpacityProperty, fadeAnim);
            translate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
            blur.BeginAnimation(BlurEffect.RadiusProperty, blurAnim);
        }

        for (int i = written; i < items.Count; i++)
        {
            double rest = ResolveElementRestingOpacity(items[i]);
            items[i].BeginAnimation(OpacityProperty, null);
            items[i].Opacity = rest;
            items[i].Effect = null;
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
