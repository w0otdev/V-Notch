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
        _navButtons[NavSectionAdvanced] = NavAdvanced;
        _navButtons[NavSectionPerformance] = NavPerformance;
        _navButtons[NavSectionDonating] = NavDonating;
        _navButtons[NavSectionUpdates] = NavUpdates;
        UpdateSectionHeader();
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
        NavSectionSystem, NavSectionPrivacy, NavSectionSpotlight, NavSectionAdvanced, NavSectionPerformance, NavSectionDonating, NavSectionUpdates
    };

    private int _navTransitionVersion;

    private void UpdateNavButtonVisual(Border btn, bool isActive)
    {
        btn.Background = isActive ? (SolidColorBrush)FindResource("NavItemActiveBg") : _transparentBrush;
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

    private void NavigateToSection(string section)
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

            SettingsScrollViewer.ScrollToTop();
            AnimateActivePanel(section, direction);
        }

        if (oldCard == null || oldTranslate == null || VNotch.Services.AnimationConfig.ReduceMotion)
        {
            RevealIncoming();
            return;
        }

        AnimateSectionExit(oldCard, oldTranslate, oldPanel, previous, direction, RevealIncoming);
    }

    private void AnimateSectionExit(
        FrameworkElement oldCard,
        TranslateTransform oldTranslate,
        UIElement? oldPanel,
        string previous,
        int direction,
        Action onExitCompleted)
    {
        int fps = VNotch.Services.AnimationConfig.TargetFps;
        var exitEase = new CubicEase { EasingMode = EasingMode.EaseIn };
        var exitDur = TimeSpan.FromMilliseconds(130);

        var fadeOut = new DoubleAnimation(oldCard.Opacity, 0, exitDur) { EasingFunction = exitEase };
        var slideOut = new DoubleAnimation(oldTranslate.Y, -14 * direction, exitDur) { EasingFunction = exitEase };
        Timeline.SetDesiredFrameRate(fadeOut, fps);
        Timeline.SetDesiredFrameRate(slideOut, fps);

        fadeOut.Completed += (_, _) =>
        {
            oldCard.BeginAnimation(OpacityProperty, null);
            oldCard.Opacity = 0;
            oldTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            oldTranslate.Y = 12;
            if (oldPanel != null && !string.Equals(previous, _activeNav, StringComparison.Ordinal))
                oldPanel.Visibility = Visibility.Collapsed;
            onExitCompleted();
        };

        oldCard.BeginAnimation(OpacityProperty, fadeOut);
        oldTranslate.BeginAnimation(TranslateTransform.YProperty, slideOut);
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
            card.BeginAnimation(OpacityProperty, null);
            card.Opacity = 1;
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.Y = 0;
            return;
        }

        int fps = VNotch.Services.AnimationConfig.TargetFps;
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };

        // entranceDirection=true → slide from above (-14px) to match top-down open cascade
        // Normal nav switching uses ±direction * 14
        double fromY = entranceDirection ? -14 : 14 * direction;
        card.Opacity = 0;
        translate.Y = fromY;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease };
        var slide = new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(fade, fps);
        Timeline.SetDesiredFrameRate(slide, fps);

        card.BeginAnimation(OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);

        if (section == NavSectionSystem && BackupCard != null && BackupCardTranslate != null)
        {
            if (VNotch.Services.AnimationConfig.ReduceMotion)
            {
                BackupCard.BeginAnimation(OpacityProperty, null);
                BackupCard.Opacity = 1;
                BackupCardTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                BackupCardTranslate.Y = 0;
            }
            else
            {
                BackupCard.Opacity = 0;
                BackupCardTranslate.Y = fromY;

                var backupFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(40) };
                var backupSlide = new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(40) };

                Timeline.SetDesiredFrameRate(backupFade, fps);
                Timeline.SetDesiredFrameRate(backupSlide, fps);

                BackupCard.BeginAnimation(OpacityProperty, backupFade);
                BackupCardTranslate.BeginAnimation(TranslateTransform.YProperty, backupSlide);
            }
        }
    }

    #endregion
}
