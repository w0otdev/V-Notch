using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Controllers;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private int _compactWidthAnimationVersion = 0;
    private bool _compactPresentationGuardInstalled;
    private bool _enforcingCompactPresentation;

    private void EnsureCompactPresentationGuard()
    {
        if (_compactPresentationGuardInstalled) return;
        _compactPresentationGuardInstalled = true;
        foreach (var element in new UIElement[] { MusicViz, CompactThumbnailBorder,
            MusicCompactContent, CollapsedContent,
            ClipboardCheckIcon, ClipboardCopiedText, VolumeIndicatorContainer,
            BluetoothNotification, BluetoothDisconnectNotification, ChargingNotification })
            element.IsVisibleChanged += (_, _) => EnforceCompactPresentationOwner();
    }

    private static void HideCompactSurface(UIElement element)
    {
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = 0;
        element.Visibility = Visibility.Collapsed;
    }

    private void EnforceCompactPresentationOwner()
    {
        if (_enforcingCompactPresentation) return;
        _enforcingCompactPresentation = true;
        try
        {
            var owner = _compactPillArbiter.ActiveSlot;
            if (owner is CompactPillSlot.Bluetooth or CompactPillSlot.Charging or CompactPillSlot.Greeting)
            {
                HideCompactSurface(MusicCompactContent);
                HideCompactSurface(CollapsedContent);
            }
            if (owner != CompactPillSlot.None)
            {
                if (owner != CompactPillSlot.Clipboard) HideCompactSurface(MusicViz);
                HideCompactSurface(CompactThumbnailBorder);
            }
            if (owner != CompactPillSlot.Clipboard)
            {
                HideCompactSurface(ClipboardCheckIcon);
                HideCompactSurface(ClipboardCopiedText);
            }
            if (owner != CompactPillSlot.Volume) HideCompactSurface(VolumeIndicatorContainer);
            if (owner != CompactPillSlot.Bluetooth)
            {
                HideCompactSurface(BluetoothNotification);
                HideCompactSurface(BluetoothDisconnectNotification);
            }
            if (owner != CompactPillSlot.Charging) HideCompactSurface(ChargingNotification);
        }
        finally { _enforcingCompactPresentation = false; }
    }

    private void RestoreCompactMediaPresentation()
    {
        if (_isExpanded || _isAnimating || !_isMusicCompactMode ||
            !_compactPillArbiter.CanRestoreMedia(_compactPillArbiter.Revision)) return;
        EnforceCompactPresentationOwner();
        AnimateCompactWidth(_collapsedWidth, _dur350, _easeExpOut6, 0);
        CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
        CompactThumbnailBorder.Opacity = 1;
        CompactThumbnailBorder.Visibility = Visibility.Visible;
        ShowMusicVisualizer();
    }

    private bool TryAcquireCompactSlot(CompactPillSlot slot, out int token)
    {
        WakeFromIdle();
        EnsureCompactPresentationGuard();

        var result = _compactPillArbiter.TryAcquire(slot);
        token = result.Token;
        if (!result.Won)
        {
            return false;
        }

        if (result.Preempted != CompactPillSlot.None)
        {
            CancelCompactSlotImmediate(result.Preempted);
        }
        EnforceCompactPresentationOwner();
        return true;
    }

    private void CancelCompactSlotImmediate(CompactPillSlot slot)
    {
        switch (slot)
        {
            case CompactPillSlot.Clipboard:
                CancelClipboardPeekImmediate();
                break;
            case CompactPillSlot.Volume:
                DismissVolumeIndicatorImmediate();
                break;
            case CompactPillSlot.Bluetooth:
                CancelBluetoothNotificationImmediate();
                break;
            case CompactPillSlot.Charging:
                CancelChargingGlanceImmediate();
                break;
            case CompactPillSlot.Greeting:
                break;
        }
    }

    private void AnimateCompactWidth(double targetWidth, TimeSpan duration, IEasingFunction ease, int token)
        => AnimateCompactWidth(targetWidth, new Duration(duration), ease, token);

    private void AnimateCompactWidth(double targetWidth, Duration duration, IEasingFunction ease, int token)
    {
        int version = ++_compactWidthAnimationVersion;
        long revision = _compactPillArbiter.Revision;
        double fromWidth = NotchBorder.ActualWidth;
        if (double.IsNaN(fromWidth) || double.IsInfinity(fromWidth) || fromWidth <= 0)
        {
            fromWidth = _collapsedWidth;
        }

        double previousBaseWidth = (double)NotchBorder.GetAnimationBaseValue(WidthProperty);

        var anim = new DoubleAnimation
        {
            From = fromWidth,
            To = targetWidth,
            Duration = duration,
            EasingFunction = ease
        };
        Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);

        anim.Completed += (_, _) =>
        {
            if (version != _compactWidthAnimationVersion) return;
            if (token == 0 && !_compactPillArbiter.CanRestoreMedia(revision)) return;

            // Allow dismiss animations to finish post token release, but prevent
            // expansions from committing if preempted by another notification.
            bool returningToRest = Math.Abs(targetWidth - _collapsedWidth) < 0.5;
            bool canCommitTarget = token == 0
                || _compactPillArbiter.IsTokenCurrent(token)
                || (returningToRest && _compactPillArbiter.CanRestoreMedia(revision + 1));
            double finalWidth = canCommitTarget ? targetWidth : previousBaseWidth;

            // Set the base while HoldEnd still owns the rendered value, then
            // remove the clock. Clearing first exposes the old base for a frame.
            NotchBorder.Width = finalWidth;
            NotchBorder.BeginAnimation(WidthProperty, null);
        };

        NotchBorder.BeginAnimation(WidthProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private bool IsCompactSlotStale(int token) => !_compactPillArbiter.IsTokenCurrent(token);
}
