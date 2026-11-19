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

    private readonly Dictionary<UIElement, TranslateTransform?> _activeBlurredElements = new();
    private readonly Dictionary<UIElement, TranslateTransform?> _activeMenuBlurredElements = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<UIElement, TranslateTransform> _componentSlides = new();
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

    internal void PlayEntranceAnimation()
    {
        var easeOut = EntranceEaseOut;
        var easeOutStrong = EntranceEaseOutStrong;
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

            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => AttachEntranceShadow(fps)));
        };

        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, expandX);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, expandY);
        this.BeginAnimation(ShellCornerRadiusProperty, cornerAnim);
        ShellTranslate.BeginAnimation(TranslateTransform.YProperty, moveY);

        AnimateEntranceContent(fps);
    }

    private void AttachEntranceShadow(int fps)
    {
        if (_isClosing) return;
        MainShell.Effect = null;
        if (!(EnableShadowCheck?.IsChecked ?? _settings.EnableShadow))
        {
            if (MainShellShadow != null) MainShellShadow.Effect = null;
            return;
        }
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
        if (MainShellShadow != null)
            MainShellShadow.Effect = shadow;

        var shadowFade = new DoubleAnimation(0.0, 0.42, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = EntranceEaseOut
        };
        Timeline.SetDesiredFrameRate(shadowFade, fps);

        shadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, shadowFade);
    }

    private void OnMouseReleasedBeforeShadow(object sender, MouseButtonEventArgs args)
    {
        PreviewMouseLeftButtonUp -= OnMouseReleasedBeforeShadow;
        if (_isClosing) return;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => AttachEntranceShadow(Math.Min(60, AnimationConfig.TargetFps))));
    }

    internal void ApplyWindowShadow(bool enabled)
    {
        if (Cpp26PromptShadowLayer != null)
            Cpp26PromptShadowLayer.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (RestartPromptShadowLayer != null)
            RestartPromptShadowLayer.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (MainShell != null) MainShell.Effect = null;
        if (MainShellShadow == null) return;
        if (!enabled)
        {
            MainShellShadow.Effect = null;
            return;
        }
        if (IsLoaded && !_isClosing && MainShellShadow.Effect is not System.Windows.Media.Effects.DropShadowEffect)
        {
            MainShellShadow.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = System.Windows.Media.Colors.Black,
                BlurRadius = 30,
                ShadowDepth = 0,
                Opacity = 0.42
            };
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
        if (isMenuList) _progressiveMenuBlurVersion++;
        else
        {
            _progressiveContentBlurVersion++;
            StopComponentReveal();
        }
        var list = isMenuList ? _activeMenuBlurredElements : _activeBlurredElements;
        foreach (var (el, translate) in list)
        {
            el.BeginAnimation(OpacityProperty, null);
            if (translate != null)
            {
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.Y = 0;
            }
        }
        list.Clear();
    }

    internal static double ResolveElementRestingOpacity(UIElement el) =>
        (double)el.GetAnimationBaseValue(OpacityProperty);

    private void AnimateSettingsOpacity(UIElement element, double targetOpacity, bool animate, int durationMs = 240)
    {
        double from = element.Opacity;
        element.BeginAnimation(OpacityProperty, null);
        // Persist the latest feature state before any reveal can replace this clock.
        element.Opacity = targetOpacity;
        if (!animate || _isLoadingSettings || AnimationConfig.ReduceMotion || from == targetOpacity) return;

        var fade = new DoubleAnimation(from, targetOpacity, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(fade, AnimationConfig.TargetFps);
        element.BeginAnimation(OpacityProperty, fade);
    }

    private void RevealSettingsContent(IReadOnlyList<UIElement> notes, IReadOnlyList<UIElement>? versions) =>
        ApplyProgressiveBlurCascade(notes, 0, direction: -1, isMenuList: false, Math.Min(60, AnimationConfig.TargetFps), parallelColumn: versions);

    internal unsafe void ApplyProgressiveBlurCascade(
        IReadOnlyList<UIElement> items,
        int baseDelayMs,
        int direction,
        bool isMenuList,
        int fps, bool appendComponents = false, IReadOnlyList<UIElement>? parallelColumn = null)
    {
        if (!appendComponents) ResetProgressiveBlurElements(isMenuList);
        int stepsPerColumn = Math.Max(items.Count, parallelColumn?.Count ?? 0);
        if (stepsPerColumn == 0) return;

        if (AnimationConfig.ReduceMotion || !AnimationConfig.ClientAreaAnimation)
        {
            for (int i = 0; i < items.Count; i++)
            {
                items[i].BeginAnimation(OpacityProperty, null);
            }
            if (parallelColumn != null)
                foreach (var item in parallelColumn) item.BeginAnimation(OpacityProperty, null);
            return;
        }

        const int MaxCascadeItems = 32;
        int count = Math.Min(stepsPerColumn, MaxCascadeItems);
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

        int version = isMenuList ? _progressiveMenuBlurVersion : _progressiveContentBlurVersion;
        var trackedList = isMenuList ? _activeMenuBlurredElements : _activeBlurredElements;
        var ease = EntranceEaseOut;
        var batch = new ParallelTimeline();
        Timeline.SetDesiredFrameRate(batch, fps);
        var targets = new List<(DependencyObject Target, DependencyProperty Property)>();
        var trees = new HashSet<SettingsTreePanel>();

        // Remaining rows share the last step so long pages finish on time.
        int totalItems = items.Count + (parallelColumn?.Count ?? 0);
        for (int index = 0; index < totalItems; index++)
        {
            int i = index < items.Count ? index : index - items.Count;
            UIElement el = index < items.Count ? items[i] : parallelColumn![i];
            if (appendComponents)
                for (var parent = VisualTreeHelper.GetParent(el); parent != null; parent = VisualTreeHelper.GetParent(parent))
                    if (parent is SettingsTreePanel tree) trees.Add(tree);
            VnProgressiveBlurStep step = steps[Math.Min(i, written - 1)];
            double targetOpacity = ResolveElementRestingOpacity(el);
            var translate = appendComponents ? GetComponentSlide(el) : EnsureElementTranslateTransform(el);
            trackedList.Add(el, translate);
            el.BeginAnimation(OpacityProperty, null);
            if (translate != null)
            {
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.Y = step.StartTranslateY;
            }

            double delayMs = appendComponents && items.Count > written
                ? baseDelayMs + (steps[written - 1].DelayMs - baseDelayMs) * (i / (items.Count - 1.0))
                : step.DelayMs;
            var delay = TimeSpan.FromMilliseconds(delayMs);
            // Keep the underlying opacity owned by its binding/style. A local value
            // would pin disabled feature groups at their old dimmed state.
            // The initial keyframes hold the start value throughout the stagger delay.
            var fadeAnim = new DoubleAnimationUsingKeyFrames
            {
                FillBehavior = FillBehavior.Stop
            };
            fadeAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(step.StartOpacity, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            fadeAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(step.StartOpacity, KeyTime.FromTimeSpan(delay)));
            fadeAnim.KeyFrames.Add(new EasingDoubleKeyFrame(targetOpacity,
                KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(step.FadeDurationMs)), ease));
            var slideAnim = new DoubleAnimation(step.StartTranslateY, 0.0, TimeSpan.FromMilliseconds(step.SlideDurationMs))
            {
                BeginTime = delay,
                EasingFunction = ease
            };


            Timeline completion = translate != null && step.SlideDurationMs >= step.FadeDurationMs ? slideAnim : fadeAnim;
            completion.Completed += (_, _) =>
            {
                int currentVer = isMenuList ? _progressiveMenuBlurVersion : _progressiveContentBlurVersion;
                if (version != currentVer) return;

                el.BeginAnimation(OpacityProperty, null);
                if (translate != null)
                {
                    translate.BeginAnimation(TranslateTransform.YProperty, null);
                    translate.Y = 0;
                }
                trackedList.Remove(el);
            };

            batch.Children.Add(fadeAnim);
            targets.Add((el, OpacityProperty));
            if (translate != null)
            {
                batch.Children.Add(slideAnim);
                targets.Add((translate, TranslateTransform.YProperty));
            }
        }
        var clock = (ClockGroup)batch.CreateClock();
        if (trees.Count > 0)
        {
            // The connectors are parent-drawn. Sample the live label position and
            // opacity on the same ticks as the reveal, including its final frame.
            clock.CurrentTimeInvalidated += (_, _) =>
            {
                foreach (var tree in trees) tree.RefreshConnectors();
            };
        }
        for (int i = 0; i < targets.Count; i++)
        {
            var (target, property) = targets[i];
            var child = (AnimationClock)clock.Children[i];
            if (target is UIElement element) element.ApplyAnimationClock(property, child);
            else ((TranslateTransform)target).ApplyAnimationClock(property, child);
        }
    }

    private TranslateTransform? GetComponentSlide(UIElement component)
    {
        if (_componentSlides.TryGetValue(component, out var owned) && ReferenceEquals(component.RenderTransform, owned))
            return owned;
        // A live preview or icon can own its transform. Fade it in place.
        if (component.RenderTransform != Transform.Identity) return null;
        var slide = _componentSlides.GetOrCreateValue(component);
        component.SetCurrentValue(RenderTransformProperty, slide);
        return slide;
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

    private void AnimateEntranceContent(int fps)
    {
        var itemEase = EntranceEaseOut;
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

        if (SettingsScrollViewer.IsVisible && _revealingPage == null)
            AnimateActivePanel(_activeNav, baseDelayMs: 200);

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

            // Slide in from above (-10px -> 0) to match the top-down cascade direction
            var slide = CreateAnimation(-10, 0, 500, itemEase);
            slide.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            translate.BeginAnimation(TranslateTransform.YProperty, slide);
            element.Effect = null;
        }
    }

}
