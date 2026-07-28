using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Clipboard Peek

    private bool _isClipboardPeekActive = false;
    private int _clipboardPeekToken = 0;
    private DispatcherTimer? _clipboardRevertTimer;

    private void PlayClipboardPeek()
    {
        if (!_isExpanded && !_isAnimating)
        {
            NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            var peakTime = TimeSpan.FromMilliseconds(100);
            var endTime = TimeSpan.FromMilliseconds(500);

            var bounceX = new DoubleAnimationUsingKeyFrames();
            bounceX.KeyFrames.Add(new EasingDoubleKeyFrame(1.06,
                KeyTime.FromTimeSpan(peakTime), _easeQuadOut));
            bounceX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
                KeyTime.FromTimeSpan(endTime), _easeSoftSpring));
            Timeline.SetDesiredFrameRate(bounceX, VNotch.Services.AnimationConfig.TargetFps);

            var bounceY = new DoubleAnimationUsingKeyFrames();
            bounceY.KeyFrames.Add(new EasingDoubleKeyFrame(0.95,
                KeyTime.FromTimeSpan(peakTime), _easeQuadOut));
            bounceY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
                KeyTime.FromTimeSpan(endTime), _easeSoftSpring));
            Timeline.SetDesiredFrameRate(bounceY, VNotch.Services.AnimationConfig.TargetFps);

            NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceX);
            NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceY);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceX);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceY);
        }

        if (_isMusicCompactMode && !_isExpanded)
        {
            ShowClipboardCopiedState();
        }
    }
    private void ShowClipboardCopiedState()
    {
        if (!TryAcquireCompactSlot(VNotch.Controllers.CompactPillSlot.Clipboard, out int token))
            return;

        _clipboardPeekToken = token;
        _isClipboardPeekActive = true;

        SuppressPrivacyDot();

        ClipboardCopiedText.Text = Loc.Get("clipboard.copied");

        CancelThumbnailSwitchAnimations();

        CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        var thumbFadeOut = MakeAnim(1.0, 0.0, _dur150, _easeQuadOut);
        thumbFadeOut.Completed += (s, e) =>
        {
            if (_isClipboardPeekActive)
                CompactThumbnailBorder.Visibility = Visibility.Collapsed;
        };
        CompactThumbnailBorder.BeginAnimation(OpacityProperty, thumbFadeOut);

        MusicViz.BeginAnimation(OpacityProperty, null);
        var vizFadeOut = MakeAnim(1.0, 0.0, _dur150, _easeQuadOut);
        vizFadeOut.Completed += (s, e) =>
        {
            if (_isClipboardPeekActive)
                MusicViz.Visibility = Visibility.Collapsed;
        };
        MusicViz.BeginAnimation(OpacityProperty, vizFadeOut);

        CompactHoverInfo.BeginAnimation(OpacityProperty, null);
        CompactHoverInfo.Opacity = 0;
        CompactHoverInfo.Visibility = Visibility.Collapsed;

        ClipboardCheckIcon.Visibility = Visibility.Visible;
        ClipboardCheckIcon.BeginAnimation(OpacityProperty, null);
        ClipboardCheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ClipboardCheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        ClipboardCheckScale.ScaleX = 0.5;
        ClipboardCheckScale.ScaleY = 0.5;

        var checkFadeIn = MakeAnim(0.0, 1.0, _dur250, _easeQuadOut);
        var checkScaleIn = MakeAnim(0.5, 1.0, _dur400, _easeSoftSpring);
        Timeline.SetDesiredFrameRate(checkScaleIn, VNotch.Services.AnimationConfig.TargetFps);

        ClipboardCheckIcon.BeginAnimation(OpacityProperty, checkFadeIn);
        ClipboardCheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, checkScaleIn);
        ClipboardCheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, checkScaleIn);

        ClipboardCopiedText.Visibility = Visibility.Visible;
        ClipboardCopiedText.BeginAnimation(OpacityProperty, null);
        ClipboardCopiedTranslate.BeginAnimation(TranslateTransform.XProperty, null);

        ClipboardCopiedTranslate.X = -8;

        var textFadeIn = MakeAnim(0.0, 1.0, _dur250, _easeQuadOut);
        var textSlideIn = MakeAnim(-8.0, 0.0, _dur350, _easeExpOut6);
        Timeline.SetDesiredFrameRate(textSlideIn, VNotch.Services.AnimationConfig.TargetFps);

        ClipboardCopiedText.BeginAnimation(OpacityProperty, textFadeIn);
        ClipboardCopiedTranslate.BeginAnimation(TranslateTransform.XProperty, textSlideIn);

        StopClipboardRevertTimer();

        int revertToken = _clipboardPeekToken;
        var revertTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1600)
        };
        _clipboardRevertTimer = revertTimer;
        revertTimer.Tick += (s, e) =>
        {
            revertTimer.Stop();
            if (!ReferenceEquals(_clipboardRevertTimer, revertTimer) ||
                revertToken != _clipboardPeekToken)
            {
                return;
            }

            _clipboardRevertTimer = null;
            RevertClipboardCopiedState();
        };
        revertTimer.Start();
    }
    private void RevertClipboardCopiedState()
    {
        int token = _clipboardPeekToken;
        _isClipboardPeekActive = false;
        _compactPillArbiter.Release(token);
        _clipboardPeekToken = 0;

        RestorePrivacyDotVisibility();

        ClipboardCheckIcon.BeginAnimation(OpacityProperty, null);
        ClipboardCheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ClipboardCheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        var checkFadeOut = MakeAnim(1.0, 0.0, _dur250, _easeQuadOut);
        var checkScaleOut = MakeAnim(1.0, 0.5, _dur400, _easeSoftSpring);
        Timeline.SetDesiredFrameRate(checkScaleOut, VNotch.Services.AnimationConfig.TargetFps);

        checkFadeOut.Completed += (s, e) =>
        {
            if (!_isClipboardPeekActive)
                ClipboardCheckIcon.Visibility = Visibility.Collapsed;
        };

        ClipboardCheckIcon.BeginAnimation(OpacityProperty, checkFadeOut);
        ClipboardCheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, checkScaleOut);
        ClipboardCheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, checkScaleOut);

        ClipboardCopiedText.BeginAnimation(OpacityProperty, null);
        ClipboardCopiedTranslate.BeginAnimation(TranslateTransform.XProperty, null);

        var textFadeOut = MakeAnim(1.0, 0.0, _dur250, _easeQuadOut);
        var textSlideOut = MakeAnim(0.0, -8.0, _dur350, _easeExpOut6);
        Timeline.SetDesiredFrameRate(textSlideOut, VNotch.Services.AnimationConfig.TargetFps);

        textFadeOut.Completed += (s, e) =>
        {
            if (!_isClipboardPeekActive)
                ClipboardCopiedText.Visibility = Visibility.Collapsed;
        };

        ClipboardCopiedText.BeginAnimation(OpacityProperty, textFadeOut);
        ClipboardCopiedTranslate.BeginAnimation(TranslateTransform.XProperty, textSlideOut);

        CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CompactThumbnailScale.ScaleX = 1.0;
        CompactThumbnailScale.ScaleY = 1.0;
        CompactThumbnailBorder.Visibility = Visibility.Visible;
        CompactThumbnailBorder.Opacity = 0;
        var thumbFadeIn = MakeAnim(0.0, 1.0, _dur200, _easeQuadOut);
        CompactThumbnailBorder.BeginAnimation(OpacityProperty, thumbFadeIn);

        ShowMusicVisualizer(duration: _dur100);
    }

    private void CancelClipboardPeekImmediate()
    {
        if (!_isClipboardPeekActive) return;

        StopClipboardRevertTimer();
        _isClipboardPeekActive = false;
        _clipboardPeekToken = 0;

        ClipboardCheckIcon.BeginAnimation(OpacityProperty, null);
        ClipboardCheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ClipboardCheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ClipboardCheckIcon.Opacity = 0;
        ClipboardCheckIcon.Visibility = Visibility.Collapsed;

        ClipboardCopiedText.BeginAnimation(OpacityProperty, null);
        ClipboardCopiedTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        ClipboardCopiedText.Opacity = 0;
        ClipboardCopiedText.Visibility = Visibility.Collapsed;

    }

    private void StopClipboardRevertTimer()
    {
        var timer = _clipboardRevertTimer;
        _clipboardRevertTimer = null;
        timer?.Stop();
    }

    #endregion
}
