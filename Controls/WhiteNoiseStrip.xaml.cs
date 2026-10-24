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
    private static readonly Color ChipIdleColor = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
    private static readonly Color ChipHoverColor = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF);
    private static readonly Color ChipActiveColor = Color.FromRgb(0xFF, 0x8C, 0x1A);
    private static readonly Color TextIdleColor = Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF);
    private static readonly Color TextActiveColor = Color.FromRgb(0x1A, 0x10, 0x05);
    private static readonly Color ToggleOffColor = Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF);

    private sealed record IconSpec(string Data, double Width, double Height, Stretch Stretch);

    private static readonly IconSpec SpeakerIcon = new(
        "M3,9 H7 L12,5 V19 L7,15 H3 Z M14.2,8.2 A5,5 0 0 1 14.2,15.8 L13,14.4 A3.2,3.2 0 0 0 13,9.6 Z",
        15, 15, Stretch.Uniform);
    private static readonly IconSpec MutedIcon = new(
        "M3,9 H7 L12,5 V19 L7,15 H3 Z M14,9.4 L15.4,8 L17.5,10.1 L19.6,8 L21,9.4 L18.9,11.5 L21,13.6 L19.6,15 L17.5,12.9 L15.4,15 L14,13.6 L16.1,11.5 Z",
        15, 15, Stretch.Uniform);
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    private sealed record Chip(Border Border, TextBlock Label, SolidColorBrush Fill, SolidColorBrush Text, ScaleTransform Scale);

    private readonly Dictionary<int, Chip> _chips = [];
    private Border _muteButton = null!;
    private Path _mutePath = null!;
    private Border _toggleTrack = null!;
    private SolidColorBrush _toggleBrush = null!;
    private TranslateTransform _toggleKnob = null!;
    private bool _draggingVolume;
    private bool _firstRefresh = true;

    public WhiteNoiseStrip()
    {
        InitializeComponent();
        foreach (var sound in WhiteNoiseService.Catalog) AddChip(sound);
        BuildControls();
        VolumeBar.ToolTip = Loc.Get("whitenoise.volume");
        VolumeBar.SizeChanged += (_, _) => UpdateVolumeFill(false);
        WhiteNoiseService.StateChanged += Refresh;
        Unloaded += (_, _) => WhiteNoiseService.StateChanged -= Refresh;
        Loaded += (_, _) =>
        {
            WhiteNoiseService.StateChanged -= Refresh;
            WhiteNoiseService.StateChanged += Refresh;
            Refresh();
        };
        Refresh();
    }

    private static bool Animated => !AnimationConfig.ReduceMotion;

    private void AnimateColor(SolidColorBrush brush, Color target, int ms = 200)
    {
        if (!Animated || !IsLoaded || _firstRefresh)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = target;
            return;
        }
        var anim = new ColorAnimation(target, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease };
        Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
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
        var textBrush = new SolidColorBrush(TextIdleColor);
        var fill = new SolidColorBrush(ChipIdleColor);
        var scale = new ScaleTransform(1, 1);
        var label = new TextBlock
        {
            Text = Loc.Get(sound.LocKey),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = textBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var border = new Border
        {
            Height = 26,
            Margin = new Thickness(2, 0, 2, 0),
            Padding = new Thickness(6, 0, 6, 0),
            CornerRadius = new CornerRadius(13),
            Cursor = Cursors.Hand,
            Background = fill,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale,
            Child = label
        };
        border.MouseEnter += (_, _) =>
        {
            if (WhiteNoiseService.PlayingKind != sound.Kind) AnimateColor(fill, ChipHoverColor, 120);
        };
        border.MouseLeave += (_, _) =>
        {
            if (WhiteNoiseService.PlayingKind != sound.Kind) AnimateColor(fill, ChipIdleColor, 160);
            AnimateScale(scale, 1, 140);
        };
        border.MouseLeftButtonDown += (_, _) => AnimateScale(scale, 0.94, 80);
        border.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            AnimateScale(scale, 1, 180);
            WhiteNoiseService.Toggle(sound);
        };
        _chips[sound.Kind] = new Chip(border, label, fill, textBrush, scale);
        ChipHost.Children.Add(border);
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
            FontWeight = FontWeights.SemiBold,
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
        void Apply()
        {
            path.Data = Geometry.Parse(spec.Data);
            path.Stretch = spec.Stretch;
            path.Width = spec.Width;
            path.Height = spec.Height;
        }
        if (!Animated || !IsLoaded || _firstRefresh)
        {
            Apply();
            return;
        }
        // Shrink, swap the glyph, then pop back so play/pause and mute changes feel physical.
        var scale = (ScaleTransform)path.RenderTransform;
        var down = new DoubleAnimation(0.6, TimeSpan.FromMilliseconds(70)) { EasingFunction = Ease };
        down.Completed += (_, _) =>
        {
            Apply();
            var up = new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, up);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, up);
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, down);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, down);
    }
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
            bool playing = WhiteNoiseService.PlayingKind == sound.Kind;
            AnimateColor(chip.Fill, playing ? ChipActiveColor : ChipIdleColor);
            AnimateColor(chip.Text, playing ? TextActiveColor : TextIdleColor);
        }


        SetIcon(_mutePath, WhiteNoiseService.Muted ? MutedIcon : SpeakerIcon);
        ((Path)_muteButton.Child).Opacity = WhiteNoiseService.Muted ? 0.55 : 1;

        bool sync = WhiteNoiseService.SyncWithTimer;
        AnimateColor(_toggleBrush, sync ? ChipActiveColor : ToggleOffColor);
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
