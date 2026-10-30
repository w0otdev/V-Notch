using System.Runtime.InteropServices;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;
using static VNotch.Services.Win32Interop;

namespace VNotch;

public partial class MainWindow
{
    #region Media Controls

    private const string MediaCtrlLogTag = "MEDIA-CTRL";
    private bool _isPlaying = true;

    private async Task TogglePlayPauseInstantAsync()
    {
        try
        {
            if ((DateTime.UtcNow - _lastMediaActionTime).TotalMilliseconds < 250) return;
            _lastMediaActionTime = DateTime.UtcNow;

            WakeFromIdle();

            if (_isVolumeIndicatorActive)
            {
                DismissVolumeIndicatorImmediate(restoreMedia: true);
            }

            _isPlaying = !_viewModel.IsPlaying;
            UpdatePlayPauseIcon();

            if (!_isExpanded && !_isAnimating &&
                !_transitionCoordinator.IsTransitionActive &&
                _transitionCoordinator.CurrentView == NotchView.Compact)
            {
                PlayGestureMiddleClickFeedback();
            }

            if (_isExpanded && MediaPlayback.PlayPauseButton != null && MediaPlayback.PlayPauseButton.Visibility == Visibility.Visible)
            {
                PlayGentleButtonPressAnimation(MediaPlayback.PlayPauseButton);
            }
            else if (_isExpanded && InlinePlayPauseButton != null && InlinePlayPauseButton.Visibility == Visibility.Visible)
            {
                PlayGentleButtonPressAnimation(InlinePlayPauseButton);
            }

            _progressEngine.NotifyUserPlayPause(_isPlaying);
            await _viewModel.PlayPauseCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(MediaCtrlLogTag, ex, "MiddleClick/PlayPause failed");
        }
    }

    private void PlayPauseButton_Click(object sender, MouseButtonEventArgs e) => MediaPlayback.PlayPauseButton_Click(sender, e);

    private void NextButton_Click(object sender, MouseButtonEventArgs e) => MediaPlayback.NextButton_Click(sender, e);

    private void PrevButton_Click(object sender, MouseButtonEventArgs e) => MediaPlayback.PrevButton_Click(sender, e);

    private bool? _presentedInlinePlaying;

    private void OnViewModelPlayPauseToggled(object? sender, bool isPlaying)
    {
        _lastMediaActionTime = DateTime.UtcNow;
        _isPlaying = isPlaying;
        UpdatePlayPauseIcon();
    }

    private void UpdatePlayPauseIcon()
    {
        RefreshPlaybackAccessibleNames();
        MediaPlayback.PresentPlayback(_isPlaying);
        if (_presentedInlinePlaying == _isPlaying) return;
        _presentedInlinePlaying = _isPlaying;
        var duration = TimeSpan.FromMilliseconds(180);

        if (_isPlaying)
        {
            AnimateIconSwitch(InlinePlayIcon, InlinePauseIcon, duration, _easeQuadInOut);
        }
        else
        {
            AnimateIconSwitch(InlinePauseIcon, InlinePlayIcon, duration, _easeQuadInOut);
        }
    }

    private async void InlinePlayPauseButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            _lastMediaActionTime = DateTime.UtcNow;
            _isPlaying = !_viewModel.IsPlaying;
            UpdatePlayPauseIcon();
            PlayButtonPressAnimation(InlinePlayPauseButton);

            _progressEngine.NotifyUserPlayPause(_isPlaying);

            await _viewModel.PlayPauseCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(MediaCtrlLogTag, ex, "InlinePlayPause failed");
        }
    }

    private async void InlineNextButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            PlayButtonPressAnimation(InlineNextButton);
            PlayNextSkipAnimation(InlineNextArrow0, InlineNextArrow1);

            OptimisticPrepareForNextTrack();
            await _viewModel.NextTrackCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(MediaCtrlLogTag, ex, "InlineNext failed");
        }
    }

    private async void InlinePrevButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            PlayButtonPressAnimation(InlinePrevButton);
            PlayPrevSkipAnimation(InlinePrevArrow0, InlinePrevArrow2);

            PrepareForPreviousTrackRequest();
            await _viewModel.PreviousTrackCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(MediaCtrlLogTag, ex, "InlinePrev failed");
        }
    }

    private void PrepareForPreviousTrackRequest() => MediaPlayback.PrepareForPreviousTrackRequest();
    private void OptimisticPrepareForNextTrack() => MediaPlayback.OptimisticPrepareForNextTrack();

    private void ThumbnailBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenCurrentMediaSourceFromThumbnail();
    }

    private void CompactThumbnailBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.EnableGestureControls && !_isExpanded && !_isMusicExpanded &&
            _currentMediaInfo != null && _currentMediaInfo.IsAnyMediaPlaying && !_isAnimating)
        {
            return;
        }

        e.Handled = true;
        if (!_isExpanded && !_isAnimating)
        {
            ExpandNotch();
        }
    }

    private void OpenCurrentMediaSourceFromThumbnail()
    {
        var info = _currentMediaInfo;
        if (info == null || !info.IsAnyMediaPlaying) return;
        Task.Run(() => MediaWindowActivator.TryActivateForMedia(info), System.Threading.CancellationToken.None)
            .SafeFireAndForget("MEDIA-ACTIVATE");
    }

    private void MediaButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border button && button.RenderTransform is ScaleTransform transform)
        {
            button.CacheMode ??= new System.Windows.Media.BitmapCache(1.5);

            var animX = new DoubleAnimation(transform.ScaleX, 1.18, _dur150) { EasingFunction = _easeQuadOut };
            var animY = new DoubleAnimation(transform.ScaleY, 1.18, _dur150) { EasingFunction = _easeQuadOut };
            Timeline.SetDesiredFrameRate(animX, VNotch.Services.AnimationConfig.TargetFps);
            Timeline.SetDesiredFrameRate(animY, VNotch.Services.AnimationConfig.TargetFps);
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
        }
    }

    private void MediaButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border button && button.RenderTransform is ScaleTransform transform)
        {
            var animX = new DoubleAnimation(transform.ScaleX, 1.0, _dur200) { EasingFunction = _easeQuadOut };
            var animY = new DoubleAnimation(transform.ScaleY, 1.0, _dur200) { EasingFunction = _easeQuadOut };
            Timeline.SetDesiredFrameRate(animX, VNotch.Services.AnimationConfig.TargetFps);
            Timeline.SetDesiredFrameRate(animY, VNotch.Services.AnimationConfig.TargetFps);

            animX.Completed += (_, _) =>
            {
                if (!button.IsMouseOver)
                    button.CacheMode = null;
            };

            transform.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
        }
    }

    #endregion

    #region Volume Control

    private float _currentVolume = 0.5f;
    private const float VolumeScrollStep = 0.05f;
    private const double CompactVolumeImmediateWheelDelta = 120.0;
    private const double CompactVolumeSmoothActivationDelta = 240.0;
    private const double CompactVolumeWheelSessionResetMs = 450.0;
    private const double CompactVolumeClickSuppressMs = 600.0;
    private UiScheduledTimer? _volumeIndicatorHideTimer;
    private bool _isVolumeIndicatorActive = false;
    private bool _volumeSynced = false;
    private int _volumeIndicatorToken = 0;
    private double _compactVolumeWheelAccumulator = 0;
    private DateTime _lastCompactVolumeWheelUtc = DateTime.MinValue;
    private DateTime _suppressCompactVolumeWheelUntilUtc = DateTime.MinValue;
    private bool _isCompactVolumeWheelActive = false;

    private void SuppressCompactVolumeWheelForClick()
    {
        _suppressCompactVolumeWheelUntilUtc = DateTime.UtcNow.AddMilliseconds(CompactVolumeClickSuppressMs);
        ResetCompactVolumeWheelIntent();
    }

    private void ResetCompactVolumeWheelIntent()
    {
        ++_volumeBaselineVersion;
        _volumeBaselinePending = false;
        _compactVolumeWheelAccumulator = 0;
        _lastCompactVolumeWheelUtc = DateTime.MinValue;
        _isCompactVolumeWheelActive = false;
    }

    private bool TryGetCompactVolumeWheelDelta(int rawDelta, out int volumeDelta)
    {
        volumeDelta = 0;
        if (rawDelta == 0) return false;

        var now = DateTime.UtcNow;
        if (now < _suppressCompactVolumeWheelUntilUtc)
        {
            ResetCompactVolumeWheelIntent();
            return false;
        }

        if ((now - _lastCompactVolumeWheelUtc).TotalMilliseconds > CompactVolumeWheelSessionResetMs)
        {
            _compactVolumeWheelAccumulator = 0;
            _isCompactVolumeWheelActive = false;
        }
        _lastCompactVolumeWheelUtc = now;

        if (_isVolumeIndicatorActive || _isCompactVolumeWheelActive)
        {
            _isCompactVolumeWheelActive = true;
            volumeDelta = rawDelta;
            return true;
        }

        if (Math.Abs(rawDelta) >= CompactVolumeImmediateWheelDelta)
        {
            _isCompactVolumeWheelActive = true;
            volumeDelta = rawDelta;
            return true;
        }

        if (_compactVolumeWheelAccumulator != 0 &&
            Math.Sign(_compactVolumeWheelAccumulator) != Math.Sign(rawDelta))
        {
            _compactVolumeWheelAccumulator = 0;
        }

        _compactVolumeWheelAccumulator += rawDelta;
        if (Math.Abs(_compactVolumeWheelAccumulator) < CompactVolumeSmoothActivationDelta)
        {
            return false;
        }

        _compactVolumeWheelAccumulator = 0;
        _isCompactVolumeWheelActive = true;
        volumeDelta = Math.Sign(rawDelta) * (int)CompactVolumeImmediateWheelDelta;
        return true;
    }

    private bool _volumeBaselinePending;
    private int _volumeBaselineVersion;
    private DateTime _volumeScrollSyncAfterUtc = DateTime.MinValue;

    private const int VolumeHudTickCount = 40;
    private const double VolumeHudExtraWidth = 44.0;
    private const double VolumeHudExtraHeight = 20.0;



    private Border[]? _volumeHudTickBorders;
    private SolidColorBrush[]? _volumeHudTickBrushes;
    private ScaleTransform[]? _volumeHudTickScales;
    private Color _volumeHudMediaColor = Colors.White;
    private bool _hasVolumeHudMediaTint;
    private int _lastVolumeHudActiveTicks = -1;
    private BlurEffect? _volumeIndicatorTransitionBlur;
    private int _volumeIndicatorTransitionVersion;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnVolumeHudLayoutOut
    {
        public int Percentage;
        public int ActiveTicks;
        public int HighlightStartTick;
        public int IconCodepoint;
        public double LabelOffsetX;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnVolumeHudOverscrollOut
    {
        public double ScaleX;
        public double ScaleY;
        public double TranslateX;
        public double OriginX;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnVolumeHudTransitionParamsOut
    {
        public double HudFromScaleX;
        public double HudFromScaleY;
        public double HudToScaleX;
        public double HudToScaleY;
        public double HudFromTranslateY;
        public double HudToTranslateY;
        public double HudFromBlur;
        public double HudToBlur;
        public double MediaToScale;
        public double MediaToBlur;
        public double TargetWidth;
        public double TargetHeight;
        public double TargetCornerRadius;
        public int MorphDurationMs;
        public int FadeDurationMs;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_animation_compute_volume_hud_state(
        double volume,
        byte muted,
        int tickCount,
        double trackWidth,
        double labelWidth,
        double minLeftX,
        out VnVolumeHudLayoutOut outLayout,
        byte* outTickStates,
        int tickCap);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_animation_compute_volume_hud_palette(
        int tickCount,
        int activeTicks,
        int highlightStartTick,
        byte mediaR,
        byte mediaG,
        byte mediaB,
        byte hasMediaTint,
        uint* outArgb,
        double* outScaleY,
        int tickCap);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_animation_compute_volume_overscroll_spring(
        double currentVolume,
        double stepDelta,
        out VnVolumeHudOverscrollOut outOverscroll);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_animation_compute_volume_hud_transition(
        byte entering,
        byte islandMode,
        byte enableBlur,
        double collapsedWidth,
        double collapsedHeight,
        double collapsedRadius,
        out VnVolumeHudTransitionParamsOut outParams);

    private void ClearVolumeIndicatorTransitionEffects()
    {
        if (VolumeIndicatorContainer == null) return;
        if (_volumeIndicatorTransitionBlur != null)
        {
            _volumeIndicatorTransitionBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            _volumeIndicatorTransitionBlur.Radius = 0;
        }
        VolumeIndicatorContainer.Effect = null;
        VolumeIndicatorContainer.CacheMode = null;
    }

    private void SetVolumeHudMediaTint(Color vibrantColor, bool hasMediaTint)
    {
        _volumeHudMediaColor = vibrantColor;
        _hasVolumeHudMediaTint = hasMediaTint;
        Color glowColor = hasMediaTint ? vibrantColor : Colors.White;
        if (VolumeIndicatorTicksGlow != null) VolumeIndicatorTicksGlow.Color = glowColor;
        if (VolumeIndicatorIconGlow != null) VolumeIndicatorIconGlow.Color = glowColor;
        if (_isVolumeIndicatorActive && VolumeIndicatorScale != null)
        {
            UpdateVolumeIndicatorHud(VolumeIndicatorScale.ScaleX, animate: false);
        }
    }

    private void EnsureVolumeIndicatorTicks()
    {
        if (_volumeHudTickBorders != null || VolumeIndicatorTicksHost == null) return;
        VolumeIndicatorTicksHost.Children.Clear();
        _volumeHudTickBorders = new Border[VolumeHudTickCount];
        _volumeHudTickBrushes = new SolidColorBrush[VolumeHudTickCount];
        _volumeHudTickScales = new ScaleTransform[VolumeHudTickCount];
        for (int i = 0; i < VolumeHudTickCount; i++)
        {
            var brush = new SolidColorBrush(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF));
            var scale = new ScaleTransform(1.0, 0.86);
            var tick = new Border
            {
                Width = 2.4,
                Height = 9.2,
                CornerRadius = new CornerRadius(1.2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = brush,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale,
                SnapsToDevicePixels = false
            };
            _volumeHudTickBorders[i] = tick;
            _volumeHudTickBrushes[i] = brush;
            _volumeHudTickScales[i] = scale;
            VolumeIndicatorTicksHost.Children.Add(tick);
        }
    }

    private void UpdateSolidSpeakerIconState(int iconCodepoint, Color accentColor, bool animate)
    {
        if (VolumeIndicatorSpeakerCone == null) return;
        bool isMuted = iconCodepoint == 0xE74F;
        bool hasWave1 = !isMuted && iconCodepoint >= 0xE993;
        bool hasWave2 = !isMuted && iconCodepoint >= 0xE995;

        if (VolumeIndicatorMuteSlashGap != null)
            VolumeIndicatorMuteSlashGap.Visibility = isMuted ? Visibility.Visible : Visibility.Collapsed;
        if (VolumeIndicatorMuteSlash != null)
        {
            VolumeIndicatorMuteSlash.Visibility = isMuted ? Visibility.Visible : Visibility.Collapsed;
            if (VolumeIndicatorMuteSlash.Stroke is SolidColorBrush slashBrush && !slashBrush.IsFrozen)
                slashBrush.Color = accentColor;
            else
                VolumeIndicatorMuteSlash.Stroke = new SolidColorBrush(accentColor);
        }
        if (VolumeIndicatorWave1 != null)
        {
            double wave1Opacity = hasWave1 ? 1.0 : 0.22;
            VolumeIndicatorWave1.Opacity = isMuted ? 0.0 : wave1Opacity;
        }
        if (VolumeIndicatorWave2 != null)
        {
            double wave2Base = iconCodepoint == 0xE994 ? 0.32 : 0.15;
            double wave2Opacity = hasWave2 ? 1.0 : wave2Base;
            VolumeIndicatorWave2.Opacity = isMuted ? 0.0 : wave2Opacity;
        }

        if (VolumeIndicatorSpeakerCone.Fill is SolidColorBrush coneBrush && !coneBrush.IsFrozen)
            coneBrush.Color = accentColor;
        else
            VolumeIndicatorSpeakerCone.Fill = new SolidColorBrush(accentColor);

        if (VolumeIndicatorSpeakerCone.Stroke is SolidColorBrush coneStroke && !coneStroke.IsFrozen)
            coneStroke.Color = accentColor;
        else
            VolumeIndicatorSpeakerCone.Stroke = new SolidColorBrush(accentColor);

        if (VolumeIndicatorWave1?.Stroke is SolidColorBrush w1Brush && !w1Brush.IsFrozen)
            w1Brush.Color = accentColor;
        else if (VolumeIndicatorWave1 != null)
            VolumeIndicatorWave1.Stroke = new SolidColorBrush(accentColor);

        if (VolumeIndicatorWave2?.Stroke is SolidColorBrush w2Brush && !w2Brush.IsFrozen)
            w2Brush.Color = accentColor;
        else if (VolumeIndicatorWave2 != null)
            VolumeIndicatorWave2.Stroke = new SolidColorBrush(accentColor);

        if (animate && !AnimationConfig.ReduceMotion && VolumeIndicatorIconScale != null)
        {
            var pop = new DoubleAnimationUsingKeyFrames();
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.16, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(75)), _easeQuadOut));
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)), _easeSoftSpring));
            Timeline.SetDesiredFrameRate(pop, AnimationConfig.TargetFps);
            VolumeIndicatorIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            VolumeIndicatorIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
    }

    private unsafe void UpdateVolumeIndicatorHud(double volume, bool animate = false)
    {
        if (VolumeIndicatorTicksHost == null) return;
        EnsureVolumeIndicatorTicks();
        if (_volumeHudTickBorders == null || _volumeHudTickBrushes == null || _volumeHudTickScales == null) return;

        byte* tickStates = stackalloc byte[VolumeHudTickCount];
        uint* tickArgb = stackalloc uint[VolumeHudTickCount];
        double* tickScaleY = stackalloc double[VolumeHudTickCount];

        double trackWidth = VolumeIndicatorContainer.ActualWidth > 0
            ? VolumeIndicatorContainer.ActualWidth
            : Math.Max(120.0, (_collapsedWidth + VolumeHudExtraWidth) - 28.0);
        bool isMuted = _viewModel?.AudioMixer?.IsMuted == true && volume <= 0.0001;

        double approxLabelWidth = VolumeIndicatorPercentText?.ActualWidth > 0
            ? VolumeIndicatorPercentText.ActualWidth
            : 28.0;

        if (vn_animation_compute_volume_hud_state(
                volume,
                isMuted ? (byte)1 : (byte)0,
                VolumeHudTickCount,
                trackWidth,
                approxLabelWidth,
                30.0,
                out var layout,
                tickStates,
                VolumeHudTickCount) != 0)
        {
            return;
        }

        if (VolumeIndicatorIcon != null)
        {
            string glyph = ((char)layout.IconCodepoint).ToString();
            if (!string.Equals(VolumeIndicatorIcon.Text, glyph, StringComparison.Ordinal))
                VolumeIndicatorIcon.Text = glyph;
        }

        if (VolumeIndicatorPercentText != null)
        {
            string pctText = $"{layout.Percentage}%";
            if (!string.Equals(VolumeIndicatorPercentText.Text, pctText, StringComparison.Ordinal))
            {
                VolumeIndicatorPercentText.Text = pctText;
                VolumeIndicatorPercentText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (animate && !AnimationConfig.ReduceMotion && VolumeIndicatorPercentScale != null)
                {
                    var pop = new DoubleAnimationUsingKeyFrames();
                    pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(65)), _easeQuadOut));
                    pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)), _easeSoftSpring));
                    Timeline.SetDesiredFrameRate(pop, AnimationConfig.TargetFps);
                    VolumeIndicatorPercentScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                    VolumeIndicatorPercentScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
                }
            }
            double desiredLabelW = VolumeIndicatorPercentText.DesiredSize.Width > 0 ? VolumeIndicatorPercentText.DesiredSize.Width : approxLabelWidth;
            double measuredLabelW = VolumeIndicatorPercentText.ActualWidth > 0 ? VolumeIndicatorPercentText.ActualWidth : desiredLabelW;

            if (Math.Abs(measuredLabelW - approxLabelWidth) > 0.5)
            {
                _ = vn_animation_compute_volume_hud_state(
                    volume,
                    isMuted ? (byte)1 : (byte)0,
                    VolumeHudTickCount,
                    trackWidth,
                    measuredLabelW,
                    30.0,
                    out layout,
                    tickStates,
                    VolumeHudTickCount);
            }
        }

        if (VolumeIndicatorPercentTranslate != null)
        {
            double currentX = VolumeIndicatorPercentTranslate.X;
            VolumeIndicatorPercentTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            VolumeIndicatorPercentTranslate.X = layout.LabelOffsetX;
            if (animate && !AnimationConfig.ReduceMotion && Math.Abs(currentX - layout.LabelOffsetX) > 0.5)
            {
                var moveAnim = MakeAnim(currentX, layout.LabelOffsetX, _dur150, _easeAppleOut);
                moveAnim.FillBehavior = FillBehavior.Stop;
                VolumeIndicatorPercentTranslate.BeginAnimation(TranslateTransform.XProperty, moveAnim, HandoffBehavior.SnapshotAndReplace);
            }
        }

        if (vn_animation_compute_volume_hud_palette(
                VolumeHudTickCount,
                layout.ActiveTicks,
                layout.HighlightStartTick,
                _volumeHudMediaColor.R,
                _volumeHudMediaColor.G,
                _volumeHudMediaColor.B,
                _hasVolumeHudMediaTint ? (byte)1 : (byte)0,
                tickArgb,
                tickScaleY,
                VolumeHudTickCount) == 0)
        {
            bool activeChanged = _lastVolumeHudActiveTicks != layout.ActiveTicks;
            _lastVolumeHudActiveTicks = layout.ActiveTicks;

            Color fallbackAccent = _hasVolumeHudMediaTint ? _volumeHudMediaColor : Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF);
            Color startAccent = layout.ActiveTicks > 0
                ? Color.FromArgb(
                    (byte)(tickArgb[0] >> 24),
                    (byte)(tickArgb[0] >> 16),
                    (byte)(tickArgb[0] >> 8),
                    (byte)tickArgb[0])
                : fallbackAccent;
            UpdateSolidSpeakerIconState(layout.IconCodepoint, startAccent, animate && activeChanged);

            for (int i = 0; i < VolumeHudTickCount; i++)
            {
                uint argb = tickArgb[i];
                var targetColor = Color.FromArgb(
                    (byte)(argb >> 24),
                    (byte)(argb >> 16),
                    (byte)(argb >> 8),
                    (byte)argb);

                var brush = _volumeHudTickBrushes[i];
                if (brush.Color != targetColor)
                {
                    brush.Color = targetColor;
                }

                var scale = _volumeHudTickScales[i];
                double targetSy = tickScaleY[i];
                double currentSy = scale.ScaleY;
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleY = targetSy;
                if (animate && !AnimationConfig.ReduceMotion && Math.Abs(currentSy - targetSy) > 0.01)
                {
                    var syAnim = MakeAnim(currentSy, targetSy, _dur150, _easeAppleOut);
                    syAnim.FillBehavior = FillBehavior.Stop;
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, syAnim, HandoffBehavior.SnapshotAndReplace);
                }
            }
        }
    }

    private void PlayVolumeOverscrollSpring(double currentVolume, double stepDelta)
    {
        if (AnimationConfig.ReduceMotion || VolumeIndicatorContainer == null) return;
        if (vn_animation_compute_volume_overscroll_spring(currentVolume, stepDelta, out var over) != 0) return;
        if (Math.Abs(over.TranslateX) < 0.01) return;

        VolumeIndicatorContainer.RenderTransformOrigin = new Point(over.OriginX, 0.5);
        int fps = AnimationConfig.TargetFps;
        var durPeak = TimeSpan.FromMilliseconds(85);
        var durSettle = TimeSpan.FromMilliseconds(420);

        if (VolumeIndicatorContentScale != null)
        {
            var sx = new DoubleAnimationUsingKeyFrames();
            sx.KeyFrames.Add(new EasingDoubleKeyFrame(over.ScaleX, KeyTime.FromTimeSpan(durPeak), _easeQuadOut));
            sx.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(durSettle), _easeSoftSpring));
            Timeline.SetDesiredFrameRate(sx, fps);

            var sy = new DoubleAnimationUsingKeyFrames();
            sy.KeyFrames.Add(new EasingDoubleKeyFrame(over.ScaleY, KeyTime.FromTimeSpan(durPeak), _easeQuadOut));
            sy.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(durSettle), _easeSoftSpring));
            Timeline.SetDesiredFrameRate(sy, fps);

            VolumeIndicatorContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
            VolumeIndicatorContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
        }

        if (VolumeIndicatorContentTranslate != null)
        {
            var tx = new DoubleAnimationUsingKeyFrames();
            tx.KeyFrames.Add(new EasingDoubleKeyFrame(over.TranslateX, KeyTime.FromTimeSpan(durPeak), _easeQuadOut));
            tx.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(durSettle), _easeSoftSpring));
            Timeline.SetDesiredFrameRate(tx, fps);
            VolumeIndicatorContentTranslate.BeginAnimation(TranslateTransform.XProperty, tx);
        }

        if (NotchScale != null && NotchShadowScale != null)
        {
            double pillScaleX = 1.0 + (over.ScaleX - 1.0) * 0.55;
            double pillScaleY = 1.0 + (over.ScaleY - 1.0) * 0.55;
            var psx = new DoubleAnimationUsingKeyFrames();
            psx.KeyFrames.Add(new EasingDoubleKeyFrame(pillScaleX, KeyTime.FromTimeSpan(durPeak), _easeQuadOut));
            psx.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(durSettle), _easeSoftSpring));
            Timeline.SetDesiredFrameRate(psx, fps);

            var psy = new DoubleAnimationUsingKeyFrames();
            psy.KeyFrames.Add(new EasingDoubleKeyFrame(pillScaleY, KeyTime.FromTimeSpan(durPeak), _easeQuadOut));
            psy.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(durSettle), _easeSoftSpring));
            Timeline.SetDesiredFrameRate(psy, fps);

            NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, psx);
            NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, psy);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, psx);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, psy);
        }
    }

    private void VolumeIndicatorContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (VolumeIndicatorScale != null)
            UpdateVolumeIndicatorHud(VolumeIndicatorScale.ScaleX, animate: false);
    }

    private void SetVolumeFill(ScaleTransform scale, double volume, bool animate = false)
    {
        double target = Math.Clamp(volume, 0, 1);
        double current = scale.ScaleX;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.ScaleX = target;
        if (ReferenceEquals(scale, VolumeIndicatorScale))
        {
            UpdateVolumeIndicatorHud(target, animate);
        }
        if (!animate || AnimationConfig.ReduceMotion || Math.Abs(current - target) < 0.0001) return;

        // Retarget from the visible fill while keeping the final value on the
        // base property, so a drag or keyboard update can take over immediately.
        var animation = MakeAnim(current, target, _dur150, _easeAppleOut);
        animation.FillBehavior = FillBehavior.Stop;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void AdjustVolumeByScroll(int delta)
    {
        if (!_volumeSynced)
        {
            // Query baseline volume off UI thread to avoid cold COM query stalls,
            // applying volume state once known to prevent flicker.
            if (_volumeBaselinePending) return;
            _volumeBaselinePending = true;
            int version = ++_volumeBaselineVersion;

            _mediaVolumeWrites.Post("volume", () =>
            {
                bool ok = _mediaService.TryGetCurrentSessionVolume(out float vol, out bool _);
                Dispatcher.BeginInvoke(() =>
                {
                    if (version != _volumeBaselineVersion) return;
                    _volumeBaselinePending = false;
                    if (_isExpanded || _isAnimating ||
                        DateTime.UtcNow < _suppressCompactVolumeWheelUntilUtc) return;
                    if (!ok)
                    {
                        _volumeSynced = false;
                        ResetCompactVolumeWheelIntent();
                        RuntimeLog.Log(MediaCtrlLogTag, "No audio session found for wheel volume control.");
                        return;
                    }
                    _currentVolume = vol;
                    _volumeSynced = true;
                    ApplyVolumeStep(delta);
                });
            });
            return;
        }

        ApplyVolumeStep(delta);
    }

    private void ApplyVolumeStep(int delta)
    {
        _volumeInteractionVersion++;
        _volumeScrollSyncAfterUtc = AnimationConfig.ReduceMotion ? DateTime.MinValue : DateTime.UtcNow.Add(_dur150.TimeSpan);
        float step = (delta / 120f) * VolumeScrollStep;
        double priorVolume = _currentVolume;
        float newVolume = Math.Clamp(_currentVolume + step, 0f, 1f);
        _currentVolume = newVolume;

        ShowVolumeIndicator(newVolume);
        if ((priorVolume + step > 1.0001) || (priorVolume + step < -0.0001))
        {
            PlayVolumeOverscrollSpring(priorVolume, step);
        }

        QueueSessionVolumeWrite(newVolume, (float)priorVolume);

        if (_isExpanded)
        {
            SetVolumeFill(VolumeBarScale, newVolume, animate: true);
            UpdateVolumeIcon(newVolume, false);
        }
    }

    private void QueueSessionVolumeWrite(float volume, float fallbackVolume)
    {
        int version = _volumeInteractionVersion;
        _mediaVolumeWrites.Post("volume", () =>
        {
            if (_mediaService.TrySetCurrentSessionVolume(volume)) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (version != _volumeInteractionVersion) return;
                _currentVolume = fallbackVolume;
                SetVolumeFill(VolumeBarScale, fallbackVolume);
                UpdateVolumeIcon(fallbackVolume, false);
                DismissVolumeIndicatorImmediate(restoreMedia: true);
                RuntimeLog.Log(MediaCtrlLogTag, "Session volume write failed; refreshing audio state.");
                _pendingMuteRefreshVersion = version;
                _ = SyncVolumeFromActiveSession();
            });
        });
    }

    private void ShowVolumeIndicator(float volume)
    {
        if (VolumeIndicatorContainer == null || VolumeIndicatorFill == null) return;
        if (!_isMusicCompactMode) return;
        if (_isExpanded || _isAnimating) return;

        bool animateFill = _isVolumeIndicatorActive;

        if (!_isVolumeIndicatorActive)
        {
            if (!TryAcquireCompactSlot(VNotch.Controllers.CompactPillSlot.Volume, out int token))
            {
                return;
            }
            _volumeIndicatorToken = token;
        }

        if (!_isVolumeIndicatorActive)
        {
            _isVolumeIndicatorActive = true;
            ++_volumeIndicatorExitVersion;
            int transitionVersion = ++_volumeIndicatorTransitionVersion;
            _isVolumeIndicatorExiting = false;
            VolumeIndicatorContainer.IsHitTestVisible = true;
            int presentationToken = _volumeIndicatorToken;

            _hoverThumbnailDelayTimer.Stop();
            _compactThumbnailHoverLeaveTimer.Stop();

            // Cancel any hover stretch on the pill so the volume HUD stays crisp and unstretched
            NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            NotchScale.ScaleX = NotchScale.ScaleY = 1.0;
            NotchShadowScale.ScaleX = NotchShadowScale.ScaleY = 1.0;
            HoverGlow.BeginAnimation(OpacityProperty, null);
            HoverGlow.Opacity = 0;

            SuppressPrivacyDot();

            bool enableBlur = _settings.EnableBlurEffects && !AnimationConfig.ReduceMotion;
            if (vn_animation_compute_volume_hud_transition(
                    1,
                    _settings.EnableDynamicIslandMode ? (byte)1 : (byte)0,
                    enableBlur ? (byte)1 : (byte)0,
                    _collapsedWidth,
                    _collapsedHeight,
                    _cornerRadiusCollapsed,
                    out var trans) != 0)
            {
                trans = new VnVolumeHudTransitionParamsOut
                {
                    HudFromScaleX = 0.86,
                    HudFromScaleY = 0.78,
                    HudToScaleX = 1.0,
                    HudToScaleY = 1.0,
                    HudFromTranslateY = -6.0,
                    HudToTranslateY = 0.0,
                    HudFromBlur = enableBlur ? 10.0 : 0.0,
                    HudToBlur = 0.0,
                    MediaToScale = 0.76,
                    MediaToBlur = enableBlur ? 7.0 : 0.0,
                    TargetWidth = _collapsedWidth + VolumeHudExtraWidth,
                    TargetHeight = _collapsedHeight + VolumeHudExtraHeight,
                    TargetCornerRadius = _settings.EnableDynamicIslandMode ? (_collapsedHeight + VolumeHudExtraHeight) * 0.5 : Math.Max(_cornerRadiusCollapsed, 18.0),
                    MorphDurationMs = 350,
                    FadeDurationMs = 240
                };
            }

            double targetHeight = trans.TargetHeight;
            var morphDur = new Duration(TimeSpan.FromMilliseconds(trans.MorphDurationMs));
            var fadeDur = new Duration(TimeSpan.FromMilliseconds(trans.FadeDurationMs));

            double fallbackFromHeight = double.IsFinite(NotchBorder.Height) && NotchBorder.Height > 0 ? NotchBorder.Height : _collapsedHeight;
            double fromHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : fallbackFromHeight;
            if (_isCompactThumbnailHovered)
            {
                _isCompactThumbnailHovered = false;

                NotchBorder.BeginAnimation(HeightProperty, null);
                AnimateCompactWidth(trans.TargetWidth, _dur400, _easeExpOut6, _volumeIndicatorToken);
                var heightAnim = MakeAnim(fromHeight, targetHeight, _dur400, _easeExpOut6, VNotch.Services.AnimationConfig.TargetFps);
                NotchBorder.BeginAnimation(HeightProperty, heightAnim);

                CompactHoverInfo.BeginAnimation(OpacityProperty, null);
                var hoverFadeOut = MakeAnim(0.0, _dur200, _easeQuadOut);
                hoverFadeOut.Completed += (s, e) => CompactHoverInfo.Visibility = Visibility.Collapsed;
                CompactHoverInfo.BeginAnimation(OpacityProperty, hoverFadeOut);

                AnimateCornerRadius(trans.TargetCornerRadius, TimeSpan.FromMilliseconds(400));
            }
            else
            {
                AnimateCompactWidth(trans.TargetWidth, morphDur, _easeExpOut6, _volumeIndicatorToken);
                NotchBorder.BeginAnimation(HeightProperty, null);
                var heightAnim = MakeAnim(fromHeight, targetHeight, morphDur, _easeExpOut6, VNotch.Services.AnimationConfig.TargetFps);
                NotchBorder.BeginAnimation(HeightProperty, heightAnim);
                AnimateCornerRadius(trans.TargetCornerRadius, morphDur.TimeSpan);
            }

            MusicViz.BeginAnimation(OpacityProperty, null);
            var vizOut = MakeAnim(1.0, 0.0, _dur200, _easeQuadOut);
            vizOut.Completed += (s, e) =>
            {
                if (_isVolumeIndicatorActive && !IsCompactSlotStale(presentationToken))
                    MusicViz.Visibility = Visibility.Collapsed;
            };
            MusicViz.BeginAnimation(OpacityProperty, vizOut);

            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
            if (!AnimationConfig.ReduceMotion)
            {
                var thumbShrink = MakeAnim(trans.MediaToScale, _dur200, _easeQuadOut, VNotch.Services.AnimationConfig.TargetFps);
                CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, thumbShrink);
                CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, thumbShrink);
                if (enableBlur && MusicCompactContentBlur != null)
                {
                    var thumbBlurOut = MakeAnim(0.0, trans.MediaToBlur, _dur200, _easeQuadOut, VNotch.Services.AnimationConfig.TargetFps);
                    MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, thumbBlurOut);
                }
            }
            var thumbOut = MakeAnim(1.0, 0.0, _dur200, _easeQuadOut);
            thumbOut.Completed += (s, e) =>
            {
                if (_isVolumeIndicatorActive && !IsCompactSlotStale(presentationToken))
                {
                    CompactThumbnailBorder.Visibility = Visibility.Collapsed;
                    if (MusicCompactContentBlur != null)
                    {
                        MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
                        MusicCompactContentBlur.Radius = 0;
                    }
                }
            };
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, thumbOut);

            VolumeIndicatorContainer.Visibility = Visibility.Visible;
            VolumeIndicatorContainer.Opacity = 0;
            VolumeIndicatorContainer.BeginAnimation(OpacityProperty, null);
            var indicatorIn = MakeAnim(0.0, 1.0, fadeDur, _easeExpOut6);
            VolumeIndicatorContainer.BeginAnimation(OpacityProperty, indicatorIn);

            if (!AnimationConfig.ReduceMotion)
            {
                VolumeIndicatorContainer.RenderTransformOrigin = new Point(0.5, 0.5);
                if (VolumeIndicatorContentScale != null)
                {
                    var scaleInX = MakeAnim(trans.HudFromScaleX, trans.HudToScaleX, morphDur, _easeSoftSpring);
                    var scaleInY = MakeAnim(trans.HudFromScaleY, trans.HudToScaleY, morphDur, _easeSoftSpring);
                    VolumeIndicatorContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleInX);
                    VolumeIndicatorContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleInY);
                }
                if (VolumeIndicatorContentTranslate != null)
                {
                    VolumeIndicatorContentTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                    VolumeIndicatorContentTranslate.X = 0;
                    var slideInY = MakeAnim(trans.HudFromTranslateY, trans.HudToTranslateY, morphDur, _easeExpOut6);
                    VolumeIndicatorContentTranslate.BeginAnimation(TranslateTransform.YProperty, slideInY);
                }
                if (enableBlur && trans.HudFromBlur > 0.1)
                {
                    _volumeIndicatorTransitionBlur ??= new BlurEffect { RenderingBias = RenderingBias.Performance };
                    _volumeIndicatorTransitionBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
                    _volumeIndicatorTransitionBlur.Radius = trans.HudFromBlur;
                    VolumeIndicatorContainer.Effect = _volumeIndicatorTransitionBlur;
                    var blurIn = MakeAnim(trans.HudFromBlur, 0.0, _dur250, _easeExpOut6, VNotch.Services.AnimationConfig.TargetFps);
                    blurIn.Completed += (_, _) =>
                    {
                        if (transitionVersion == _volumeIndicatorTransitionVersion)
                        {
                            ClearVolumeIndicatorTransitionEffects();
                        }
                    };
                    _volumeIndicatorTransitionBlur.BeginAnimation(BlurEffect.RadiusProperty, blurIn);
                }
                else
                {
                    ClearVolumeIndicatorTransitionEffects();
                }

                EnsureVolumeIndicatorTicks();
                if (_volumeHudTickScales != null)
                {
                    for (int i = 0; i < VolumeHudTickCount; i++)
                    {
                        _volumeHudTickScales[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
                        _volumeHudTickScales[i].ScaleY = 0.28;
                    }
                    animateFill = true;
                }
            }
            else
            {
                ClearVolumeIndicatorTransitionEffects();
            }
        }

        SetVolumeFill(VolumeIndicatorScale, volume, animate: animateFill);

        if (_volumeIndicatorHideTimer == null)
        {
            _volumeIndicatorHideTimer = new UiScheduledTimer
            {
                Interval = TimeSpan.FromMilliseconds(1200)
            };
            _volumeIndicatorHideTimer.Tick += (s, e) =>
            {
                _volumeIndicatorHideTimer.Stop();
                ResetCompactVolumeWheelIntent();
                HideVolumeIndicator();
            };
        }
        _volumeIndicatorHideTimer.Stop();
        _volumeIndicatorHideTimer.Start();
    }
    private void HideVolumeIndicator()
    {
        if (VolumeIndicatorContainer == null) return;
        if (!_isVolumeIndicatorActive || IsCompactSlotStale(_volumeIndicatorToken)) return;
        ResetCompactVolumeWheelIntent();
        int token = _volumeIndicatorToken;
        int exitVersion = ++_volumeIndicatorExitVersion;
        int transitionVersion = ++_volumeIndicatorTransitionVersion;

        _isVolumeIndicatorActive = false;
        _volumeSynced = false;
        _compactPillArbiter.Release(token);
        _volumeIndicatorToken = 0;

        RestorePrivacyDotVisibility();

        if (_isExpanded || _isAnimating)
        {
            _isVolumeIndicatorExiting = false;
            ClearVolumeIndicatorTransitionEffects();
            VolumeIndicatorContainer.BeginAnimation(OpacityProperty, null);
            VolumeIndicatorContainer.Opacity = 0;
            VolumeIndicatorContainer.Visibility = Visibility.Collapsed;
            return;
        }

        _isVolumeIndicatorExiting = true;
        VolumeIndicatorContainer.IsHitTestVisible = false;

        bool enableBlur = _settings.EnableBlurEffects && !AnimationConfig.ReduceMotion;
        if (vn_animation_compute_volume_hud_transition(
                0,
                _settings.EnableDynamicIslandMode ? (byte)1 : (byte)0,
                enableBlur ? (byte)1 : (byte)0,
                _collapsedWidth,
                _collapsedHeight,
                _cornerRadiusCollapsed,
                out var trans) != 0)
        {
            trans = new VnVolumeHudTransitionParamsOut
            {
                HudFromScaleX = 1.0,
                HudFromScaleY = 1.0,
                HudToScaleX = 0.88,
                HudToScaleY = 0.76,
                HudFromTranslateY = 0.0,
                HudToTranslateY = -5.5,
                HudFromBlur = 0.0,
                HudToBlur = enableBlur ? 9.0 : 0.0,
                MediaToScale = 1.0,
                MediaToBlur = 0.0,
                TargetWidth = _collapsedWidth,
                TargetHeight = _collapsedHeight,
                TargetCornerRadius = _cornerRadiusCollapsed,
                MorphDurationMs = 340,
                FadeDurationMs = 220
            };
        }

        var morphDur = new Duration(TimeSpan.FromMilliseconds(trans.MorphDurationMs));
        var fadeDur = new Duration(TimeSpan.FromMilliseconds(trans.FadeDurationMs));

        AnimateCompactWidth(trans.TargetWidth, morphDur, _easeExpOut6, 0);
        double fallbackFromHeight = double.IsFinite(NotchBorder.Height) && NotchBorder.Height > 0 ? NotchBorder.Height : _collapsedHeight + VolumeHudExtraHeight;
        double fromHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : fallbackFromHeight;
        NotchBorder.BeginAnimation(HeightProperty, null);
        NotchBorder.Height = trans.TargetHeight;
        var heightAnim = MakeAnim(fromHeight, trans.TargetHeight, morphDur, _easeExpOut6, VNotch.Services.AnimationConfig.TargetFps);
        heightAnim.FillBehavior = FillBehavior.Stop;
        NotchBorder.BeginAnimation(HeightProperty, heightAnim);
        AnimateCornerRadius(trans.TargetCornerRadius, morphDur.TimeSpan);

        if (!AnimationConfig.ReduceMotion)
        {
            VolumeIndicatorContainer.RenderTransformOrigin = new Point(0.5, 0.5);
            if (VolumeIndicatorContentScale != null)
            {
                var outSx = MakeAnim(trans.HudFromScaleX, trans.HudToScaleX, fadeDur, _easeQuadOut);
                var outSy = MakeAnim(trans.HudFromScaleY, trans.HudToScaleY, fadeDur, _easeQuadOut);
                VolumeIndicatorContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, outSx);
                VolumeIndicatorContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, outSy);
            }
            if (VolumeIndicatorContentTranslate != null)
            {
                var outTy = MakeAnim(trans.HudFromTranslateY, trans.HudToTranslateY, fadeDur, _easeQuadOut);
                VolumeIndicatorContentTranslate.BeginAnimation(TranslateTransform.YProperty, outTy);
            }
            if (enableBlur && trans.HudToBlur > 0.1)
            {
                _volumeIndicatorTransitionBlur ??= new BlurEffect { RenderingBias = RenderingBias.Performance };
                _volumeIndicatorTransitionBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
                _volumeIndicatorTransitionBlur.Radius = 0.0;
                VolumeIndicatorContainer.Effect = _volumeIndicatorTransitionBlur;
                var blurOut = MakeAnim(0.0, trans.HudToBlur, fadeDur, _easeQuadOut, VNotch.Services.AnimationConfig.TargetFps);
                _volumeIndicatorTransitionBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOut);
            }
        }

        VolumeIndicatorContainer.BeginAnimation(OpacityProperty, null);
        var fadeOut = MakeAnim(1.0, 0.0, fadeDur, _easeQuadOut);
        fadeOut.Completed += (s, e) =>
        {
            if (exitVersion != _volumeIndicatorExitVersion) return;
            _isVolumeIndicatorExiting = false;
            if (!_isVolumeIndicatorActive)
            {
                if (transitionVersion == _volumeIndicatorTransitionVersion)
                {
                    ClearVolumeIndicatorTransitionEffects();
                }
                VolumeIndicatorContainer.Visibility = Visibility.Collapsed;
                VolumeIndicatorContainer.IsHitTestVisible = true;
                if (VolumeIndicatorContentScale != null)
                {
                    VolumeIndicatorContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    VolumeIndicatorContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    VolumeIndicatorContentScale.ScaleX = VolumeIndicatorContentScale.ScaleY = 1.0;
                }
                if (VolumeIndicatorContentTranslate != null)
                {
                    VolumeIndicatorContentTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                    VolumeIndicatorContentTranslate.Y = 0;
                }
            }
        };
        VolumeIndicatorContainer.BeginAnimation(OpacityProperty, fadeOut);

        if (!_isClipboardPeekActive)
        {
            CompactThumbnailBorder.Visibility = Visibility.Visible;
            CompactThumbnailBorder.Opacity = 0;
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
            var thumbIn = MakeAnim(0.0, 1.0, _dur250, _easeExpOut6);
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, thumbIn);
            if (!AnimationConfig.ReduceMotion)
            {
                var thumbGrow = MakeAnim(0.76, 1.0, morphDur, _easeSoftSpring, VNotch.Services.AnimationConfig.TargetFps);
                CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, thumbGrow);
                CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, thumbGrow);
                if (enableBlur && MusicCompactContentBlur != null)
                {
                    var thumbBlurIn = MakeAnim(6.0, 0.0, _dur250, _easeExpOut6, VNotch.Services.AnimationConfig.TargetFps);
                    thumbBlurIn.Completed += (_, _) =>
                    {
                        MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
                        MusicCompactContentBlur.Radius = 0;
                    };
                    MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, thumbBlurIn);
                }
            }
            else
            {
                CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                CompactThumbnailScale.ScaleX = CompactThumbnailScale.ScaleY = 1.0;
            }
        }

        if (!_isClipboardPeekActive)
        {
            ShowMusicVisualizer(duration: _dur150);
        }
    }

    private bool _isVolumeIndicatorExiting;
    private int _volumeIndicatorExitVersion;

    private void DismissVolumeIndicatorImmediate(bool restoreMedia = false, bool animateExit = false)
    {
        int exitVersion = ++_volumeIndicatorExitVersion;
        int transitionVersion = ++_volumeIndicatorTransitionVersion;
        _isVolumeIndicatorExiting = animateExit;
        _volumeIndicatorHideTimer?.Stop();
        ResetCompactVolumeWheelIntent();
        int token = _volumeIndicatorToken;

        _isVolumeIndicatorActive = false;
        _volumeSynced = false;
        _isDraggingVolumeIndicator = false;
        _compactPillArbiter.Release(token);
        _volumeIndicatorToken = 0;

        if (!_isExpanded && !_isAnimating && NotchBorder != null)
        {
            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Height = _collapsedHeight;
        }

        if (VolumeIndicatorContainer != null)
        {
            VolumeIndicatorContainer.ReleaseMouseCapture();
            double liveOpacity = VolumeIndicatorContainer.Opacity;
            VolumeIndicatorContainer.BeginAnimation(OpacityProperty, null);
            VolumeIndicatorContainer.Opacity = 0;
            if (animateExit && VolumeIndicatorContainer.Visibility == Visibility.Visible)
            {
                VolumeIndicatorContainer.IsHitTestVisible = false;
                if (!AnimationConfig.ReduceMotion)
                {
                    if (VolumeIndicatorContentScale != null)
                    {
                        var outSx = MakeAnim(1.0, 0.88, _dur200, _easeQuadOut);
                        var outSy = MakeAnim(1.0, 0.76, _dur200, _easeQuadOut);
                        VolumeIndicatorContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, outSx);
                        VolumeIndicatorContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, outSy);
                    }
                    if (_settings.EnableBlurEffects)
                    {
                        _volumeIndicatorTransitionBlur ??= new BlurEffect { RenderingBias = RenderingBias.Performance };
                        _volumeIndicatorTransitionBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
                        _volumeIndicatorTransitionBlur.Radius = 0.0;
                        VolumeIndicatorContainer.Effect = _volumeIndicatorTransitionBlur;
                        var blurOut = MakeAnim(0.0, 8.0, _dur200, _easeQuadOut, VNotch.Services.AnimationConfig.TargetFps);
                        _volumeIndicatorTransitionBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOut);
                    }
                }
                var fadeOut = MakeAnim(liveOpacity, 0.0, _dur250, _easeQuadOut);
                fadeOut.Completed += (_, _) =>
                {
                    if (exitVersion != _volumeIndicatorExitVersion) return;
                    _isVolumeIndicatorExiting = false;
                    if (transitionVersion == _volumeIndicatorTransitionVersion)
                    {
                        ClearVolumeIndicatorTransitionEffects();
                    }
                    VolumeIndicatorContainer.Visibility = Visibility.Collapsed;
                    VolumeIndicatorContainer.BeginAnimation(OpacityProperty, null);
                    VolumeIndicatorContainer.IsHitTestVisible = true;
                };
                VolumeIndicatorContainer.BeginAnimation(OpacityProperty, fadeOut);
            }
            else
            {
                _isVolumeIndicatorExiting = false;
                ClearVolumeIndicatorTransitionEffects();
                VolumeIndicatorContainer.Visibility = Visibility.Collapsed;
                VolumeIndicatorContainer.IsHitTestVisible = true;
            }
        }

        if (CompactThumbnailBorder != null)
        {
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
        }
        if (MusicViz != null)
        {
            MusicViz.BeginAnimation(OpacityProperty, null);
        }

        RestorePrivacyDotVisibility();
        if (restoreMedia) RestoreCompactMediaPresentation();
    }

    #region Volume Indicator Drag

    private bool _isDraggingVolumeIndicator = false;

    private void VolumeIndicator_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _isDraggingVolumeIndicator = true;
        VolumeIndicatorContainer.CaptureMouse();
        SetVolumeFromIndicatorPosition(e);

        _volumeIndicatorHideTimer?.Stop();
    }

    private void VolumeIndicator_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingVolumeIndicator || e.LeftButton != MouseButtonState.Pressed) return;
        e.Handled = true;
        SetVolumeFromIndicatorPosition(e);
    }

    private void VolumeIndicator_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingVolumeIndicator) return;
        e.Handled = true;
        _isDraggingVolumeIndicator = false;
        VolumeIndicatorContainer.ReleaseMouseCapture();

        _volumeIndicatorHideTimer?.Stop();
        _volumeIndicatorHideTimer?.Start();
    }

    private void SetVolumeFromIndicatorPosition(MouseEventArgs e)
    {
        _volumeInteractionVersion++;
        var pos = e.GetPosition(VolumeIndicatorContainer);
        double containerWidth = VolumeIndicatorContainer.ActualWidth;
        if (containerWidth <= 0) containerWidth = (_collapsedWidth + VolumeHudExtraWidth) - 28;

        double position = FlowDirection == FlowDirection.RightToLeft ? containerWidth - pos.X : pos.X;
        double rawRatio = position / containerWidth;
        if (rawRatio > 1.02 || rawRatio < -0.02)
        {
            PlayVolumeOverscrollSpring(Math.Clamp(rawRatio, 0.0, 1.0), rawRatio > 1.0 ? (rawRatio - 1.0) : rawRatio);
        }
        float newVolume = (float)Math.Clamp(rawRatio, 0.0, 1.0);
        float priorVolume = _currentVolume;
        _currentVolume = newVolume;
        SetVolumeFill(VolumeIndicatorScale, newVolume, animate: true);

        QueueSessionVolumeWrite(newVolume, priorVolume);

        if (_isExpanded)
        {
            SetVolumeFill(VolumeBarScale, newVolume);
            UpdateVolumeIcon(newVolume, false);
        }
    }

    #endregion

    private void VolumeIcon_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        int request = ++_volumeInteractionVersion;
        // Every click is a toggle, so retain each one while serializing driver
        // calls. Concurrent read/modify/write operations can otherwise lose clicks.
        _mediaVolumeWrites.Post($"mute:{request}", () =>
        {
            if (!_mediaService.TryToggleCurrentSessionMute()) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _volumeInteractionVersion++;
                if (_volumeReadInFlight)
                    _pendingMuteRefreshVersion = _volumeInteractionVersion;
                else
                    _ = SyncVolumeFromActiveSession();
            }));
        });
    }

    private void VolumeIcon_MouseEnter(object sender, MouseEventArgs e)
    {
        var animX = new DoubleAnimation(1, 1.2, _dur150) { EasingFunction = _easeQuadOut };
        var animY = new DoubleAnimation(1, 1.2, _dur150) { EasingFunction = _easeQuadOut };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animX, VNotch.Services.AnimationConfig.TargetFps);
        VolumeIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animY, VNotch.Services.AnimationConfig.TargetFps);
        VolumeIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    private void VolumeIcon_MouseLeave(object sender, MouseEventArgs e)
    {
        var animX = new DoubleAnimation(VolumeIconScale.ScaleX, 1, _dur150) { EasingFunction = _easeQuadOut };
        var animY = new DoubleAnimation(VolumeIconScale.ScaleY, 1, _dur150) { EasingFunction = _easeQuadOut };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animX, VNotch.Services.AnimationConfig.TargetFps);
        VolumeIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animY, VNotch.Services.AnimationConfig.TargetFps);
        VolumeIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    private void VolumeBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _isDraggingVolume = true;
        VolumeBarContainer.CaptureMouse();
        SetVolumeFromMousePosition(e);
    }

    private void VolumeBar_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (e.Delta == 0 || _isDraggingVolume || _isAnimating) return;
        ApplyVolumeStep(e.Delta);
    }

    private void VolumeBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingVolume && e.LeftButton == MouseButtonState.Pressed)
        {
            SetVolumeFromMousePosition(e);
        }
    }

    private void VolumeBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingVolume)
        {
            _isDraggingVolume = false;
            VolumeBarContainer.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void SetVolumeFromMousePosition(MouseEventArgs e)
    {
        _volumeInteractionVersion++;
        const double volumeBarWidth = 100.0;
        var pos = e.GetPosition(VolumeBarContainer);
        float newVolume = (float)Math.Clamp(pos.X / volumeBarWidth, 0.0, 1.0);

        float priorVolume = _currentVolume;
        _currentVolume = newVolume;
        SetVolumeFill(VolumeBarScale, newVolume);
        UpdateVolumeIcon(newVolume, false);

        QueueSessionVolumeWrite(newVolume, priorVolume);
    }

    private bool _volumeReadInFlight;
    private int? _pendingMuteRefreshVersion;
    private int _volumeInteractionVersion;

    private async Task SyncVolumeFromActiveSession()
    {
        if (_isDraggingVolume || _volumeReadInFlight) return;

        _volumeReadInFlight = true;
        int version = _volumeInteractionVersion;
        try
        {
            var result = await Task.Run(() =>
            {
                bool success = _mediaService.TryGetCurrentSessionVolume(out float volume, out bool muted);
                return (success, volume, muted);
            });
            if (_isDraggingVolume || version != _volumeInteractionVersion) return;

            if (result.success)
            {
                _currentVolume = result.volume;
                SetVolumeFill(VolumeBarScale, _currentVolume);
                UpdateVolumeIcon(_currentVolume, result.muted);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(MediaCtrlLogTag, ex, "Read session volume failed");
        }
        finally
        {
            _volumeReadInFlight = false;
            int? pendingVersion = _pendingMuteRefreshVersion;
            _pendingMuteRefreshVersion = null;
            if (pendingVersion == _volumeInteractionVersion)
                _ = SyncVolumeFromActiveSession();
        }
    }

    private void UpdateVolumeIcon(float volume, bool isMuted)
    {
        _viewModel.UpdateVolumeState(volume, isMuted);
    }

    #endregion
}
