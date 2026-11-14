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

    private string _activeNav = NavSectionAppearance;
    private string? _hoveredNav;
    private int _lastNavDirection = 1;
    private readonly Dictionary<string, StackPanel> _navPanels = new();
    private readonly Dictionary<string, Border> _navButtons = new();

    private static readonly SolidColorBrush _navHoverTextBrush = CreateFrozenNavBrush(0xFF, 0xD8, 0xDC, 0xE4);
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
        IsVisibleChanged -= NavPillVisibilityChanged;
        IsVisibleChanged += NavPillVisibilityChanged;
        IsVisibleChanged -= NavHoverVisibilityChanged;
        IsVisibleChanged += NavHoverVisibilityChanged;
        _navPanels[NavSectionSearching] = PanelSearching;
        _navPanels[NavSectionAppearance] = PanelAppearance;
        _navPanels[NavSectionSkins] = PanelSkins;
        _navPanels[NavSectionBehavior] = PanelBehavior;
        _navPanels[NavSectionDevices] = PanelDevices;
        _navPanels[NavSectionSystem] = PanelSystem;
        _navPanels[NavSectionPrivacy] = PanelPrivacy;
        _navPanels[NavSectionSpotlight] = PanelSpotlight;
        _navPanels[NavSectionAiUsage] = PanelAiUsage;
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
        _navButtons[NavSectionAiUsage] = NavAiUsage;
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
                // Entrance/hover transforms can still be moving a row. The
                // shared background must land on its final layout slot.
                Rect slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(btn);
                double y = slot.Y + btn.Margin.Top;
                if (!slot.IsEmpty && double.IsFinite(y))
                {
                    return (Math.Max(0.0, y), height);
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
        if (NavActiveIndicator == null || NavActiveIndicatorTranslate == null) return;
        animate &= IsLoaded && !AnimationConfig.ReduceMotion && SystemParameters.ClientAreaAnimation;
        var (targetY, targetHeight) = ResolveNavButtonSlot(targetBtn);
        StartNavPillMotion(targetY, targetHeight, animate);
    }
    private void AnimateNavHoverIndicator(Border? hoveredBtn)
    {
        if (NavHoverIndicator == null || NavHoverIndicatorTranslate == null)
            return;

        bool animate = IsVisible && !AnimationConfig.ReduceMotion && SystemParameters.ClientAreaAnimation;
        bool activeHovered = hoveredBtn?.Tag is string activeTag && activeTag == _activeNav;
        double targetY = _navHoverTargetY;
        if (hoveredBtn != null)
        {
            var slot = ResolveNavButtonSlot(hoveredBtn);
            targetY = slot.Y;
            NavHoverIndicator.Height = slot.Height;
        }
        StartNavHoverMotion(targetY, hoveredBtn != null && !activeHovered, activeHovered, animate);
    }

    private void NavItemsHost_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_hoveredNav != null && _navButtons.TryGetValue(_hoveredNav, out var previous))
        {
            _hoveredNav = null;
            UpdateNavButtonVisual(previous, previous.Tag as string == _activeNav);
        }
        _hoveredNav = null;
        AnimateNavHoverIndicator(null);
    }

    private void NavButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border btn || btn.Tag is not string section) return;
        if (_isSearchMode && section != NavSectionSearching) return;

        if (_hoveredNav != section && _hoveredNav != null && _navButtons.TryGetValue(_hoveredNav, out var previous))
        {
            _hoveredNav = null;
            UpdateNavButtonVisual(previous, previous.Tag as string == _activeNav);
        }
        _hoveredNav = section;
        UpdateNavButtonVisual(btn, string.Equals(section, _activeNav, StringComparison.Ordinal));
        AnimateNavHoverIndicator(btn);
    }

    // Row margins are part of the same hover interaction. Only leaving the
    // whole host fades it out; entering another row simply changes its target.
    private void RefreshNavHoverIndicator() => AnimateNavHoverIndicator(
        _hoveredNav != null && _navButtons.TryGetValue(_hoveredNav, out var hovered) ? hovered : null);

    private void NavHoverVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) return;
        _hoveredNav = null;
        foreach (var (section, button) in _navButtons)
            UpdateNavButtonVisual(button, section == _activeNav, animate: false);
        AnimateNavHoverIndicator(null);
    }

    private void Nav_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is string section)
        {
            if (_isSearchMode && section != NavSectionSearching)
            {
                return;
            }

            e.Handled = true;
            NavigateToSection(section);
        }
    }

    private static readonly string[] _navOrder =
    {
        NavSectionSearching, NavSectionAppearance, NavSectionSkins, NavSectionBehavior, NavSectionDevices,
        NavSectionSystem, NavSectionPrivacy, NavSectionSpotlight, NavSectionAiUsage, "Translation", NavSectionFileTray, NavSectionAdvanced, NavSectionPerformance, NavSectionDonating, NavSectionUpdates
    };

    private int _navTransitionVersion;

    private void UpdateNavButtonVisual(Border btn, bool isActive, bool animate = true)
    {
        btn.Background = _transparentBrush;
        btn.BorderBrush = _transparentBrush;

        if (btn.Child is not StackPanel stack || stack.Children.Count < 2) return;

        var targetBrush = isActive ? (Brush)FindResource("AccentForegroundBrush")
            : btn.Tag as string == _hoveredNav ? _navHoverTextBrush : _navInactiveBrush;
        if (!isActive && stack.Children[1] is TextBlock label && label.Foreground is SolidColorBrush from && targetBrush is SolidColorBrush to &&
            animate && IsVisible && !AnimationConfig.ReduceMotion && SystemParameters.ClientAreaAnimation && from.Color != to.Color)
        {
            // Give each fading row its own ink; shared resource brushes can be frozen.
            var ink = new SolidColorBrush(to.Color);
            var fade = AnimationPrimitives.WithFps(new ColorAnimation(from.Color, to.Color, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = _navEaseOut,
                FillBehavior = FillBehavior.Stop
            });
            var clock = fade.CreateClock();
            clock.Completed += (_, _) => ink.ApplyAnimationClock(SolidColorBrush.ColorProperty, null);
            ink.ApplyAnimationClock(SolidColorBrush.ColorProperty, clock);
            clock.Controller!.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime);
            targetBrush = ink;
        }
        ApplyNavIconBrush(stack.Children[0], targetBrush, useAccent: isActive);

        if (stack.Children[1] is TextBlock text)
        {
            if (isActive) text.SetResourceReference(TextBlock.ForegroundProperty, "AccentForegroundBrush");
            else text.Foreground = targetBrush;
        }
    }

    private static void ApplyNavIconBrush(UIElement element, Brush targetBrush, bool useAccent = false)
    {
        if (element is Viewbox vb)
        {
            if (vb.Child is System.Windows.Shapes.Path path)
            {
                if (useAccent) path.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentForegroundBrush");
                else path.Fill = targetBrush;
            }
            else if (vb.Child is Canvas canvas)
            {
                foreach (var child in canvas.Children)
                {
                    if (child is System.Windows.Shapes.Path cp)
                    {
                        if (useAccent) cp.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentForegroundBrush");
                        else cp.Fill = targetBrush;
                    }
                }
            }
        }
        else if (element is TextBlock iconText)
        {
            if (useAccent) iconText.SetResourceReference(TextBlock.ForegroundProperty, "AccentForegroundBrush");
            else iconText.Foreground = targetBrush;
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
            RefreshNavHoverIndicator();
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
            NavSectionAiUsage => AiUsageCard,
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
            NavSectionAiUsage => AiUsageCardTranslate,
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
            RevealSettingsRows(card, section == NavSectionSystem ? BackupCard : null);
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

    // Page changes and newly opened settings groups share the window's label cascade.
    private void RevealSettingsRows(FrameworkElement content, FrameworkElement? additionalContent = null)
    {
        if (!IsLoaded || !content.IsVisible) return;
        if (!SystemParameters.ClientAreaAnimation || AnimationConfig.ReduceMotion)
        {
            ResetProgressiveBlurElements(isMenuList: false);
            return;
        }
        var targets = content is Panel ? new List<UIElement>() : CollectPanelProgressiveBlurTargets(content);
        if (content is Panel children) CollectPanelChildrenRecursive(children, targets, 24);
        if (additionalContent?.IsVisible == true)
            targets.AddRange(CollectPanelProgressiveBlurTargets(additionalContent));
        ApplyProgressiveBlurCascade(targets, 0, -1, isMenuList: false, Math.Min(60, AnimationConfig.TargetFps));
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

            // Keep connectors and their rows in the same coordinate system during motion.
            if (child is SettingsTreePanel tree)
            {
                AddOrResetCascadeTarget(tree, targets, maxCount);
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
