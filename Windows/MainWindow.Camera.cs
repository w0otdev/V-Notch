using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using VNotch.Controllers;
using VNotch.Services;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Camera Logic

    private readonly WebcamCaptureController _camera = new();

    private bool _cameraPreviewMorphPending = false;
    private bool _isCameraSectionAnimating = false;
    private const int CameraSectionExpandDurationMs = 420;
    private const int CameraSectionCollapseDurationMs = 420;

    private WriteableBitmap? _cameraWriteableBitmap;
    private bool _cameraFrameDispatchPending = false;

    private bool _isCameraActive => _camera.IsActive;
    private bool IsCameraPreviewLifecycleActive => _camera.IsLifecycleActive;

    private void InitializeCameraController()
    {
        _camera.FrameAvailable += OnCameraFrameAvailable;
    }

    private void PrimeCameraPreviewMorphIn()
    {
        _cameraPreviewMorphPending = true;

        CameraPreviewImage.BeginAnimation(OpacityProperty, null);
        CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CameraPreviewBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        CameraOverlay.BeginAnimation(OpacityProperty, null);

        CameraPreviewImage.Opacity = 0.0;
        CameraPreviewScale.ScaleX = 1.06;
        CameraPreviewScale.ScaleY = 1.06;
        CameraPreviewBlur.Radius = _settings.EnableBlurEffects ? 16.0 : 0.0;

        CameraOverlay.Visibility = Visibility.Visible;
        CameraOverlay.Opacity = 1.0;
    }

    private void AnimateCameraPreviewMorphIn()
    {
        if (!_cameraPreviewMorphPending) return;
        _cameraPreviewMorphPending = false;

        int token = _camera.FadeToken;
        var morphDuration = new Duration(TimeSpan.FromMilliseconds(380));
        var overlayDuration = new Duration(TimeSpan.FromMilliseconds(320));

        var previewFadeIn = MakeAnim(CameraPreviewImage.Opacity, 0.8, morphDuration, _easeExpOut6, null);
        var scaleXIn = MakeAnim(CameraPreviewScale.ScaleX, 1.0, morphDuration, _easeExpOut6, null);
        var scaleYIn = MakeAnim(CameraPreviewScale.ScaleY, 1.0, morphDuration, _easeExpOut6, null);
        var blurClear = MakeAnim(CameraPreviewBlur.Radius, 0.0, morphDuration, _easeExpOut6, null);

        var overlayFadeOut = MakeAnim(CameraOverlay.Opacity, 0.0, overlayDuration, _easeQuadOut, TimeSpan.FromMilliseconds(40));
        overlayFadeOut.Completed += (s, e) =>
        {
            if (token != _camera.FadeToken || !_isCameraActive) return;
            CameraOverlay.BeginAnimation(OpacityProperty, null);
            CameraOverlay.Visibility = Visibility.Collapsed;
            CameraOverlay.Opacity = 0.0;
        };

        CameraPreviewImage.BeginAnimation(OpacityProperty, previewFadeIn, HandoffBehavior.SnapshotAndReplace);
        CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXIn, HandoffBehavior.SnapshotAndReplace);
        CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYIn, HandoffBehavior.SnapshotAndReplace);
        CameraPreviewBlur.BeginAnimation(BlurEffect.RadiusProperty, blurClear, HandoffBehavior.SnapshotAndReplace);
        CameraOverlay.BeginAnimation(OpacityProperty, overlayFadeOut, HandoffBehavior.SnapshotAndReplace);
    }

    private double ComputeCameraCornerRadius(bool expandedToShelf)
    {
        return expandedToShelf ? 16.0 : 12.0;
    }

    private void ApplyCameraCornerRadius(bool expandedToShelf)
    {
        double radius = ComputeCameraCornerRadius(expandedToShelf);

        double w = CameraSection.ActualWidth;
        double h = CameraSection.ActualHeight;
        if (w <= 0 || h <= 0) return;

        CameraSection.CornerRadius = new CornerRadius(radius);
        var clipRect = new RectangleGeometry(new System.Windows.Rect(0, 0, w, h), radius, radius);
        CameraSection.Clip = clipRect;
    }

    private void AnimateCameraSectionToShelf(bool expand)
    {
        if (!_isSecondaryView || _isAnimating) return;
        if (_isCameraSectionAnimating) return;
        if (expand == _isCameraSectionExpanded) return;

        int token = ++_cameraSectionAnimToken;
        _isCameraSectionAnimating = true;
        var duration = new Duration(TimeSpan.FromMilliseconds(expand ? CameraSectionExpandDurationMs : CameraSectionCollapseDurationMs));
        var easing = (IEasingFunction)_easeExpOut6;

        CameraSection.BeginAnimation(WidthProperty, null);
        CameraSection.BeginAnimation(HeightProperty, null);
        CameraSection.BeginAnimation(MarginProperty, null);
        CameraSectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CameraSectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        FileShelf.BeginAnimation(OpacityProperty, null);
        ApplyCameraCornerRadius(expand);

        if (expand)
        {
            double compactWidth = CameraSection.ActualWidth;
            if (compactWidth <= 1)
            {
                compactWidth = _cameraSectionCompactWidth > 1 ? _cameraSectionCompactWidth : 120;
            }
            double compactHeight = CameraSection.ActualHeight;
            if (compactHeight <= 1)
            {
                compactHeight = _cameraSectionCompactHeight > 1 ? _cameraSectionCompactHeight : 100;
            }
            _cameraSectionCompactWidth = compactWidth;
            _cameraSectionCompactHeight = compactHeight;
            _cameraSectionCompactMargin = CameraSection.Margin;

            double targetWidth = SecondaryContent.ActualWidth;
            if (targetWidth <= 1)
            {
                SecondaryContent.UpdateLayout();
                targetWidth = SecondaryContent.ActualWidth;
            }
            if (targetWidth <= 1)
            {
                double shelfWidth = FileShelf.ActualWidth;
                targetWidth = shelfWidth > 1 ? (shelfWidth + compactWidth) : Math.Max(compactWidth, 320);
            }
            targetWidth = Math.Max(compactWidth, targetWidth);

            double targetHeight = targetWidth;

            double notchTargetHeight = targetHeight + 30 + 12 + 2;
            double currentNotchHeight = _expandedHeight;

            double newWindowHeightDip = notchTargetHeight + 80;
            _overlayWindow.ResizeHeight(newWindowHeightDip);

            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Height = currentNotchHeight;
            var notchHeightAnim = MakeAnim(currentNotchHeight, notchTargetHeight, duration, easing, null);
            NotchBorder.BeginAnimation(HeightProperty, notchHeightAnim, HandoffBehavior.SnapshotAndReplace);

            FileShelf.Visibility = Visibility.Visible;
            FileShelf.IsHitTestVisible = false;

            CameraSection.Width = compactWidth;
            CameraSection.Height = compactHeight;
            CameraSection.VerticalAlignment = VerticalAlignment.Top;
            CameraSection.HorizontalAlignment = HorizontalAlignment.Left;
            CameraSection.Margin = _cameraSectionCompactMargin;
            Grid.SetColumn(CameraSection, 0);
            Grid.SetColumnSpan(CameraSection, 2);
            Panel.SetZIndex(CameraSection, 10);

            var widthAnim = MakeAnim(compactWidth, targetWidth, duration, easing, null);
            var heightAnim = MakeAnim(compactHeight, targetHeight, duration, easing, null);
            var marginAnim = new ThicknessAnimation(CameraSection.Margin, new Thickness(0, 0, 0, 0), duration)
            {
                EasingFunction = easing
            };
            Timeline.SetDesiredFrameRate(marginAnim, VNotch.Services.AnimationConfig.TargetFps);

            var shelfFadeOut = MakeAnim(FileShelf.Opacity, 0.0, new Duration(TimeSpan.FromMilliseconds(220)), _easeQuadOut, null);

            var squashX = new DoubleAnimationUsingKeyFrames { Duration = duration };
            squashX.KeyFrames.Add(new EasingDoubleKeyFrame(CameraSectionScale.ScaleX, KeyTime.FromPercent(0.0)));
            squashX.KeyFrames.Add(new EasingDoubleKeyFrame(0.985, KeyTime.FromPercent(0.34), _easeSineInOut));
            squashX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), _easeSineInOut));
            Timeline.SetDesiredFrameRate(squashX, VNotch.Services.AnimationConfig.TargetFps);

            var squashY = new DoubleAnimationUsingKeyFrames { Duration = duration };
            squashY.KeyFrames.Add(new EasingDoubleKeyFrame(CameraSectionScale.ScaleY, KeyTime.FromPercent(0.0)));
            squashY.KeyFrames.Add(new EasingDoubleKeyFrame(0.96, KeyTime.FromPercent(0.34), _easeSineInOut));
            squashY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), _easeSineInOut));
            Timeline.SetDesiredFrameRate(squashY, VNotch.Services.AnimationConfig.TargetFps);

            widthAnim.Completed += (s, e) =>
            {
                if (token != _cameraSectionAnimToken) return;
                _isCameraSectionExpanded = true;
                _isCameraSectionAnimating = false;
                CameraSection.BeginAnimation(WidthProperty, null);
                CameraSection.BeginAnimation(HeightProperty, null);
                CameraSection.Width = targetWidth;
                CameraSection.Height = targetHeight;
                CameraSection.BeginAnimation(MarginProperty, null);
                CameraSection.Margin = new Thickness(0);
                CameraSectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                CameraSectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                CameraSectionScale.ScaleX = 1.0;
                CameraSectionScale.ScaleY = 1.0;
                ApplyCameraCornerRadius(true);
                FileShelf.BeginAnimation(OpacityProperty, null);
                FileShelf.Opacity = 0;
                FileShelf.Visibility = Visibility.Collapsed;

                NotchBorder.BeginAnimation(HeightProperty, null);
                NotchBorder.Height = notchTargetHeight;
            };

            CameraSection.BeginAnimation(WidthProperty, widthAnim, HandoffBehavior.SnapshotAndReplace);
            CameraSection.BeginAnimation(HeightProperty, heightAnim, HandoffBehavior.SnapshotAndReplace);
            CameraSection.BeginAnimation(MarginProperty, marginAnim, HandoffBehavior.SnapshotAndReplace);
            CameraSectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, squashX, HandoffBehavior.SnapshotAndReplace);
            CameraSectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, squashY, HandoffBehavior.SnapshotAndReplace);
            FileShelf.BeginAnimation(OpacityProperty, shelfFadeOut, HandoffBehavior.SnapshotAndReplace);
            return;
        }

        double currentWidth = CameraSection.ActualWidth;
        if (currentWidth <= 1)
        {
            currentWidth = (double.IsNaN(CameraSection.Width) || CameraSection.Width <= 1)
                ? (_cameraSectionCompactWidth > 1 ? _cameraSectionCompactWidth : 120)
                : CameraSection.Width;
        }
        double collapsedWidth = _cameraSectionCompactWidth > 1 ? _cameraSectionCompactWidth : currentWidth;

        double currentHeight = CameraSection.ActualHeight;
        if (currentHeight <= 1)
        {
            currentHeight = (double.IsNaN(CameraSection.Height) || CameraSection.Height <= 1)
                ? (_cameraSectionCompactHeight > 1 ? _cameraSectionCompactHeight : 100)
                : CameraSection.Height;
        }
        double collapsedHeight = _cameraSectionCompactHeight > 1 ? _cameraSectionCompactHeight : currentHeight;

        FileShelf.Visibility = Visibility.Visible;
        FileShelf.IsHitTestVisible = false;
        CameraSection.Width = currentWidth;
        CameraSection.Height = currentHeight;
        CameraSection.VerticalAlignment = VerticalAlignment.Top;
        CameraSection.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(CameraSection, 0);
        Grid.SetColumnSpan(CameraSection, 2);
        Panel.SetZIndex(CameraSection, 10);

        double collapseNotchHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _expandedHeight;
        NotchBorder.BeginAnimation(HeightProperty, null);
        var notchHeightCollapseAnim = MakeAnim(collapseNotchHeight, _expandedHeight, duration, easing, null);
        notchHeightCollapseAnim.Completed += (s, e) =>
        {
            if (token != _cameraSectionAnimToken) return;
            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Height = _expandedHeight;
            double windowHeightDip = _expandedHeight + 80;
            _overlayWindow.ResizeHeight(windowHeightDip);
        };
        NotchBorder.BeginAnimation(HeightProperty, notchHeightCollapseAnim, HandoffBehavior.SnapshotAndReplace);

        var widthCollapseAnim = MakeAnim(currentWidth, collapsedWidth, duration, easing, null);
        var heightCollapseAnim = MakeAnim(currentHeight, collapsedHeight, duration, easing, null);
        var marginCollapseAnim = new ThicknessAnimation(CameraSection.Margin, _cameraSectionCompactMargin, duration)
        {
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(marginCollapseAnim, VNotch.Services.AnimationConfig.TargetFps);

        var shelfFadeIn = MakeAnim(FileShelf.Opacity, 1.0, new Duration(TimeSpan.FromMilliseconds(260)), _easePowerOut3, null);

        var settleX = new DoubleAnimationUsingKeyFrames { Duration = duration };
        settleX.KeyFrames.Add(new EasingDoubleKeyFrame(CameraSectionScale.ScaleX, KeyTime.FromPercent(0.0)));
        settleX.KeyFrames.Add(new EasingDoubleKeyFrame(0.99, KeyTime.FromPercent(0.36), _easeSineInOut));
        settleX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), _easeSineInOut));
        Timeline.SetDesiredFrameRate(settleX, VNotch.Services.AnimationConfig.TargetFps);

        var settleY = new DoubleAnimationUsingKeyFrames { Duration = duration };
        settleY.KeyFrames.Add(new EasingDoubleKeyFrame(CameraSectionScale.ScaleY, KeyTime.FromPercent(0.0)));
        settleY.KeyFrames.Add(new EasingDoubleKeyFrame(0.94, KeyTime.FromPercent(0.36), _easeSineInOut));
        settleY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), _easeSineInOut));
        Timeline.SetDesiredFrameRate(settleY, VNotch.Services.AnimationConfig.TargetFps);

        widthCollapseAnim.Completed += (s, e) =>
        {
            if (token != _cameraSectionAnimToken) return;
            _isCameraSectionExpanded = false;
            ResetCameraSectionLayoutInstant();
        };

        CameraSection.BeginAnimation(WidthProperty, widthCollapseAnim, HandoffBehavior.SnapshotAndReplace);
        CameraSection.BeginAnimation(HeightProperty, heightCollapseAnim, HandoffBehavior.SnapshotAndReplace);
        CameraSection.BeginAnimation(MarginProperty, marginCollapseAnim, HandoffBehavior.SnapshotAndReplace);
        CameraSectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, settleX, HandoffBehavior.SnapshotAndReplace);
        CameraSectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, settleY, HandoffBehavior.SnapshotAndReplace);
        FileShelf.BeginAnimation(OpacityProperty, shelfFadeIn, HandoffBehavior.SnapshotAndReplace);
    }

    private void ResetCameraSectionLayoutInstant()
    {
        bool wasCameraSectionAnimating = _isCameraSectionAnimating;
        _cameraSectionAnimToken++;
        _isCameraSectionAnimating = false;
        _isCameraSectionExpanded = false;

        CameraSection.BeginAnimation(WidthProperty, null);
        CameraSection.BeginAnimation(HeightProperty, null);
        CameraSection.BeginAnimation(MarginProperty, null);
        CameraSection.Width = double.NaN;
        CameraSection.Height = double.NaN;
        CameraSection.Margin = _cameraSectionCompactMargin;
        CameraSection.HorizontalAlignment = HorizontalAlignment.Stretch;
        CameraSection.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetColumn(CameraSection, 0);
        Grid.SetColumnSpan(CameraSection, 1);
        Panel.SetZIndex(CameraSection, 0);
        ApplyCameraCornerRadius(false);

        CameraSectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CameraSectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CameraSectionScale.ScaleX = 1.0;
        CameraSectionScale.ScaleY = 1.0;

        double currentNotchH = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : NotchBorder.Height;
        if (wasCameraSectionAnimating || currentNotchH > _expandedHeight + 1)
        {
            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Height = _expandedHeight;
            double windowHeightDip = _expandedHeight + 80;
            _overlayWindow.ResizeHeight(windowHeightDip);
        }

        FileShelf.BeginAnimation(OpacityProperty, null);
        FileShelf.Opacity = 1.0;
        FileShelf.Visibility = Visibility.Visible;
        FileShelf.IsHitTestVisible = true;

        if (!IsCameraPreviewLifecycleActive)
        {
            CameraPreviewImage.BeginAnimation(OpacityProperty, null);
            CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CameraPreviewBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            CameraOverlay.BeginAnimation(OpacityProperty, null);

            CameraPreviewImage.Opacity = 0;
            CameraPreviewImage.Source = null;
            CameraPreviewScale.ScaleX = 1.06;
            CameraPreviewScale.ScaleY = 1.06;
            CameraPreviewBlur.Radius = _settings.EnableBlurEffects ? 16.0 : 0.0;
            CameraOverlay.Visibility = Visibility.Visible;
            CameraOverlay.Opacity = 1.0;
            CameraErrorOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void CameraSection_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (_isAnimating || !_isSecondaryView || _isCameraSectionAnimating) return;
        if (_camera.IsStarting || _camera.IsStopping) return;

        if (!_isCameraActive)
        {
            AnimateCameraSectionToShelf(true);
            StartCameraPreview().SafeFireAndForget("CAMERA-START");
        }
        else
        {
            StopCameraPreview().SafeFireAndForget("CAMERA-STOP");
        }
    }

    private void CameraSection_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyCameraCornerRadius(_isCameraSectionExpanded);
    }

    private async Task StartCameraPreview()
    {
        if (_camera.IsActive && _camera.HasReader) return;
        if (_camera.IsStarting) return;

        try
        {
            CameraPreviewImage.BeginAnimation(OpacityProperty, null);
            CameraPreviewBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            PrimeCameraPreviewMorphIn();
            CameraErrorOverlay.Visibility = Visibility.Collapsed;

            string? error = await _camera.StartAsync(_settings.CameraDeviceId, () => _isSecondaryView);
            if (error != null)
            {
                throw new Exception(error);
            }
        }
        catch (Exception ex)
        {
            StopCameraPreviewSafe();
            if (_isSecondaryView)
            {
                CameraErrorOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                CameraErrorOverlay.Visibility = Visibility.Collapsed;
            }
            RuntimeLog.Error("CAMERA", ex, "Camera initialization failed");
        }
    }

    private void StopCameraPreviewForViewExit(bool resetLayout = true)
    {
        StopCameraPreviewSafe();
        if (resetLayout)
        {
            ResetCameraSectionLayoutInstant();
        }
    }

    private void StopCameraPreviewSafe()
    {
        _cameraPreviewMorphPending = false;
        _cameraWriteableBitmap = null;
        _cameraFrameDispatchPending = false;

        var (reader, capture, initializingCapture) = _camera.DetachForSafeStop();

        CameraPreviewImage.BeginAnimation(OpacityProperty, null);
        CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CameraPreviewBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        CameraOverlay.BeginAnimation(OpacityProperty, null);

        CameraPreviewImage.Opacity = 0;
        CameraPreviewImage.Source = null;
        CameraPreviewScale.ScaleX = 1.06;
        CameraPreviewScale.ScaleY = 1.06;
        CameraPreviewBlur.Radius = _settings.EnableBlurEffects ? 16.0 : 0.0;
        CameraOverlay.Opacity = 0;
        CameraOverlay.Visibility = Visibility.Collapsed;
        CameraErrorOverlay.Visibility = Visibility.Collapsed;

        _ = WebcamCaptureController.DisposeResourcesAsync(reader, capture);
        if (initializingCapture != null && !ReferenceEquals(initializingCapture, capture))
        {
            _ = WebcamCaptureController.DisposeResourcesAsync(null, initializingCapture);
        }
    }

    private void OnCameraFrameAvailable(byte[] buffer, int w, int h, int frameToken)
    {
        if (_cameraFrameDispatchPending)
        {
            _camera.ReleaseFrameBuffer();
            return;
        }

        _cameraFrameDispatchPending = true;
        try
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                try
                {
                    if (!_isCameraActive || frameToken != _camera.FadeToken) return;

                    var wbmp = _cameraWriteableBitmap;
                    if (wbmp == null || wbmp.PixelWidth != w || wbmp.PixelHeight != h)
                    {
                        wbmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                        _cameraWriteableBitmap = wbmp;
                        CameraPreviewImage.Source = wbmp;
                    }

                    wbmp.WritePixels(new Int32Rect(0, 0, w, h), buffer, w * 4, 0);

                    if (_cameraPreviewMorphPending)
                    {
                        AnimateCameraPreviewMorphIn();
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("CAMERA", ex, "Frame update failed");
                }
                finally
                {
                    _cameraFrameDispatchPending = false;
                    _camera.ReleaseFrameBuffer();
                }
            });
        }
        catch (Exception ex)
        {
            _cameraFrameDispatchPending = false;
            _camera.ReleaseFrameBuffer();
            RuntimeLog.Error("CAMERA", ex, "Frame dispatch failed");
        }
    }

    private async Task StopCameraPreview()
    {
        if (!_camera.TryBeginGracefulStop(out int fadeToken, out var reader, out var capture))
            return;

        try
        {
            AnimateCameraSectionToShelf(false);
            _cameraPreviewMorphPending = false;
            _cameraWriteableBitmap = null;
            _cameraFrameDispatchPending = false;

            double overlayFrom = Math.Clamp(CameraOverlay.Opacity, 0.0, 1.0);
            double previewFrom = Math.Clamp(CameraPreviewImage.Opacity, 0.0, 1.0);
            double previewScaleXFrom = CameraPreviewScale.ScaleX;
            double previewScaleYFrom = CameraPreviewScale.ScaleY;
            double previewBlurFrom = CameraPreviewBlur.Radius;

            CameraOverlay.BeginAnimation(OpacityProperty, null);
            CameraOverlay.Visibility = Visibility.Visible;
            CameraOverlay.Opacity = overlayFrom;
            CameraErrorOverlay.Visibility = Visibility.Collapsed;

            CameraPreviewImage.BeginAnimation(OpacityProperty, null);
            CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CameraPreviewBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            CameraPreviewImage.Opacity = previewFrom;
            CameraPreviewScale.ScaleX = previewScaleXFrom;
            CameraPreviewScale.ScaleY = previewScaleYFrom;
            CameraPreviewBlur.Radius = previewBlurFrom;

            var previewFadeOut = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(TimeSpan.FromMilliseconds(CameraSectionCollapseDurationMs))
            };
            previewFadeOut.KeyFrames.Add(new EasingDoubleKeyFrame(previewFrom, KeyTime.FromPercent(0.0)));
            previewFadeOut.KeyFrames.Add(new EasingDoubleKeyFrame(previewFrom, KeyTime.FromPercent(0.72), _easeSineInOut));
            previewFadeOut.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromPercent(1.0), _easeExpOut6));
            Timeline.SetDesiredFrameRate(previewFadeOut, VNotch.Services.AnimationConfig.TargetFps);

            var overlayFadeIn = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(TimeSpan.FromMilliseconds(CameraSectionCollapseDurationMs))
            };
            overlayFadeIn.KeyFrames.Add(new EasingDoubleKeyFrame(overlayFrom, KeyTime.FromPercent(0.0)));
            overlayFadeIn.KeyFrames.Add(new EasingDoubleKeyFrame(overlayFrom, KeyTime.FromPercent(0.66), _easeSineInOut));
            overlayFadeIn.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), _easeExpOut6));
            Timeline.SetDesiredFrameRate(overlayFadeIn, VNotch.Services.AnimationConfig.TargetFps);

            var previewScaleOutX = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(TimeSpan.FromMilliseconds(CameraSectionCollapseDurationMs))
            };
            previewScaleOutX.KeyFrames.Add(new EasingDoubleKeyFrame(previewScaleXFrom, KeyTime.FromPercent(0.0)));
            previewScaleOutX.KeyFrames.Add(new EasingDoubleKeyFrame(previewScaleXFrom, KeyTime.FromPercent(0.72), _easeSineInOut));
            previewScaleOutX.KeyFrames.Add(new EasingDoubleKeyFrame(1.04, KeyTime.FromPercent(1.0), _easeExpOut6));
            Timeline.SetDesiredFrameRate(previewScaleOutX, VNotch.Services.AnimationConfig.TargetFps);

            var previewScaleOutY = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(TimeSpan.FromMilliseconds(CameraSectionCollapseDurationMs))
            };
            previewScaleOutY.KeyFrames.Add(new EasingDoubleKeyFrame(previewScaleYFrom, KeyTime.FromPercent(0.0)));
            previewScaleOutY.KeyFrames.Add(new EasingDoubleKeyFrame(previewScaleYFrom, KeyTime.FromPercent(0.72), _easeSineInOut));
            previewScaleOutY.KeyFrames.Add(new EasingDoubleKeyFrame(1.04, KeyTime.FromPercent(1.0), _easeExpOut6));
            Timeline.SetDesiredFrameRate(previewScaleOutY, VNotch.Services.AnimationConfig.TargetFps);

            var previewBlurOut = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(TimeSpan.FromMilliseconds(CameraSectionCollapseDurationMs))
            };
            previewBlurOut.KeyFrames.Add(new EasingDoubleKeyFrame(previewBlurFrom, KeyTime.FromPercent(0.0)));
            previewBlurOut.KeyFrames.Add(new EasingDoubleKeyFrame(previewBlurFrom, KeyTime.FromPercent(0.70), _easeSineInOut));
            previewBlurOut.KeyFrames.Add(new EasingDoubleKeyFrame(_settings.EnableBlurEffects ? 16.0 : 0.0, KeyTime.FromPercent(1.0), _easeExpOut6));
            Timeline.SetDesiredFrameRate(previewBlurOut, VNotch.Services.AnimationConfig.TargetFps);

            previewFadeOut.Completed += (s, e) =>
            {
                if (fadeToken != _camera.FadeToken || _isCameraActive) return;
                CameraPreviewImage.BeginAnimation(OpacityProperty, null);
                CameraPreviewImage.Opacity = 0;
                CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                CameraPreviewScale.ScaleX = 1.06;
                CameraPreviewScale.ScaleY = 1.06;
                CameraPreviewBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
                CameraPreviewBlur.Radius = _settings.EnableBlurEffects ? 16.0 : 0.0;
                CameraPreviewImage.Source = null;
                _cameraWriteableBitmap = null;
            };
            CameraPreviewImage.BeginAnimation(OpacityProperty, previewFadeOut, HandoffBehavior.SnapshotAndReplace);
            CameraOverlay.BeginAnimation(OpacityProperty, overlayFadeIn, HandoffBehavior.SnapshotAndReplace);
            CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, previewScaleOutX, HandoffBehavior.SnapshotAndReplace);
            CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, previewScaleOutY, HandoffBehavior.SnapshotAndReplace);
            CameraPreviewBlur.BeginAnimation(BlurEffect.RadiusProperty, previewBlurOut, HandoffBehavior.SnapshotAndReplace);

            await Task.Run(async () =>
            {
                try
                {
                    if (reader != null)
                    {
                        await reader.StopAsync();
                        reader.Dispose();
                    }
                    capture?.Dispose();
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("CAMERA", ex, "StopCameraPreview failed");
            StopCameraPreviewSafe();
        }
        finally
        {
            _camera.EndGracefulStop(fadeToken);
        }
    }

    #endregion
}
