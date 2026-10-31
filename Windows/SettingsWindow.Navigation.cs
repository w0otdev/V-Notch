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
    #region Navigation

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnNavTabMotionOut
    {
        public double TargetY;
        public double TargetHeight;
        public double PeakScaleY;
        public double DipScaleX;
        public double ReboundScaleY;
        public double ReboundScaleX;
        public double ContentShiftX;
        public int PeakTimeMs;
        public int ReboundTimeMs;
        public int DurationMs;
    }

    [LibraryImport("VNotch.Native", EntryPoint = "vn_animation_compute_nav_tab_motion")]
    private static unsafe partial int NativeComputeNavTabMotion(
        double currentY,
        double targetY,
        double itemHeight,
        byte reducedMotion,
        VnNavTabMotionOut* outMotion);

    private string _activeNav = NavSectionAppearance;
    private string? _hoveredNav;
    private int _lastNavDirection = 1;
    private readonly Dictionary<string, StackPanel> _navPanels = new();
    private readonly Dictionary<string, Border> _navButtons = new();

    private static readonly SolidColorBrush _navHoverTextBrush = CreateFrozenNavBrush(0xFF, 0xE2, 0xE8, 0xF0);
    private static readonly CubicEase _navEaseOut = CreateFrozenNavEase(EasingMode.EaseOut);

    private static SolidColorBrush CreateFrozenNavBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static CubicEase CreateFrozenNavEase(EasingMode mode)
    {
        var ease = new CubicEase { EasingMode = mode };
        ease.Freeze();
        return ease;
    }

    private void InitializeNavigation()
    {
        _navPanels[NavSectionSearching] = PanelSearching;
        _navPanels[NavSectionAppearance] = PanelAppearance;
        _navPanels[NavSectionSkins] = PanelSkins;
        _navPanels[NavSectionBehavior] = PanelBehavior;
        _navPanels[NavSectionDevices] = PanelDevices;
        _navPanels[NavSectionSystem] = PanelSystem;
        _navPanels[NavSectionPrivacy] = PanelPrivacy;
        _navPanels[NavSectionSpotlight] = PanelSpotlight;
        _navPanels["Translation"] = PanelTranslation;
        _navPanels[NavSectionFileTray] = PanelFileTray;
        _navPanels[NavSectionAdvanced] = PanelAdvanced;
        _navPanels[NavSectionPerformance] = PanelPerformance;
        _navPanels[NavSectionDonating] = PanelDonating;
        _navPanels[NavSectionUpdates] = PanelUpdates;

        _navButtons[NavSectionSearching] = NavSearching;
        _navButtons[NavSectionAppearance] = NavAppearance;
        _navButtons[NavSectionSkins] = NavSkins;
        _navButtons[NavSectionBehavior] = NavBehavior;
        _navButtons[NavSectionDevices] = NavDevices;
        _navButtons[NavSectionSystem] = NavSystem;
        _navButtons[NavSectionPrivacy] = NavPrivacy;
        _navButtons[NavSectionSpotlight] = NavSpotlight;
        _navButtons["Translation"] = NavTranslation;
        _navButtons[NavSectionFileTray] = NavFileTray;
        _navButtons[NavSectionAdvanced] = NavAdvanced;
        _navButtons[NavSectionPerformance] = NavPerformance;
        _navButtons[NavSectionDonating] = NavDonating;
        _navButtons[NavSectionUpdates] = NavUpdates;

        foreach (var (key, btn) in _navButtons)
        {
            btn.Background = _transparentBrush;
            btn.BorderBrush = _transparentBrush;
            btn.MouseEnter -= NavButton_MouseEnter;
            btn.MouseEnter += NavButton_MouseEnter;
            btn.MouseLeave -= NavButton_MouseLeave;
            btn.MouseLeave += NavButton_MouseLeave;
            UpdateNavButtonVisual(btn, string.Equals(key, _activeNav, StringComparison.Ordinal), animate: false);
        }

        SyncNavIndicators(animate: false);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => SyncNavIndicators(animate: false));
        UpdateSectionHeader();
    }

    private (double Y, double Height) ResolveNavButtonSlot(Border btn)
    {
        double height = btn.ActualHeight > 0 ? btn.ActualHeight : (btn.MinHeight > 0 ? btn.MinHeight : 30.0);
        if (NavItemsStack != null && btn.ActualHeight > 0 && NavItemsStack.ActualHeight > 0)
        {
            try
            {
                Point pt = btn.TranslatePoint(new Point(0, 0), NavItemsStack);
                if (!double.IsNaN(pt.Y) && !double.IsInfinity(pt.Y))
                {
                    return (Math.Max(0.0, pt.Y), height);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        int visibleIndex = 0;
        foreach (string key in _navOrder)
        {
            if (!_navButtons.TryGetValue(key, out var candidate) || candidate.Visibility != Visibility.Visible)
                continue;
            if (ReferenceEquals(candidate, btn))
                break;
            visibleIndex++;
        }

        double slotStride = height + btn.Margin.Top + btn.Margin.Bottom;
        return (visibleIndex * slotStride + btn.Margin.Top, height);
    }

    private static unsafe VnNavTabMotionOut ComputeNavTabMotion(double currentY, double targetY, double itemHeight, bool animate)
    {
        VnNavTabMotionOut motion = default;
        try
        {
            if (NativeComputeNavTabMotion(currentY, targetY, itemHeight, animate ? (byte)0 : (byte)1, &motion) == 0)
            {
                return motion;
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        return new VnNavTabMotionOut
        {
            TargetY = Math.Max(0.0, targetY),
            TargetHeight = Math.Max(24.0, itemHeight),
            DurationMs = animate ? 200 : 0
        };
    }

    internal void SyncNavIndicators(bool animate)
    {
        string targetKey = _isSearchMode ? NavSectionSearching : _activeNav;
        if (_navButtons.TryGetValue(targetKey, out var activeBtn) && activeBtn.Visibility == Visibility.Visible)
        {
            AnimateNavActiveIndicator(activeBtn, animate);
        }
    }

    private void AnimateNavActiveIndicator(Border targetBtn, bool animate)
    {
        if (NavActiveIndicator == null || NavActiveIndicatorTranslate == null)
            return;

        animate &= IsLoaded && !AnimationConfig.ReduceMotion && SystemParameters.ClientAreaAnimation;
        var (targetY, targetHeight) = ResolveNavButtonSlot(targetBtn);
        double currentY = NavActiveIndicatorTranslate.Y;
        var motion = ComputeNavTabMotion(currentY, targetY, targetHeight, animate);

        NavActiveIndicator.Height = motion.TargetHeight;

        if (!animate || Math.Abs(currentY - motion.TargetY) < 0.5)
        {
            NavActiveIndicatorTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            NavActiveIndicatorTranslate.Y = motion.TargetY;
            return;
        }

        var moveY = AnimationPrimitives.WithFps(new DoubleAnimation(motion.TargetY, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = _navEaseOut,
            FillBehavior = FillBehavior.Stop
        });
        NavActiveIndicatorTranslate.BeginAnimation(TranslateTransform.YProperty, moveY, HandoffBehavior.SnapshotAndReplace);
        NavActiveIndicatorTranslate.Y = motion.TargetY;
    }

    private void AnimateNavHoverIndicator(Border? hoveredBtn)
    {
        if (NavHoverIndicator == null || NavHoverIndicatorTranslate == null)
            return;

        bool animate = IsLoaded && !AnimationConfig.ReduceMotion && SystemParameters.ClientAreaAnimation;
        if (hoveredBtn == null ||
            (hoveredBtn.Tag is string tag && string.Equals(tag, _activeNav, StringComparison.Ordinal)))
        {
            AnimateNavElementOpacity(NavHoverIndicator, 0.0, animate ? 160 : 0);
            return;
        }

        var (targetY, targetHeight) = ResolveNavButtonSlot(hoveredBtn);
        NavHoverIndicator.Height = targetHeight;
        bool wasVisible = NavHoverIndicator.Opacity >= 0.05;

        if (!wasVisible || !animate)
        {
            NavHoverIndicatorTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            NavHoverIndicatorTranslate.Y = targetY;
        }
        else
        {
            AnimateNavTransform(NavHoverIndicatorTranslate, TranslateTransform.YProperty, targetY, 150);
        }

        AnimateNavElementOpacity(NavHoverIndicator, 1.0, animate ? 150 : 0);
    }

    private void NavItemsHost_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoveredNav = null;
        AnimateNavHoverIndicator(null);
    }

    private void NavButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border btn || btn.Tag is not string section) return;
        if (_isSearchMode && section != NavSectionSearching) return;

        _hoveredNav = section;
        bool isActive = string.Equals(section, _activeNav, StringComparison.Ordinal);

        if (!isActive)
        {
            if (btn.Child is StackPanel stack && stack.Children.Count >= 2)
            {
                ApplyNavIconBrush(stack.Children[0], _navHoverTextBrush);
                if (stack.Children[1] is TextBlock text) text.Foreground = _navHoverTextBrush;
            }
            AnimateNavHoverIndicator(btn);
        }
        else
        {
            AnimateNavHoverIndicator(null);
        }
    }

    private void NavButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border btn || btn.Tag is not string section) return;
        bool isActive = string.Equals(section, _activeNav, StringComparison.Ordinal);
        UpdateNavButtonVisual(btn, isActive, animate: true);
    }

    private static void AnimateNavTransform(Animatable target, DependencyProperty property, double to, int durationMs)
    {
        if (durationMs <= 0 || AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation)
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, to);
            return;
        }

        var anim = AnimationPrimitives.WithFps(new DoubleAnimation(to, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = _navEaseOut,
            FillBehavior = FillBehavior.Stop
        });
        target.BeginAnimation(property, anim, HandoffBehavior.SnapshotAndReplace);
        target.SetValue(property, to);
    }

    private static void AnimateNavElementOpacity(UIElement target, double to, int durationMs)
    {
        if (durationMs <= 0 || AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation)
        {
            target.BeginAnimation(OpacityProperty, null);
            target.Opacity = to;
            return;
        }

        var anim = AnimationPrimitives.WithFps(new DoubleAnimation(to, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = _navEaseOut,
            FillBehavior = FillBehavior.Stop
        });
        target.BeginAnimation(OpacityProperty, anim, HandoffBehavior.SnapshotAndReplace);
        target.Opacity = to;
    }

    private void Nav_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is string section)
        {
            if (_isSearchMode && section != NavSectionSearching)
            {
                return;
            }

            NavigateToSection(section);
        }
    }

    private static readonly string[] _navOrder =
    {
        NavSectionSearching, NavSectionAppearance, NavSectionSkins, NavSectionBehavior, NavSectionDevices,
        NavSectionSystem, NavSectionPrivacy, NavSectionSpotlight, "Translation", NavSectionFileTray, NavSectionAdvanced, NavSectionPerformance, NavSectionDonating, NavSectionUpdates
    };

    private int _navTransitionVersion;

    private void UpdateNavButtonVisual(Border btn, bool isActive, bool animate = true)
    {
        btn.Background = _transparentBrush;
        btn.BorderBrush = _transparentBrush;

        if (btn.Child is not StackPanel stack || stack.Children.Count < 2) return;

        var targetBrush = isActive ? _whiteBrush : _navInactiveBrush;
        ApplyNavIconBrush(stack.Children[0], targetBrush);

        if (stack.Children[1] is TextBlock text)
        {
            text.Foreground = targetBrush;
        }
    }

    private static void ApplyNavIconBrush(UIElement element, Brush targetBrush)
    {
        if (element is Viewbox vb)
        {
            if (vb.Child is System.Windows.Shapes.Path path)
            {
                path.Fill = targetBrush;
            }
            else if (vb.Child is Canvas canvas)
            {
                foreach (var child in canvas.Children)
                {
                    if (child is System.Windows.Shapes.Path cp) cp.Fill = targetBrush;
                }
            }
        }
        else if (element is TextBlock iconText)
        {
            iconText.Foreground = targetBrush;
        }
    }

    internal void NavigateToSection(string section)
    {
        if (section == _activeNav) return;

        string previous = _activeNav;
        int direction = Math.Sign(Array.IndexOf(_navOrder, section) - Array.IndexOf(_navOrder, previous));
        if (direction == 0) direction = 1;
        _lastNavDirection = direction;

        if (_navButtons.TryGetValue(previous, out var oldBtn))
        {
            UpdateNavButtonVisual(oldBtn, false, animate: true);
        }

        _activeNav = section;
        UpdateSectionHeader();

        if (_navButtons.TryGetValue(section, out var newBtn))
        {
            UpdateNavButtonVisual(newBtn, true, animate: true);
            AnimateNavActiveIndicator(newBtn, animate: true);
            AnimateNavHoverIndicator(null);
        }

        _navTransitionVersion++;

        foreach (var kvp in _navPanels)
            if (kvp.Key != section) kvp.Value.Visibility = Visibility.Collapsed;
        if (_navPanels.TryGetValue(section, out var newPanel))
            newPanel.Visibility = Visibility.Visible;

        StopSmoothScroll();
        SettingsScrollViewer.ScrollToTop();
        AnimateActivePanel(section, direction);
    }

    private (FrameworkElement? Card, TranslateTransform? Translate) GetSectionCardParts(string section)
    {
        FrameworkElement? card = section switch
        {
            NavSectionAppearance => AppearanceCard,
            NavSectionSearching => SearchingCard,
            NavSectionBehavior => BehaviorCard,
            NavSectionDevices => DisplayCard,
            NavSectionSystem => SystemCard,
            NavSectionPrivacy => PrivacyCard,
            NavSectionSpotlight => SpotlightCard,
            "Translation" => TranslationCard,
            NavSectionFileTray => FileTrayCard,
            NavSectionAdvanced => AdvancedCard,
            NavSectionPerformance => PerformanceCard,
            NavSectionDonating => DonatingCard,
            NavSectionUpdates => UpdatesCard,
            NavSectionSkins => SkinCard,
            _ => null
        };

        TranslateTransform? translate = section switch
        {
            NavSectionAppearance => AppearanceCardTranslate,
            NavSectionSearching => SearchingCardTranslate,
            NavSectionBehavior => BehaviorCardTranslate,
            NavSectionDevices => DisplayCardTranslate,
            NavSectionSystem => SystemCardTranslate,
            NavSectionPrivacy => PrivacyCardTranslate,
            NavSectionSpotlight => SpotlightCardTranslate,
            "Translation" => TranslationCardTranslate,
            NavSectionFileTray => FileTrayCardTranslate,
            NavSectionAdvanced => AdvancedCardTranslate,
            NavSectionPerformance => PerformanceCardTranslate,
            NavSectionDonating => DonatingCardTranslate,
            NavSectionUpdates => UpdatesCardTranslate,
            NavSectionSkins => SkinCardTranslate,
            _ => null
        };

        return (card, translate);
    }

    private void AnimateActivePanel(string section, int direction = 1, bool entranceDirection = false)
    {
        var (card, translate) = GetSectionCardParts(section);
        if (card == null || translate == null) return;

        ResetProgressiveBlurElements(isMenuList: false);

        if (VNotch.Services.AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation)
        {
            card.BeginAnimation(OpacityProperty, null);
            card.Opacity = 1;
            card.Effect = null;
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.Y = 0;
            if (section == NavSectionSystem && BackupCard != null && BackupCardTranslate != null)
            {
                BackupCard.BeginAnimation(OpacityProperty, null);
                BackupCard.Opacity = 1;
                BackupCard.Effect = null;
                BackupCardTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                BackupCardTranslate.Y = 0;
            }
            return;
        }

        if (!entranceDirection)
        {
            int tabFps = VNotch.Services.AnimationConfig.TargetFps;
            double offset = 6.0 * (direction == 0 ? 1 : direction);
            card.Effect = null;
            card.Opacity = 0.25;
            translate.Y = offset;

            var tabFade = new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(180)) { EasingFunction = _navEaseOut };
            var tabSlide = new DoubleAnimation(offset, 0.0, TimeSpan.FromMilliseconds(210)) { EasingFunction = _navEaseOut };
            Timeline.SetDesiredFrameRate(tabFade, tabFps);
            Timeline.SetDesiredFrameRate(tabSlide, tabFps);
            card.BeginAnimation(OpacityProperty, tabFade);
            translate.BeginAnimation(TranslateTransform.YProperty, tabSlide);

            if (section == NavSectionSystem && BackupCard != null && BackupCardTranslate != null)
            {
                BackupCard.Effect = null;
                BackupCard.Opacity = 0.25;
                BackupCardTranslate.Y = offset;
                BackupCard.BeginAnimation(OpacityProperty, tabFade);
                BackupCardTranslate.BeginAnimation(TranslateTransform.YProperty, tabSlide);
            }
            return;
        }

        int fps = Math.Min(60, VNotch.Services.AnimationConfig.TargetFps);
        var ease = EntranceEaseOut;
        int cascadeDir = -1;
        double fromY = -14;
        int baseDelayMs = 200;

        card.Effect = null;
        card.Opacity = 0;
        translate.Y = fromY;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380))
        {
            BeginTime = TimeSpan.FromMilliseconds(baseDelayMs),
            EasingFunction = ease
        };
        var slide = new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(520))
        {
            BeginTime = TimeSpan.FromMilliseconds(baseDelayMs),
            EasingFunction = ease
        };
        Timeline.SetDesiredFrameRate(fade, fps);
        Timeline.SetDesiredFrameRate(slide, fps);

        card.BeginAnimation(OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);

        var cascadeItems = CollectPanelProgressiveBlurTargets(card);
        if (section == NavSectionSystem && BackupCard != null && BackupCardTranslate != null)
        {
            BackupCard.Effect = null;
            BackupCard.Opacity = 0;
            BackupCardTranslate.Y = fromY;

            var backupDelay = TimeSpan.FromMilliseconds(baseDelayMs + 60);
            var backupFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease, BeginTime = backupDelay };
            var backupSlide = new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(520)) { EasingFunction = ease, BeginTime = backupDelay };

            Timeline.SetDesiredFrameRate(backupFade, fps);
            Timeline.SetDesiredFrameRate(backupSlide, fps);

            BackupCard.BeginAnimation(OpacityProperty, backupFade);
            BackupCardTranslate.BeginAnimation(TranslateTransform.YProperty, backupSlide);

            var backupTargets = CollectPanelProgressiveBlurTargets(BackupCard);
            for (int i = 0; i < backupTargets.Count; i++)
            {
                cascadeItems.Add(backupTargets[i]);
            }
        }

        ApplyProgressiveBlurCascade(cascadeItems, baseDelayMs, cascadeDir, isMenuList: false, fps);
    }

    private static List<UIElement> CollectPanelProgressiveBlurTargets(FrameworkElement card)
    {
        const int MaxBlurredChildren = 24;
        var targets = new List<UIElement>(MaxBlurredChildren);

        if (card is Decorator { Child: Panel innerPanel })
        {
            CollectPanelChildrenRecursive(innerPanel, targets, MaxBlurredChildren);
        }

        if (targets.Count == 0)
        {
            targets.Add(card);
        }

        return targets;
    }

    private static void CollectPanelChildrenRecursive(Panel panel, List<UIElement> targets, int maxCount)
    {
        int count = panel.Children.Count;
        for (int i = 0; i < count; i++)
        {
            if (panel.Children[i] is not UIElement child || child.Visibility != Visibility.Visible)
            {
                continue;
            }

            if (child is StackPanel nestedStack && nestedStack.Children.Count > 0)
            {
                if (!nestedStack.IsEnabled || nestedStack.Opacity < 0.99)
                {
                    AddOrResetCascadeTarget(nestedStack, targets, maxCount);
                }
                else
                {
                    CollectPanelChildrenRecursive(nestedStack, targets, maxCount);
                }
                continue;
            }

            if (child is Border { Child: StackPanel borderStack } &&
                borderStack.Children.Count >= 6)
            {
                CollectPanelChildrenRecursive(borderStack, targets, maxCount);
                continue;
            }

            AddOrResetCascadeTarget(child, targets, maxCount);
        }
    }

    private static void AddOrResetCascadeTarget(UIElement element, List<UIElement> targets, int maxCount)
    {
        if (targets.Count < maxCount)
        {
            targets.Add(element);
        }
        else
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = ResolveElementRestingOpacity(element);
            element.Effect = null;
        }
    }

    #endregion
}
