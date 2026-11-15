using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.Spotlight;

namespace VNotch;

public partial class SpotlightWindow
{
    private VNotch.Controls.ThinkingOrb? _aiThinkingOrb;
    private Border? _aiThinkingCard;

    private RichTextBox? AddAiMessage(SpotlightAiMessage message, bool animate)
    {
        var sfPro = (FontFamily)FindResource("SFProDisplay");
        bool user = message.Role == "user";
        if (user)
        {
            var userContainer = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(40, 4, 0, 10),
                MaxWidth = 540
            };

            var userStack = new StackPanel();

            var userLabel = new TextBlock
            {
                Text = Loc.Get("spotlight.ai.you"),
                FontFamily = sfPro,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(140, 149, 158)),
                Margin = new Thickness(0, 0, 0, 3),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            userStack.Children.Add(userLabel);

            var textBlock = new TextBlock
            {
                Text = message.Content,
                FontFamily = sfPro,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                TextAlignment = TextAlignment.Right
            };
            userStack.Children.Add(textBlock);

            userContainer.Children.Add(userStack);
            AiTranscript.Children.Add(userContainer);
            if (animate) AnimateAiArrival(userContainer, 8, 260);
            return null;
        }

        // Assistant Message - Flat Surface (NO FLOAT CARD DESIGN)
        var view = CreateMarkdownView(message.Content);
        view.FontFamily = sfPro;
        view.FontWeight = FontWeights.Bold;

        var card = new Border
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 4, 0, 8),
            Margin = new Thickness(0, 6, 0, 16),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var stack = new StackPanel();

        // Keep the selected brand and model visible beside each assistant response.
        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var (provider, model) = CurrentAiIdentity();
        var brandStack = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        brandStack.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        brandStack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        brandStack.Children.Add(CreateAiBrandImage(provider, 14));

        var modelTitle = new TextBlock
        {
            Text = SpotlightAiModelName.Format(model, provider),
            ToolTip = provider + " · " + model,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = sfPro,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(modelTitle, 1);
        brandStack.Children.Add(modelTitle);
        headerGrid.Children.Add(brandStack);

        // Per-message copy button
        var msgCopyBtn = new Button
        {
            Style = (Style)FindResource("AiToolbarStyle"),
            Width = 24,
            Height = 24,
            ToolTip = Loc.Get("spotlight.ai.copy"),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(msgCopyBtn, 2);

        var copyPath = new System.Windows.Shapes.Path
        {
            Data = AiCopyGeometry,
            Fill = ChatIconBrush,
            Stretch = Stretch.Uniform,
            Width = 10,
            Height = 10
        };
        msgCopyBtn.Content = copyPath;

        msgCopyBtn.Click += async (_, _) =>
        {
            if (TryCopyToClipboard(message.Content))
            {
                msgCopyBtn.Content = new TextBlock
                {
                    Text = "\u2713",
                    FontFamily = sfPro,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = UiPalette.PrimaryBrush
                };
                if (!AnimationConfig.ReduceMotion)
                {
                    var pop = new DoubleAnimation(1.3, 1.0, TimeSpan.FromMilliseconds(180))
                    {
                        EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
                    };
                    msgCopyBtn.RenderTransform = new ScaleTransform();
                    msgCopyBtn.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                    msgCopyBtn.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
                }
                await Task.Delay(1800, CancellationToken.None);
                msgCopyBtn.Content = copyPath;
            }
        };

        headerGrid.Children.Add(msgCopyBtn);
        stack.Children.Add(headerGrid);
        stack.Children.Add(view);
        card.Child = stack;

        AiTranscript.Children.Add(card);
        if (animate) AnimateAiArrival(card, 8, 300);
        return view;
    }

    private void ShowAiThinking()
    {
        if (_aiThinkingCard != null) return;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _aiThinkingOrb = new VNotch.Controls.ThinkingOrb
        {
            Width = 28,
            Height = 28,
            State = VNotch.Controls.ThinkingOrbState.Working,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(_aiThinkingOrb);
        row.Children.Add(new TextBlock
        {
            Text = Loc.Get("spotlight.ai.thinking"),
            FontFamily = (FontFamily)FindResource("SFProDisplay"),
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(168, 172, 182)),
            VerticalAlignment = VerticalAlignment.Center
        });
        _aiThinkingCard = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 4, 0, 4),
            Margin = new Thickness(0, 2, 0, 8),
            Child = row
        };
        AiTranscript.Children.Add(_aiThinkingCard);
        AnimateAiArrival(_aiThinkingCard, 6, 220);
        AiTranscriptScroll.ScrollToEnd();
    }

    private void HideAiThinking()
    {
        if (_aiThinkingOrb != null) _aiThinkingOrb.IsActive = false;
        _aiThinkingOrb = null;
        if (_aiThinkingCard == null) return;
        AiTranscript.Children.Remove(_aiThinkingCard);
        _aiThinkingCard = null;
    }

    private Task TransitionAiPageAsync(Action changePage)
    {
        if (!IsSpotlightOpen || _isClosing) return Task.CompletedTask;
        // Pin the presented height before Visibility changes invalidate layout.
        // Capture the animated value so rapid navigation continues from this frame.
        bool holdHeight = _contentShown && !_entranceActive && !AnimationConfig.ReduceMotion;
        double presentedHeight = ContentRegion.Height;
        if (!double.IsFinite(presentedHeight)) presentedHeight = ContentRegion.ActualHeight;
        if (holdHeight)
        {
            ++_contentHeightAnimationVersion;
            ContentRegion.BeginAnimation(HeightProperty, null);
            ContentRegion.Height = presentedHeight;
            ContentRegion.ClipToBounds = true;
            _contentHeightTarget = double.NaN;
        }
        // Commit navigation immediately; animate only the destination content.
        // Keeping the header stationary avoids the whole window blinking between pages.
        changePage();
        if (_aiMode)
        {
            if (holdHeight)
            {
                var inset = ContentRegion.BorderThickness;
                var padding = ContentRegion.Padding;
                double width = Math.Max(1, ContentRegion.ActualWidth - inset.Left - inset.Right - padding.Left - padding.Right);
                ContentRegion.Child.Measure(new Size(width, double.PositiveInfinity));
                double target = ContentRegion.Child.DesiredSize.Height + inset.Top + inset.Bottom + padding.Top + padding.Bottom;
                if (double.IsFinite(target))
                    BeginContentHeightAnimation(presentedHeight, target, _contentSizeGeneration);
                else
                {
                    ContentRegion.Height = double.NaN;
                    ContentRegion.ClipToBounds = false;
                }
            }
            FrameworkElement mainDestination = AiWelcome.Visibility == Visibility.Visible ? AiWelcome : AiTranscriptScroll;
            FrameworkElement destination = AiHistoryPanel.Visibility == Visibility.Visible ? AiHistoryPanel : mainDestination;
            if (ReferenceEquals(destination, AiWelcome)) AnimateAiFade(destination);
            else AnimateAiPageReveal(destination);
        }
        ScheduleContentResize();
        return Task.CompletedTask;
    }

    private static void AnimateAiFade(FrameworkElement element)
    {
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = 1;
        element.RenderTransform = Transform.Identity;
        if (AnimationConfig.ReduceMotion) return;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
        { FillBehavior = FillBehavior.Stop };
        Timeline.SetDesiredFrameRate(fade, AnimationConfig.TargetFps);
        element.BeginAnimation(OpacityProperty, fade);
    }

    private static void AnimateAiPageReveal(FrameworkElement element)
    {
        // A stopped FillBehavior.Stop clock can remain attached after completion.
        // Only continue an in-flight fade; completed reveals must start anew.
        bool interrupted = element.HasAnimatedProperties && element.Opacity < 1;
        double opacity = interrupted ? element.Opacity : 0;
        var previous = element.RenderTransform as TransformGroup;
        var oldScale = previous?.Children.OfType<ScaleTransform>().FirstOrDefault();
        var oldTranslate = previous?.Children.OfType<TranslateTransform>().FirstOrDefault();
        double fromScale = interrupted && oldScale != null ? oldScale.ScaleY : 0.985;
        double fromY = interrupted && oldTranslate != null ? oldTranslate.Y : 4;

        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = 1;
        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform();
        var transforms = new TransformGroup();
        transforms.Children.Add(scale);
        transforms.Children.Add(translate);
        element.RenderTransformOrigin = new Point(0.5, 0);
        element.RenderTransform = transforms;

        var duration = TimeSpan.FromMilliseconds(AnimationConfig.ReduceMotion ? 160 : 280);
        var ease = new CubicBezierEase(0.23, 1, 0.32, 1) { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(opacity, 1, duration) { FillBehavior = FillBehavior.Stop };
        Timeline.SetDesiredFrameRate(fade, AnimationConfig.TargetFps);
        element.BeginAnimation(OpacityProperty, fade);
        if (AnimationConfig.ReduceMotion) return;

        var morph = new DoubleAnimation(fromScale, 1, duration)
        { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        var slide = new DoubleAnimation(fromY, 0, duration)
        { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        Timeline.SetDesiredFrameRate(morph, AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(slide, AnimationConfig.TargetFps);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, morph);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private static void AnimateAiArrival(FrameworkElement element, double distance, int milliseconds)
    {
        // A stopped FillBehavior.Stop clock can remain attached after completion.
        // Only continue an in-flight fade; completed reveals must start anew.
        bool interrupted = element.HasAnimatedProperties && element.Opacity < 1;
        double opacity = interrupted ? element.Opacity : 0.65;
        var translate = element.RenderTransform as TranslateTransform;
        double from = interrupted && translate != null ? translate.Y : Math.Min(distance, 6);
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = 1;
        translate ??= new TranslateTransform();
        element.RenderTransform = translate;
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        translate.Y = 0;
        if (AnimationConfig.ReduceMotion) return;

        var ease = new CubicBezierEase(0.23, 1, 0.32, 1) { EasingMode = EasingMode.EaseIn };
        var duration = TimeSpan.FromMilliseconds(Math.Min(milliseconds, 280));
        var movement = new DoubleAnimation(from, 0, duration)
        { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        var fade = new DoubleAnimation(opacity, 1, duration)
        { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        Timeline.SetDesiredFrameRate(movement, AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(fade, AnimationConfig.TargetFps);
        translate.BeginAnimation(TranslateTransform.YProperty, movement);
        element.BeginAnimation(OpacityProperty, fade);
    }

    private void UpdateAiActivity()
    {
        if (_aiRequest == null || !_aiMode || !IsSpotlightOpen || _isClosing)
        {
            StopAiActivity();
            return;
        }
        // One orb at a time: working beside the thinking label, then composing
        // beside the streaming status until the last word has been revealed.
        if (_aiThinkingOrb != null) _aiThinkingOrb.IsActive = true;
        bool composing = _aiThinkingCard == null;
        AiActivityOrb.Visibility = composing ? Visibility.Visible : Visibility.Collapsed;
        AiActivityOrb.IsActive = composing;
    }

    private void StopAiActivity()
    {
        if (_aiThinkingOrb != null) _aiThinkingOrb.IsActive = false;
        AiActivityOrb.IsActive = false;
        AiActivityOrb.Visibility = Visibility.Collapsed;
    }

    private int _aiGlowVersion;

    private void UpdateAiAmbientGlow(bool active)
    {
        if (AiGlowBorder == null) return;
        int version = ++_aiGlowVersion;
        if (AnimationConfig.ReduceMotion)
        {
            AiGlowBorder.BeginAnimation(OpacityProperty, null);
            AiGlowEffect.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
            AiGlowBorder.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            AiGlowBorder.Opacity = active ? 0.5 : 0;
            return;
        }

        if (active)
        {
            AiGlowBorder.Visibility = Visibility.Visible;
            var fadeIn = new DoubleAnimation(AiGlowBorder.Opacity, 0.5, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
            };
            Timeline.SetDesiredFrameRate(fadeIn, AnimationConfig.TargetFps);
            AiGlowBorder.BeginAnimation(OpacityProperty, fadeIn);

            // Gentle breathing glow on shadow effect
            var breathe = new DoubleAnimation(12, 22, TimeSpan.FromMilliseconds(1800))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Timeline.SetDesiredFrameRate(breathe, AnimationConfig.TargetFps);
            AiGlowEffect.BeginAnimation(DropShadowEffect.BlurRadiusProperty, breathe);
        }
        else
        {
            var fadeOut = new DoubleAnimation(AiGlowBorder.Opacity, 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseIn }
            };
            Timeline.SetDesiredFrameRate(fadeOut, AnimationConfig.TargetFps);
            fadeOut.Completed += (_, _) =>
            {
                if (version != _aiGlowVersion || _aiMode) return;
                AiGlowBorder.BeginAnimation(OpacityProperty, null);
                AiGlowBorder.Opacity = 0;
                AiGlowBorder.Visibility = Visibility.Collapsed;
                AiGlowEffect.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
            };
            AiGlowBorder.BeginAnimation(OpacityProperty, fadeOut);
        }
    }


    private static Geometry GetLookupIconGeometry() => AiMagnifierGeometry;

    private Geometry GetChatIconGeometry() => VNotch.Controls.AiProviderBrand.GeometryFor(CurrentAiIdentity().Provider);

    private void SyncSearchIconState(bool animate = false)
    {
        if (SearchIcon == null) return;
        var geom = _aiMode ? GetChatIconGeometry() : GetLookupIconGeometry();
        SearchIcon.Brush = UiPalette.PrimaryBrush;
        SearchIcon.MorphTo(geom, animate: animate);
    }

    private void AnimateSearchIconToAi(bool toAi)
    {
        if (SearchIcon == null) return;

        var targetGeom = toAi ? GetChatIconGeometry() : GetLookupIconGeometry();
        SearchIcon.Brush = UiPalette.PrimaryBrush;

        if (AnimationConfig.ReduceMotion)
        {
            SearchIcon.MorphTo(targetGeom, animate: false);
            return;
        }

        // Morph between the lookup icon and the selected provider's brand mark.
        SearchIcon.MorphTo(targetGeom, animate: true, duration: TimeSpan.FromMilliseconds(380),
            easing: new CubicEase { EasingMode = EasingMode.EaseOut });

        // Subtle tactile elastic pop during the morph transition
        var scale = new DoubleAnimationUsingKeyFrames();
        scale.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        scale.KeyFrames.Add(new EasingDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(75)), new CubicEase { EasingMode = EasingMode.EaseIn }));
        scale.KeyFrames.Add(new EasingDoubleKeyFrame(1.14, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(210)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        scale.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380)), new QuarticEase { EasingMode = EasingMode.EaseOut }));

        Timeline.SetDesiredFrameRate(scale, AnimationConfig.TargetFps);
        SearchIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        SearchIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }
}
