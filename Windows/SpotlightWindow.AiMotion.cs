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
    private bool _aiActivityAnimating;
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

        // Header: Clean inline \u2726 glyph + Model Name (NO BADGE CONTAINER) + copy button
        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var brandStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var sparkText = new TextBlock
        {
            Text = "\u2726",
            FontFamily = sfPro,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = ChatIconBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        brandStack.Children.Add(sparkText);

        var modelTitle = new TextBlock
        {
            Text = string.IsNullOrEmpty(_aiConversationProvider) ? _settings.SpotlightAiProvider : _aiConversationProvider,
            FontFamily = sfPro,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
            VerticalAlignment = VerticalAlignment.Center
        };
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
                await Task.Delay(1800);
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
        var sfPro = (FontFamily)FindResource("SFProDisplay");
        var thinkingText = Loc.Get("spotlight.ai.thinking");

        var card = new Border
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 6, 0, 6),
            Margin = new Thickness(0, 2, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };

        // Shimmer brush â€” same technique as lyrics search shimmer
        var shimmerBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };
        var stop0 = new GradientStop(Color.FromArgb(110, 140, 149, 158), -0.45);
        var stop1 = new GradientStop(Color.FromArgb(240, 200, 200, 200), -0.20);
        var stop2 = new GradientStop(Color.FromArgb(110, 140, 149, 158), 0.05);
        shimmerBrush.GradientStops.Add(stop0);
        shimmerBrush.GradientStops.Add(stop1);
        shimmerBrush.GradientStops.Add(stop2);

        var sparkText = new TextBlock
        {
            Text = "\u2726",
            FontFamily = sfPro,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = ChatIconBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        };
        row.Children.Add(sparkText);

        var textBlock = new TextBlock
        {
            Text = thinkingText,
            FontFamily = sfPro,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = shimmerBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(textBlock);

        card.Child = row;

        _aiThinkingCard = card;
        AiTranscript.Children.Add(card);
        AnimateAiArrival(card, 6, 220);
        AiTranscriptScroll.ScrollToEnd();

        if (AnimationConfig.ReduceMotion) return;

        // Animate shimmer â€” sweep GradientStop offsets across the text
        var shimmerDuration = TimeSpan.FromMilliseconds(1600);

        void AnimateStop(GradientStop stop, double from, double to)
        {
            var anim = new DoubleAnimation(from, to, shimmerDuration)
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = null
            };
            Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
            stop.BeginAnimation(GradientStop.OffsetProperty, anim);
        }

        AnimateStop(stop0, -0.45, 1.05);
        AnimateStop(stop1, -0.20, 1.30);
        AnimateStop(stop2, 0.05, 1.55);
    }

    private void HideAiThinking()
    {
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
            FrameworkElement destination = AiHistoryPanel.Visibility == Visibility.Visible
                ? AiHistoryPanel
                : AiWelcome.Visibility == Visibility.Visible ? AiWelcome : AiTranscriptScroll;
            AnimateAiPageReveal(destination);
        }
        ScheduleContentResize();
        return Task.CompletedTask;
    }

    private static void AnimateAiPageReveal(FrameworkElement element)
    {
        bool interrupted = element.HasAnimatedProperties;
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
        bool interrupted = element.HasAnimatedProperties;
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
        if (_aiRequest == null || AnimationConfig.ReduceMotion || !IsSpotlightOpen || _isClosing)
        {
            StopAiActivity();
            return;
        }
        if (_aiActivityAnimating) return;
        _aiActivityAnimating = true;

        // Activity Halo expanding pulse
        AiActivityHalo.Opacity = 0.7;
        var haloScale = new DoubleAnimation(1.0, 1.8, TimeSpan.FromMilliseconds(950))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        var haloFade = new DoubleAnimation(0.7, 0.15, TimeSpan.FromMilliseconds(950))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(haloScale, AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(haloFade, AnimationConfig.TargetFps);
        AiActivityHaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, haloScale);
        AiActivityHaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, haloScale);
        AiActivityHalo.BeginAnimation(OpacityProperty, haloFade);

        // Dot color shift to clean white on active
        AiActivityDot.Fill = UiPalette.PrimaryBrush;
        AiActivityDot.Opacity = 1.0;
    }

    private void StopAiActivity()
    {
        _aiActivityAnimating = false;
        AiActivityHalo.BeginAnimation(OpacityProperty, null);
        AiActivityHaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        AiActivityHaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        AiActivityHalo.Opacity = 0;
        AiActivityDot.Fill = new SolidColorBrush(Color.FromRgb(119, 119, 119));
        AiActivityDot.Opacity = 0.45;
    }

    private void UpdateAiAmbientGlow(bool active)
    {
        if (AiGlowBorder == null) return;
        if (AnimationConfig.ReduceMotion)
        {
            AiGlowBorder.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            AiGlowBorder.Opacity = active ? 0.5 : 0;
            return;
        }

        if (active)
        {
            AiGlowBorder.Visibility = Visibility.Visible;
            var fadeIn = new DoubleAnimation(0, 0.5, TimeSpan.FromMilliseconds(380))
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
                if (!_aiMode)
                {
                    AiGlowBorder.Visibility = Visibility.Collapsed;
                    AiGlowEffect.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
                }
            };
            AiGlowBorder.BeginAnimation(OpacityProperty, fadeOut);
        }
    }


    private Geometry GetLookupIconGeometry() => AiMagnifierGeometry;

    private Geometry GetChatIconGeometry() => AiChatGeometry;

    private Geometry CreateIconGeometry(string glyph, string fallbackPath)
    {
        try
        {
            var fontFamily = (FontFamily?)TryFindResource("IconFont")
                ?? (FontFamily?)Application.Current?.TryFindResource("IconFont")
                ?? new FontFamily("Segoe MDL2 Assets");
            var text = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface(fontFamily, FontStyles.Normal,
                    FontWeights.Bold, FontStretches.Normal), 48, Brushes.White, 1.0);
            var built = text.BuildGeometry(new Point());
            if (built != null && !built.Bounds.IsEmpty && built.Bounds.Width > 0 && built.Bounds.Height > 0)
            {
                var group = new GeometryGroup { FillRule = FillRule.Nonzero };
                group.Children.Add(built);
                group.Freeze();
                return group;
            }
        }
        catch
        {
            // Fallback to vector path below
        }

        var fallback = Geometry.Parse(fallbackPath);
        var fallbackGroup = new GeometryGroup { FillRule = FillRule.Nonzero };
        fallbackGroup.Children.Add(fallback);
        fallbackGroup.Freeze();
        return fallbackGroup;
    }

    private void SyncSearchIconState(bool animate = false)
    {
        if (SearchIcon == null) return;
        var geom = _aiMode ? GetChatIconGeometry() : GetLookupIconGeometry();
        SearchIcon.Brush = _aiMode
            ? UiPalette.PrimaryBrush
            : UiPalette.IconBrush;
        SearchIcon.MorphTo(geom, animate: animate);
    }

    private void AnimateSearchIconToAi(bool toAi)
    {
        if (SearchIcon == null) return;

        var targetGeom = toAi ? GetChatIconGeometry() : GetLookupIconGeometry();
        var targetBrush = toAi
            ? UiPalette.PrimaryBrush
            : UiPalette.IconBrush;

        SearchIcon.Brush = targetBrush;

        if (AnimationConfig.ReduceMotion)
        {
            SearchIcon.MorphTo(targetGeom, animate: false);
            return;
        }

        // Fluid vector shape morphing between lookup and chat icons just like in Settings
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
