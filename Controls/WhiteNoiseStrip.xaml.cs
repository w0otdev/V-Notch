using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using VNotch.Services;

namespace VNotch.Controls;

public partial class WhiteNoiseStrip : UserControl
{
    private static readonly Color ChipIdleColor = Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF);
    private static readonly Color ChipActiveSurfaceColor = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);
    private static readonly Color TextIdleColor = Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF);
    private static readonly Color ToggleOffColor = Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush SelectedHoverBrush = CreateFrozenBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));

    private sealed record IconSpec(string Data, double Width, double Height, Stretch Stretch);

    private static readonly IconSpec SpeakerIcon = new(
        "M3,9 H7 L12,5 V19 L7,15 H3 Z M14.2,8.2 A5,5 0 0 1 14.2,15.8 L13,14.4 A3.2,3.2 0 0 0 13,9.6 Z",
        15, 15, Stretch.Uniform);
    private static readonly IconSpec MutedIcon = new(
        "M3,9 H7 L12,5 V19 L7,15 H3 Z M14,9.4 L15.4,8 L17.5,10.1 L19.6,8 L21,9.4 L18.9,11.5 L21,13.6 L19.6,15 L17.5,12.9 L15.4,15 L14,13.6 L16.1,11.5 Z",
        15, 15, Stretch.Uniform);
    private static readonly IEasingFunction Ease = CreateFrozenCubicOut();

    private sealed class Chip(Grid root, Border surface, Border hover, TextBlock label, SolidColorBrush fill, ScaleTransform contentScale)
    {
        public Grid Root { get; } = root;
        public Border Surface { get; } = surface;
        public Border Hover { get; } = hover;
        public TextBlock Label { get; } = label;
        public SolidColorBrush Fill { get; } = fill;
        public ScaleTransform ContentScale { get; } = contentScale;
        public bool IsPressed { get; set; }
    }

    private readonly Dictionary<int, Chip> _chips = [];
    private Rect _chipSelectionBounds = Rect.Empty;
    private int? _animatedPlayingKind;
    private bool _selectionVisible;
    private bool _wasTrackVisible;
    private Border _muteButton = null!;
    private Path _mutePath = null!;
    private Border _toggleTrack = null!;
    private SolidColorBrush _toggleBrush = null!;
    private TranslateTransform _toggleKnob = null!;
    private bool _draggingVolume;
    private bool _firstRefresh = true;

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static CubicEase CreateFrozenCubicOut()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    public WhiteNoiseStrip()
    {
        InitializeComponent();
        foreach (var sound in WhiteNoiseService.Catalog) AddChip(sound);
        BuildControls();
        VolumeBar.ToolTip = Loc.Get("whitenoise.volume");
        VolumeBar.SizeChanged += (_, _) => UpdateVolumeFill(false);
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) _wasTrackVisible = false;
        };

        Unloaded += (_, _) =>
        {
            _wasTrackVisible = false;
            WhiteNoiseService.StateChanged -= Refresh;
            Loc.LanguageChanged -= Refresh;
        };
        Loaded += (_, _) =>
        {
            WhiteNoiseService.StateChanged -= Refresh;
            WhiteNoiseService.StateChanged += Refresh;
            Loc.LanguageChanged -= Refresh;
            Loc.LanguageChanged += Refresh;
            Refresh();
        };
        Refresh();
    }

    private static bool Animated => !AnimationConfig.ReduceMotion;

    private void AnimateColor(SolidColorBrush brush, Color target, int ms = 180)
    {
        if (!Animated || !IsLoaded || _firstRefresh)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = target;
            return;
        }
        Color current = brush.Color;
        if (current == target) return;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        brush.Color = current;
        var anim = new ColorAnimation(current, target, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = TrayMotion.EaseOut,
            FillBehavior = FillBehavior.HoldEnd
        };
        Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateElementOpacity(UIElement element, double target, int ms)
    {
        if (!Animated || !IsLoaded || _firstRefresh)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = target;
            return;
        }
        double current = element.Opacity;
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = current;
        if (Math.Abs(current - target) < 0.001)
        {
            element.Opacity = target;
            return;
        }
        var anim = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = TrayMotion.EaseOut,
            FillBehavior = FillBehavior.HoldEnd
        };
        Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
        element.BeginAnimation(OpacityProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateContentScale(ScaleTransform scale, double target, int ms)
    {
        if (!Animated || !IsLoaded || _firstRefresh)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = scale.ScaleY = target;
            return;
        }
        double currentX = scale.ScaleX;
        double currentY = scale.ScaleY;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = target;
        scale.ScaleY = target;

        if (Math.Abs(currentX - target) >= 0.001)
        {
            var animX = new DoubleAnimation(currentX, target, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = TrayMotion.EaseOut,
                FillBehavior = FillBehavior.Stop
            };
            Timeline.SetDesiredFrameRate(animX, AnimationConfig.TargetFps);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animX, HandoffBehavior.SnapshotAndReplace);
        }

        if (Math.Abs(currentY - target) >= 0.001)
        {
            var animY = new DoubleAnimation(currentY, target, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = TrayMotion.EaseOut,
                FillBehavior = FillBehavior.Stop
            };
            Timeline.SetDesiredFrameRate(animY, AnimationConfig.TargetFps);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animY, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private static void AnimateScale(ScaleTransform scale, double target, int ms)
    {
        if (!Animated)
        {
            scale.ScaleX = scale.ScaleY = target;
            return;
        }
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease };
        Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void AddChip(WhiteNoiseService.Sound sound)
    {
        var fill = new SolidColorBrush(ChipIdleColor);
        var contentScale = new ScaleTransform(1, 1);
        var label = new TextBlock
        {
            Text = Loc.Get(sound.LocKey),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrushStrong");

        var contentHost = new Grid
        {
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = contentScale,
            Children = { label }
        };
        var surface = new Border
        {
            Padding = new Thickness(6, 0, 6, 0),
            CornerRadius = new CornerRadius(13),
            Background = fill,
            Child = contentHost
        };
        var hover = new Border
        {
            CornerRadius = new CornerRadius(13),
            IsHitTestVisible = false,
            Opacity = 0
        };
        hover.SetResourceReference(Border.BackgroundProperty, "AccentSubtleBrush");

        var root = new Grid
        {
            Height = 26,
            Margin = new Thickness(2, 0, 2, 0),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            Children = { surface, hover }
        };

        var chip = new Chip(root, surface, hover, label, fill, contentScale);
        root.MouseEnter += (_, _) =>
        {
            AnimateElementOpacity(hover, 1.0, 180);
            if (!chip.IsPressed) AnimateContentScale(contentScale, 1.03, 180);
        };
        root.MouseLeave += (_, _) =>
        {
            chip.IsPressed = false;
            AnimateElementOpacity(hover, 0.0, 180);
            AnimateElementOpacity(surface, 1.0, 180);
            AnimateContentScale(contentScale, 1.0, 180);
        };
        root.MouseLeftButtonDown += (_, _) =>
        {
            chip.IsPressed = true;
            AnimateElementOpacity(surface, 0.82, 80);
            AnimateContentScale(contentScale, 0.93, 80);
        };
        root.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            chip.IsPressed = false;
            AnimateElementOpacity(surface, 1.0, 180);
            bool wasPlayingThis = WhiteNoiseService.PlayingKind == sound.Kind;
            WhiteNoiseService.Toggle(sound);
            if (wasPlayingThis || WhiteNoiseService.PlayingKind != sound.Kind)
            {
                AnimateContentScale(contentScale, root.IsMouseOver ? 1.03 : 1.0, 180);
            }
        };
        _chips[sound.Kind] = chip;
        ChipHost.Children.Add(root);
    }

    private (Border Button, Path Icon, ScaleTransform IconScale) MakeIconButton(IconSpec spec, int column, string toolTipKey, Action onClick)
    {
        // Same treatment as the main media controls: bare glyph, no circle, scale feedback only.
        var scale = new ScaleTransform(1, 1);
        var iconScale = new ScaleTransform(1, 1);
        var icon = new Path
        {
            Data = Geometry.Parse(spec.Data),
            Fill = UiPalette.PrimaryBrush,
            Stretch = spec.Stretch,
            Width = spec.Width,
            Height = spec.Height,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = iconScale
        };
        var button = new Border
        {
            Width = 32,
            Height = 26,
            Margin = new Thickness(0, 0, 2, 0),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale,
            ToolTip = Loc.Get(toolTipKey),
            Child = icon
        };
        button.MouseEnter += (_, _) => AnimateScale(scale, 1.12, 140);
        button.MouseLeave += (_, _) => AnimateScale(scale, 1, 140);
        button.MouseLeftButtonDown += (_, _) => AnimateScale(scale, 0.88, 80);
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            AnimateScale(scale, button.IsMouseOver ? 1.12 : 1, 180);
            onClick();
        };
        Grid.SetColumn(button, column);
        ControlRow.Children.Add(button);
        return (button, icon, iconScale);
    }
    private void BuildControls()
    {
        (_muteButton, _mutePath, _) = MakeIconButton(SpeakerIcon, 0, "whitenoise.mute", () => WhiteNoiseService.Muted = !WhiteNoiseService.Muted);
        BuildSyncToggle();
    }

    private void BuildSyncToggle()
    {
        _toggleBrush = new SolidColorBrush(ToggleOffColor);
        _toggleKnob = new TranslateTransform(0, 0);
        _toggleTrack = new Border
        {
            Width = 28,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = _toggleBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(6),
                Background = Brushes.White,
                Margin = new Thickness(2, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = _toggleKnob
            }
        };
        var label = new TextBlock
        {
            Text = Loc.Get("whitenoise.sync"),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(TextIdleColor),
            Margin = new Thickness(7, 0, 0, 0),
            MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            ToolTip = Loc.Get("whitenoise.syncTip"),
            Children = { _toggleTrack, label }
        };
        host.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            WhiteNoiseService.SyncWithTimer = !WhiteNoiseService.SyncWithTimer;
        };
        Grid.SetColumn(host, 2);
        ControlRow.Children.Add(host);
    }

    private void SetIcon(Path path, IconSpec spec)
    {
        if (ReferenceEquals(path.Tag, spec)) return;
        path.Tag = spec;
        var scale = (ScaleTransform)path.RenderTransform;
        void Apply()
        {
            path.Data = Geometry.Parse(spec.Data);
            path.Stretch = spec.Stretch;
            path.Width = spec.Width;
            path.Height = spec.Height;
        }
        if (!Animated || !IsLoaded || _firstRefresh)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = 1;
            scale.ScaleY = 1;
            Apply();
            return;
        }
        // Shrink, swap the glyph, then pop back so play/pause and mute changes feel physical.
        var down = new DoubleAnimation(0.6, TimeSpan.FromMilliseconds(70)) { EasingFunction = Ease };
        down.Completed += (_, _) =>
        {
            if (!ReferenceEquals(path.Tag, spec)) return;
            Apply();
            var up = new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, up);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, up);
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, down);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, down);
    }
    private void ChipHost_LayoutUpdated(object? sender, EventArgs e)
    {
        if (!IsVisible || !ChipHost.IsArrangeValid) return;
        bool justBecameVisible = !_wasTrackVisible;
        _wasTrackVisible = true;
        SyncChipSelection(allowAnimation: !justBecameVisible);
    }

    private void SyncChipSelection(bool allowAnimation)
    {
        int playingKind = WhiteNoiseService.PlayingKind;
        bool canAnimate = allowAnimation && Animated && IsLoaded && !_firstRefresh && IsVisible;

        if (!_chips.TryGetValue(playingKind, out var activeChip))
        {
            _animatedPlayingKind = null;
            if (!_selectionVisible) return;
            _selectionVisible = false;

            ChipSelectionPulse.BeginAnimation(OpacityProperty, null);
            ChipSelectionPulse.Opacity = 0;

            if (!canAnimate || _chipSelectionBounds.IsEmpty)
            {
                ChipSelection.BeginAnimation(OpacityProperty, null);
                ChipSelectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                ChipSelectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                ChipSelectionOffset.BeginAnimation(TranslateTransform.XProperty, null);
                ChipSelection.Opacity = 0;
                ChipSelectionScale.ScaleX = 1;
                ChipSelectionScale.ScaleY = 1;
                _chipSelectionBounds = Rect.Empty;
                return;
            }

            AnimateElementOpacity(ChipSelection, 0.0, 170);
            MoveChipIndicator(_chipSelectionBounds.X + _chipSelectionBounds.Width * 0.06, animate: true);

            double curScaleX = ChipSelectionScale.ScaleX;
            double curScaleY = ChipSelectionScale.ScaleY;
            ChipSelectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ChipSelectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ChipSelectionScale.ScaleX = curScaleX;
            ChipSelectionScale.ScaleY = curScaleY;

            var shrinkX = new DoubleAnimation(curScaleX, 0.88, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = TrayMotion.EaseOut,
                FillBehavior = FillBehavior.HoldEnd
            };
            var shrinkY = new DoubleAnimation(curScaleY, 0.88, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = TrayMotion.EaseOut,
                FillBehavior = FillBehavior.HoldEnd
            };
            Timeline.SetDesiredFrameRate(shrinkX, AnimationConfig.TargetFps);
            Timeline.SetDesiredFrameRate(shrinkY, AnimationConfig.TargetFps);
            ChipSelectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrinkX, HandoffBehavior.SnapshotAndReplace);
            ChipSelectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrinkY, HandoffBehavior.SnapshotAndReplace);
            return;
        }

        if (activeChip.Root.ActualWidth <= 0) return;

        var pos = activeChip.Root.TranslatePoint(new Point(), ChipTrack);
        var bounds = new Rect(pos, activeChip.Root.RenderSize);
        if (_selectionVisible && _animatedPlayingKind == playingKind && bounds == _chipSelectionBounds) return;

        bool wasVisible = _selectionVisible;
        bool midFadeOut = !wasVisible && ChipSelection.Opacity > 0.02 && !_chipSelectionBounds.IsEmpty;
        bool kindChanged = _animatedPlayingKind != playingKind;

        _selectionVisible = true;
        _animatedPlayingKind = playingKind;
        _chipSelectionBounds = bounds;

        if (!canAnimate)
        {
            ChipSelection.BeginAnimation(OpacityProperty, null);
            ChipSelectionPulse.BeginAnimation(OpacityProperty, null);
            ChipSelectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ChipSelectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ChipSelection.Width = bounds.Width;
            ChipSelection.Opacity = 1;
            ChipSelectionPulse.Opacity = 0;
            ChipSelectionScale.ScaleX = 1;
            ChipSelectionScale.ScaleY = 1;
            MoveChipIndicator(bounds.X, animate: false);
            return;
        }

        double currentX;
        double currentWidth;
        if (wasVisible || midFadeOut)
        {
            currentX = ChipSelectionOffset.X;
            currentWidth = ChipSelection.Width * ChipSelectionScale.ScaleX;
        }
        else
        {
            currentWidth = bounds.Width * 0.86;
            currentX = bounds.X + (bounds.Width - currentWidth) * 0.5;
            ChipSelectionOffset.BeginAnimation(TranslateTransform.XProperty, null);
            ChipSelectionOffset.X = currentX;
        }

        ChipSelection.Width = bounds.Width;
        ChipSelectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ChipSelectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        if (wasVisible && ChipSelection.Opacity >= 0.999)
        {
            ChipSelection.BeginAnimation(OpacityProperty, null);
            ChipSelection.Opacity = 1;
        }
        else
        {
            AnimateElementOpacity(ChipSelection, 1.0, 180);
        }

        if (ClipboardTray.vn_animation_compute_category_pill_motion(
                currentX,
                currentWidth,
                bounds.X,
                bounds.Width,
                0,
                out var motion) < 0)
        {
            ChipSelectionScale.ScaleX = 1;
            ChipSelectionScale.ScaleY = 1;
            MoveChipIndicator(bounds.X, animate: false);
            return;
        }

        ChipSelectionScale.ScaleX = motion.StartScaleX;
        ChipSelectionScale.ScaleY = wasVisible || midFadeOut ? 1.0 : 0.88;
        MoveChipIndicator(bounds.X, animate: true);
        AnimateChipPillMorph(in motion, startScaleY: ChipSelectionScale.ScaleY);

        if (kindChanged)
        {
            AnimateSelectedChipContent(activeChip, motion.ContentPopScale);
        }
    }

    private void AnimateChipPillMorph(in ClipboardTray.VnCategoryPillMotionNative motion, double startScaleY)
    {
        ChipSelectionPulse.BeginAnimation(OpacityProperty, null);
        if (motion.DurationMs <= 0)
        {
            ChipSelectionScale.ScaleX = 1;
            ChipSelectionScale.ScaleY = 1;
            ChipSelectionPulse.Opacity = 0;
            return;
        }

        var scaleXFrames = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        scaleXFrames.KeyFrames.Add(new DiscreteDoubleKeyFrame(motion.StartScaleX, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        scaleXFrames.KeyFrames.Add(new EasingDoubleKeyFrame(motion.PeakScaleX, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(motion.PeakTimeMs)), TrayMotion.EaseOut));
        scaleXFrames.KeyFrames.Add(new EasingDoubleKeyFrame(motion.SettleOvershootScaleX, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(motion.SettleOvershootTimeMs)), TrayMotion.EaseOut));
        scaleXFrames.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(motion.DurationMs)), TrayMotion.EaseOut));
        Timeline.SetDesiredFrameRate(scaleXFrames, AnimationConfig.TargetFps);
        ChipSelectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXFrames, HandoffBehavior.SnapshotAndReplace);

        var scaleYFrames = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        scaleYFrames.KeyFrames.Add(new DiscreteDoubleKeyFrame(startScaleY, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        scaleYFrames.KeyFrames.Add(new EasingDoubleKeyFrame(motion.DipScaleY, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(motion.PeakTimeMs)), TrayMotion.EaseOut));
        scaleYFrames.KeyFrames.Add(new EasingDoubleKeyFrame(motion.ReboundScaleY, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(motion.SettleOvershootTimeMs)), TrayMotion.EaseOut));
        scaleYFrames.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(motion.DurationMs)), TrayMotion.EaseOut));
        Timeline.SetDesiredFrameRate(scaleYFrames, AnimationConfig.TargetFps);
        ChipSelectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYFrames, HandoffBehavior.SnapshotAndReplace);

        var pulseFrames = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        pulseFrames.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pulseFrames.KeyFrames.Add(new EasingDoubleKeyFrame(motion.GlowPeakOpacity, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70)), TrayMotion.EaseOut));
        pulseFrames.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(motion.DurationMs)), TrayMotion.EaseOut));
        Timeline.SetDesiredFrameRate(pulseFrames, AnimationConfig.TargetFps);
        ChipSelectionPulse.Opacity = 0;
        ChipSelectionPulse.BeginAnimation(OpacityProperty, pulseFrames, HandoffBehavior.SnapshotAndReplace);
    }

    private static void AnimateSelectedChipContent(Chip chip, double peakScale)
    {
        var scale = chip.ContentScale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        double restScale = chip.Root.IsMouseOver && Animated ? 1.03 : 1.0;
        scale.ScaleX = restScale;
        scale.ScaleY = restScale;
        if (peakScale <= 1.0) return;

        var pop = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        pop.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.92, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(peakScale, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(115)), TrayMotion.EaseOut));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(restScale, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(245)), TrayMotion.EaseOut));
        Timeline.SetDesiredFrameRate(pop, AnimationConfig.TargetFps);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop, HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop, HandoffBehavior.SnapshotAndReplace);
    }

    private void MoveChipIndicator(double targetX, bool animate)
    {
        double current = ChipSelectionOffset.X;
        ChipSelectionOffset.BeginAnimation(TranslateTransform.XProperty, null);
        ChipSelectionOffset.X = animate ? current : targetX;
        if (!animate) return;

        var motion = new DoubleAnimation(current, targetX, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = TrayMotion.EaseOut,
            FillBehavior = FillBehavior.HoldEnd
        };
        Timeline.SetDesiredFrameRate(motion, AnimationConfig.TargetFps);
        ChipSelectionOffset.BeginAnimation(TranslateTransform.XProperty, motion, HandoffBehavior.SnapshotAndReplace);
    }

    internal void RefreshForTest() => Refresh();

    private void Refresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Refresh);
            return;
        }

        foreach (var sound in WhiteNoiseService.Catalog)
        {
            var chip = _chips[sound.Kind];
            chip.Label.Text = Loc.Get(sound.LocKey);
            bool playing = WhiteNoiseService.PlayingKind == sound.Kind;
            AnimateColor(chip.Fill, playing ? ChipActiveSurfaceColor : ChipIdleColor);
            if (playing)
            {
                chip.Hover.Background = SelectedHoverBrush;
            }
            else
            {
                chip.Hover.SetResourceReference(Border.BackgroundProperty, "AccentSubtleBrush");
            }
            chip.Label.SetResourceReference(TextBlock.ForegroundProperty, playing ? "AccentForegroundBrush" : "AccentBrushStrong");
        }

        if (IsVisible && ChipHost.IsArrangeValid)
        {
            SyncChipSelection(allowAnimation: !_firstRefresh && _wasTrackVisible);
        }


        SetIcon(_mutePath, WhiteNoiseService.Muted ? MutedIcon : SpeakerIcon);
        ((Path)_muteButton.Child).Opacity = WhiteNoiseService.Muted ? 0.55 : 1;

        bool sync = WhiteNoiseService.SyncWithTimer;
        if (sync) _toggleTrack.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        else
        {
            _toggleTrack.Background = _toggleBrush;
            AnimateColor(_toggleBrush, ToggleOffColor);
        }
        if (_toggleTrack.Child is Border knob)
        {
            knob.Background = Brushes.White;
        }
        AnimateKnob(sync ? 12 : 0);

        UpdateVolumeFill(Animated && !_draggingVolume);
        _firstRefresh = false;
    }

    private void AnimateKnob(double target)
    {
        if (!Animated || !IsLoaded || _firstRefresh)
        {
            _toggleKnob.BeginAnimation(TranslateTransform.XProperty, null);
            _toggleKnob.X = target;
            return;
        }
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(180)) { EasingFunction = Ease };
        Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
        _toggleKnob.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void UpdateVolumeFill(bool animate)
    {
        double target = VolumeBar.ActualWidth * (WhiteNoiseService.Muted ? 0 : WhiteNoiseService.Volume);
        if (!animate || !IsLoaded)
        {
            VolumeFill.BeginAnimation(WidthProperty, null);
            VolumeFill.Width = target;
            return;
        }
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(160)) { EasingFunction = Ease };
        Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
        VolumeFill.BeginAnimation(WidthProperty, anim);
    }

    private void SetVolumeFromPoint(MouseEventArgs e)
    {
        if (VolumeBar.ActualWidth <= 0) return;
        WhiteNoiseService.Volume = (float)(e.GetPosition(VolumeBar).X / VolumeBar.ActualWidth);
    }

    private void VolumeBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingVolume = true;
        VolumeBar.CaptureMouse();
        SetVolumeFromPoint(e);
        e.Handled = true;
    }

    private void VolumeBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingVolume) SetVolumeFromPoint(e);
    }

    private void VolumeBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _draggingVolume = false;
        VolumeBar.ReleaseMouseCapture();
        e.Handled = true;
    }
}
