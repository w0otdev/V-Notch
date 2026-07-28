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

    private bool TryAcquireCompactSlot(CompactPillSlot slot, out int token)
    {
        WakeFromIdle();

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

            // A dismiss animation is allowed to finish after its compact-slot
            // token has been released by the shorter content fade. Expansions,
            // however, must not commit after another notification preempts them.
            bool returningToRest = Math.Abs(targetWidth - _collapsedWidth) < 0.5;
            bool canCommitTarget = token == 0
                || _compactPillArbiter.IsTokenCurrent(token)
                || returningToRest;
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
