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

    private string _activeNav = NavSectionAppearance;
    private readonly Dictionary<string, StackPanel> _navPanels = new();
    private readonly Dictionary<string, Border> _navButtons = new();

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
            EnsureNavButtonTransform(btn);
            btn.MouseEnter -= NavButton_MouseEnter;
            btn.MouseEnter += NavButton_MouseEnter;
            btn.MouseLeave -= NavButton_MouseLeave;
            btn.MouseLeave += NavButton_MouseLeave;
            btn.PreviewMouseLeftButtonDown -= NavButton_PreviewMouseDown;
            btn.PreviewMouseLeftButtonDown += NavButton_PreviewMouseDown;
            btn.PreviewMouseLeftButtonUp -= NavButton_PreviewMouseUp;
            btn.PreviewMouseLeftButtonUp += NavButton_PreviewMouseUp;
            UpdateNavButtonVisual(btn, string.Equals(key, _activeNav, StringComparison.Ordinal), animate: false);
        }

        UpdateSectionHeader();
    }

    private static readonly SolidColorBrush _navHoverTextBrush = CreateFrozenNavBrush(0xFF, 0xEC, 0xF1, 0xF7);
    private static readonly SolidColorBrush _navActiveBorderBrush = CreateFrozenNavBrush(0x38, 0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush _navHoverBorderBrush = CreateFrozenNavBrush(0x22, 0xFF, 0xFF, 0xFF);

    private static SolidColorBrush CreateFrozenNavBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static (ScaleTransform Scale, TranslateTransform Translate) EnsureNavButtonTransform(Border btn)
    {
        btn.RenderTransformOrigin = new Point(0.5, 0.5);
        if (btn.RenderTransform is TransformGroup { IsFrozen: false } existing &&
            existing.Children.Count >= 2 &&
            existing.Children[0] is ScaleTransform s &&
            existing.Children[1] is TranslateTransform t)
        {
            return (s, t);
        }

        var scale = new ScaleTransform(1.0, 1.0);
        var translate = new TranslateTransform(0.0, 0.0);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(translate);
        btn.RenderTransform = group;
        return (scale, translate);
    }

    private void NavButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border btn || btn.Tag is not string section) return;
        bool isActive = string.Equals(section, _activeNav, StringComparison.Ordinal);
        var (scale, translate) = EnsureNavButtonTransform(btn);
        if (!isActive)
        {
            btn.BorderBrush = _navHoverBorderBrush;
            if (btn.Child is StackPanel stack && stack.Children.Count >= 2)
            {
                ApplyNavIconBrush(stack.Children[0], _navHoverTextBrush);
                if (stack.Children[1] is TextBlock text) text.Foreground = _navHoverTextBrush;
            }
        }
        AnimateNavTransform(translate, TranslateTransform.XProperty, isActive ? 0.0 : 2.0, 140);
        AnimateNavTransform(scale, ScaleTransform.ScaleXProperty, 1.012, 140);
        AnimateNavTransform(scale, ScaleTransform.ScaleYProperty, 1.012, 140);
    }

    private void NavButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border btn || btn.Tag is not string section) return;
        bool isActive = string.Equals(section, _activeNav, StringComparison.Ordinal);
        UpdateNavButtonVisual(btn, isActive, animate: true);
    }

    private void NavButton_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border btn) return;
        var (scale, _) = EnsureNavButtonTransform(btn);
        AnimateNavTransform(scale, ScaleTransform.ScaleXProperty, 0.968, 85);
        AnimateNavTransform(scale, ScaleTransform.ScaleYProperty, 0.968, 85);
    }

    private void NavButton_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border btn) return;
        var (scale, _) = EnsureNavButtonTransform(btn);
        AnimateNavTransform(scale, ScaleTransform.ScaleXProperty, 1.0, 160);
        AnimateNavTransform(scale, ScaleTransform.ScaleYProperty, 1.0, 160);
    }

    private static void AnimateNavTransform(Animatable target, DependencyProperty property, double to, int durationMs)
    {
        if (durationMs <= 0 || AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation)
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, to);
            return;
        }

        double current = (double)target.GetValue(property);
        if (Math.Abs(current - to) < 0.001)
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, to);
            return;
        }

        var anim = AnimationPrimitives.WithFps(new DoubleAnimation(current, to, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = AnimationPrimitives._easeAppleOut,
            FillBehavior = FillBehavior.Stop
        });
        target.BeginAnimation(property, anim, HandoffBehavior.SnapshotAndReplace);
        target.SetValue(property, to);
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
        var (scale, translate) = EnsureNavButtonTransform(btn);
        if (isActive)
        {
            btn.Background = (SolidColorBrush)FindResource("NavItemActiveBg");
            btn.BorderBrush = _navActiveBorderBrush;
        }
        else
        {
            btn.ClearValue(Border.BackgroundProperty);
            btn.BorderBrush = _transparentBrush;
        }

        AnimateNavTransform(translate, TranslateTransform.XProperty, 0.0, animate ? 170 : 0);
        AnimateNavTransform(scale, ScaleTransform.ScaleXProperty, 1.0, animate ? 170 : 0);
        AnimateNavTransform(scale, ScaleTransform.ScaleYProperty, 1.0, animate ? 170 : 0);

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

        if (_navButtons.TryGetValue(previous, out var oldBtn))
        {
            UpdateNavButtonVisual(oldBtn, false);
        }

        _activeNav = section;
        UpdateSectionHeader();

        if (_navButtons.TryGetValue(section, out var newBtn))
        {
            UpdateNavButtonVisual(newBtn, true);
        }

        int version = ++_navTransitionVersion;
        int direction = Math.Sign(Array.IndexOf(_navOrder, section) - Array.IndexOf(_navOrder, previous));
        if (direction == 0) direction = 1;

        var (oldCard, oldTranslate) = GetSectionCardParts(previous);
        _navPanels.TryGetValue(previous, out var oldPanel);

        void RevealIncoming()
        {
            if (version != _navTransitionVersion ||
                !string.Equals(_activeNav, section, StringComparison.Ordinal))
                return;

            foreach (var kvp in _navPanels)
                if (kvp.Key != section) kvp.Value.Visibility = Visibility.Collapsed;
            if (_navPanels.TryGetValue(section, out var newPanel))
                newPanel.Visibility = Visibility.Visible;

            StopSmoothScroll();
            SettingsScrollViewer.ScrollToTop();
            AnimateActivePanel(section, direction);
        }

        if (oldCard == null || oldTranslate == null || VNotch.Services.AnimationConfig.ReduceMotion)
        {
            RevealIncoming();
            return;
        }

        AnimateSectionExit(oldCard, oldTranslate, oldPanel, previous, direction, version, RevealIncoming);
    }

    private static readonly CubicEase SectionExitEase = CreateFrozenSectionExitEase();

    private static CubicEase CreateFrozenSectionExitEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        ease.Freeze();
        return ease;
    }

    private void AnimateSectionExit(
        FrameworkElement oldCard,
        TranslateTransform oldTranslate,
        UIElement? oldPanel,
        string previous,
        int direction,
        int version,
        Action onExitCompleted)
    {
        ResetProgressiveBlurElements(isMenuList: false);

        int fps = VNotch.Services.AnimationConfig.TargetFps;
        var exitEase = SectionExitEase;
        var exitDur = TimeSpan.FromMilliseconds(170);

        var fadeOut = new DoubleAnimation(oldCard.Opacity, 0, exitDur) { EasingFunction = exitEase };
        var slideOut = new DoubleAnimation(oldTranslate.Y, -14 * direction, exitDur) { EasingFunction = exitEase };
        Timeline.SetDesiredFrameRate(fadeOut, fps);
        Timeline.SetDesiredFrameRate(slideOut, fps);

        var exitBlur = new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        oldCard.Effect = exitBlur;
        var blurOut = new DoubleAnimation(0, 8, exitDur) { EasingFunction = exitEase };
        Timeline.SetDesiredFrameRate(blurOut, fps);

        fadeOut.Completed += (_, _) =>
        {
            if (ReferenceEquals(oldCard.Effect, exitBlur))
            {
                oldCard.Effect = null;
            }
            if (!string.Equals(previous, _activeNav, StringComparison.Ordinal))
            {
                oldCard.BeginAnimation(OpacityProperty, null);
                oldCard.Opacity = 0;
                oldTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                oldTranslate.Y = 12;
                if (oldPanel != null)
                    oldPanel.Visibility = Visibility.Collapsed;
            }
            if (version == _navTransitionVersion)
                onExitCompleted();
        };

        oldCard.BeginAnimation(OpacityProperty, fadeOut);
        oldTranslate.BeginAnimation(TranslateTransform.YProperty, slideOut);
        exitBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOut);
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

    private static ScaleTransform EnsureCardScale(FrameworkElement card, TranslateTransform translate)
    {
        if (card.RenderTransform is TransformGroup existing &&
            existing.Children.Count > 0 && existing.Children[0] is ScaleTransform s)
            return s;

        var scale = new ScaleTransform(1, 1);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(translate);
        card.RenderTransform = group;
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        return scale;
    }

    private void AnimateActivePanel(string section, int direction = 1, bool entranceDirection = false)
    {
        var (card, translate) = GetSectionCardParts(section);
        if (card == null || translate == null) return;

        if (VNotch.Services.AnimationConfig.ReduceMotion)
        {
            ResetProgressiveBlurElements(isMenuList: false);
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

        int fps = entranceDirection
            ? Math.Min(60, VNotch.Services.AnimationConfig.TargetFps)
            : VNotch.Services.AnimationConfig.TargetFps;
        var ease = EntranceEaseOut;

        // entranceDirection=true → slide from above (-14px) to match top-down open cascade
        // Normal nav switching uses ±direction * 14
        int cascadeDir = entranceDirection ? -1 : (direction == 0 ? 1 : direction);
        double fromY = entranceDirection ? -14 : 14 * cascadeDir;
        int baseDelayMs = entranceDirection ? 200 : 0;

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
