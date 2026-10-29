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

    private const string CameraLogTag = "CAMERA";
    private readonly WebcamCaptureController _camera = new();

    private bool _cameraPreviewMorphPending = false;
    private Storyboard? _cameraPreviewExitStoryboard;

    private WriteableBitmap? _cameraWriteableBitmap;
    // Tracks the bitmap's allocated pixel dimensions so we can reuse oversized bitmaps.
    private int _cameraBitmapAllocW;
    private int _cameraBitmapAllocH;
    private bool _cameraFrameDispatchPending = false;

    private bool _isCameraActive => _camera.IsActive;
    private bool IsCameraPreviewLifecycleActive => _camera.IsLifecycleActive;

    private void InitializeCameraController()
    {
        _camera.FrameAvailable += OnCameraFrameAvailable;
    }

    private readonly System.Collections.Generic.Dictionary<UIElement, Brush?> _cameraAppearanceMasks = new();

    private void ApplyAppearanceOpacity()
    {
        foreach (var (element, originalMask) in _cameraAppearanceMasks)
            element.OpacityMask = originalMask;
        _cameraAppearanceMasks.Clear();

        // Preserve whole-window compositing outside preview playback. The held
        // exit frame stays opaque until its visual teardown clears the source.
        bool hasPreview = CameraPreviewImage.Source != null;
        Opacity = hasPreview ? 1 : _settings.Opacity;
        if (!hasPreview) return;

        var mask = new SolidColorBrush(Colors.White) { Opacity = _settings.Opacity };
        mask.Freeze();
        // Apply the mask to sibling branches along the preview's ancestor path.
        // This preserves their animations and descendant masks without dimming
        // any ancestor of the camera or changing its layout and transitions.
        for (DependencyObject branch = CameraContent; branch != NotchContainer;)
        {
            var parent = VisualTreeHelper.GetParent(branch);
            if (parent == null) break;
            if (parent is Panel panel)
            {
                foreach (UIElement sibling in panel.Children)
                {
                    if (ReferenceEquals(sibling, branch)) continue;
                    _cameraAppearanceMasks.Add(sibling, sibling.OpacityMask);
                    sibling.OpacityMask = mask;
                }
            }
            branch = parent;
        }
    }

    private void PrimeCameraPreviewMorphIn()
    {
        CancelCameraPreviewMorphOut();
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

        var previewFadeIn = MakeAnim(CameraPreviewImage.Opacity, 1.0, morphDuration, _easeExpOut6, null);
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

    private static double ComputeCameraCornerRadius(bool expandedToShelf)
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

    private void ResetCameraSectionLayoutInstant()
    {
        if (!double.IsNaN(CameraSection.Width)) CameraSection.Width = double.NaN;
        if (!double.IsNaN(CameraSection.Height)) CameraSection.Height = double.NaN;
        if (CameraSection.Margin != new Thickness(0)) CameraSection.Margin = new Thickness(0);
        ApplyCameraCornerRadius(true);
        if (!IsCameraPreviewLifecycleActive)
        {
            CameraOverlay.Visibility = Visibility.Visible;
            CameraOverlay.Opacity = 1;
        }
    }


    private void CameraSection_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (_isAnimating || !_isCameraView) return;
        if (_camera.IsStarting || _camera.IsStopping) return;

        if (!_isCameraActive)
        {
            StartCameraPreview().SafeFireAndForget("CAMERA-START");
        }
        else
        {
            StopCameraPreview().SafeFireAndForget("CAMERA-STOP");
        }
    }

    private void CameraSection_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (CameraContent == null || CameraContent.Visibility != Visibility.Visible || CameraContent.Opacity < 0.01) return;
        ApplyCameraCornerRadius(true);
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

            string? error = await _camera.StartAsync(_settings.CameraDeviceId, () => _isCameraView);
            if (error != null)
            {
                throw new InvalidOperationException(error);
            }
        }
        catch (Exception ex)
        {
            StopCameraPreviewSafe();
            if (_isCameraView)
            {
                CameraErrorOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                CameraErrorOverlay.Visibility = Visibility.Collapsed;
            }
            RuntimeLog.Error(CameraLogTag, ex, "Camera initialization failed");
        }
    }

    private bool _pendingCameraPreviewVisualTeardown;

    private void StopCameraPreviewForViewExit(bool resetLayout = true)
    {
        _pendingCameraPreviewVisualTeardown = false;
        StopCameraPreviewSafe();
        if (resetLayout)
        {
            ResetCameraSectionLayoutInstant();
        }
    }

    private void StopCameraPreviewForViewTransition()
    {
        _pendingCameraPreviewVisualTeardown = true;
        DetachCameraHardwareForExit();
    }

    private void DetachCameraHardwareForExit()
    {
        _cameraPreviewMorphPending = false;
        _cameraWriteableBitmap = null;
        _cameraBitmapAllocW = 0;
        _cameraBitmapAllocH = 0;

        var (reader, capture, initializingCapture) = _camera.DetachForSafeStop();

        _ = WebcamCaptureController.DisposeResourcesAsync(reader, capture);
        if (initializingCapture != null && !ReferenceEquals(initializingCapture, capture))
        {
            _ = WebcamCaptureController.DisposeResourcesAsync(null, initializingCapture);
        }
    }

    private void FinalizePendingCameraPreviewTeardown()
    {
        if (!_pendingCameraPreviewVisualTeardown) return;
        _pendingCameraPreviewVisualTeardown = false;
        CancelCameraPreviewMorphOut();

        CameraPreviewImage.BeginAnimation(OpacityProperty, null);
        CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CameraPreviewBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        CameraOverlay.BeginAnimation(OpacityProperty, null);

        CameraPreviewImage.Opacity = 0;
        CameraPreviewImage.Source = null;
        ApplyAppearanceOpacity();
        CameraPreviewScale.ScaleX = 1.06;
        CameraPreviewScale.ScaleY = 1.06;
        CameraPreviewBlur.Radius = _settings.EnableBlurEffects ? 16.0 : 0.0;
        CameraOverlay.Opacity = 1;
        CameraOverlay.Visibility = Visibility.Visible;
        CameraErrorOverlay.Visibility = Visibility.Collapsed;

        if (CameraContent.Visibility == Visibility.Visible)
        {
            ResetCameraSectionLayoutInstant();
        }
    }

    private void StopCameraPreviewSafe()
    {
        CancelCameraPreviewMorphOut();
        _cameraPreviewMorphPending = false;
        _cameraWriteableBitmap = null;
        _cameraBitmapAllocW = 0;
        _cameraBitmapAllocH = 0;

        var (reader, capture, initializingCapture) = _camera.DetachForSafeStop();

        CameraPreviewImage.BeginAnimation(OpacityProperty, null);
        CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CameraPreviewBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        CameraOverlay.BeginAnimation(OpacityProperty, null);

        CameraPreviewImage.Opacity = 0;
        CameraPreviewImage.Source = null;
        ApplyAppearanceOpacity();
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
                    // Reuse bitmap if large enough (grow-only) to eliminate LOH allocations
                    // during webcam resolution negotiation.
                    if (wbmp == null || _cameraBitmapAllocW < w || _cameraBitmapAllocH < h)
                    {
                        wbmp = new WriteableBitmap(
                            Math.Max(w, _cameraBitmapAllocW > 0 ? _cameraBitmapAllocW : w),
                            Math.Max(h, _cameraBitmapAllocH > 0 ? _cameraBitmapAllocH : h),
                            96, 96, PixelFormats.Bgra32, null);
                        _cameraBitmapAllocW = wbmp.PixelWidth;
                        _cameraBitmapAllocH = wbmp.PixelHeight;
                        _cameraWriteableBitmap = wbmp;
                        CameraPreviewImage.Source = wbmp;
                        ApplyAppearanceOpacity();
                    }

                    wbmp.WritePixels(new Int32Rect(0, 0, w, h), buffer, w * 4, 0);

                    if (_cameraPreviewMorphPending)
                    {
                        AnimateCameraPreviewMorphIn();
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error(CameraLogTag, ex, "Frame update failed");
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
            RuntimeLog.Error(CameraLogTag, ex, "Frame dispatch failed");
        }
    }

    private void AnimateCameraPreviewMorphOut(int fadeToken)
    {
        // Hold the current frame while the storyboard waits for the exit phase.
        double overlayFrom = Math.Clamp(CameraOverlay.Opacity, 0.0, 1.0);
        double previewFrom = Math.Clamp(CameraPreviewImage.Opacity, 0.0, 1.0);
        double scaleXFrom = CameraPreviewScale.ScaleX;
        double scaleYFrom = CameraPreviewScale.ScaleY;
        double blurFrom = CameraPreviewBlur.Radius;
        CancelCameraPreviewMorphOut();
        CameraOverlay.BeginAnimation(OpacityProperty, null);
        CameraPreviewImage.BeginAnimation(OpacityProperty, null);
        CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CameraPreviewBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        CameraOverlay.Opacity = overlayFrom;
        CameraPreviewImage.Opacity = previewFrom;
        CameraPreviewScale.ScaleX = scaleXFrom;
        CameraPreviewScale.ScaleY = scaleYFrom;
        CameraPreviewBlur.Radius = blurFrom;
        CameraOverlay.Visibility = Visibility.Visible;
        CameraErrorOverlay.Visibility = Visibility.Collapsed;

        if (AnimationConfig.ReduceMotion)
        {
            CompleteCameraPreviewMorphOut(fadeToken);
            return;
        }

        var storyboard = WithFps(((Storyboard)FindResource("CameraPreviewExit")).Clone());
        foreach (DoubleAnimation animation in storyboard.Children.OfType<DoubleAnimation>())
            if (Storyboard.GetTargetName(animation) == nameof(CameraPreviewBlur))
                animation.To = _settings.EnableBlurEffects ? 16.0 : 0.0;
        storyboard.Completed += (_, _) =>
        {
            if (ReferenceEquals(_cameraPreviewExitStoryboard, storyboard))
                CompleteCameraPreviewMorphOut(fadeToken);
        };
        _cameraPreviewExitStoryboard = storyboard;
        storyboard.Begin(this, HandoffBehavior.SnapshotAndReplace, isControllable: true);
    }

    private void CompleteCameraPreviewMorphOut(int fadeToken)
    {
        if (fadeToken != _camera.FadeToken || _isCameraActive) return;
        CancelCameraPreviewMorphOut();
        CameraPreviewImage.Opacity = 0;
        CameraPreviewScale.ScaleX = 1.06;
        CameraPreviewScale.ScaleY = 1.06;
        CameraPreviewBlur.Radius = _settings.EnableBlurEffects ? 16.0 : 0.0;
        CameraOverlay.Opacity = 1;
        CameraPreviewImage.Source = null;
        ApplyAppearanceOpacity();
        _cameraWriteableBitmap = null;
    }

    private void CancelCameraPreviewMorphOut()
    {
        _cameraPreviewExitStoryboard?.Remove(this);
        _cameraPreviewExitStoryboard = null;
    }

    private async Task StopCameraPreview()
    {
        if (!_camera.TryBeginGracefulStop(out int fadeToken, out var reader, out var capture))
            return;

        try
        {
            _cameraPreviewMorphPending = false;
            _cameraWriteableBitmap = null;
            _cameraBitmapAllocW = 0;
            _cameraBitmapAllocH = 0;

            AnimateCameraPreviewMorphOut(fadeToken);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(CameraLogTag, ex, "StopCameraPreview failed");
            StopCameraPreviewSafe();
        }
        finally
        {
            await WebcamCaptureController.DisposeResourcesAsync(reader, capture);
            _camera.EndGracefulStop(fadeToken);
        }
    }

    #endregion
}
