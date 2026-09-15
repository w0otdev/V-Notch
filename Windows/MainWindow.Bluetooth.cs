using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Bluetooth Notification

    private bool _isBluetoothNotificationVisible = false;
    private bool _currentBluetoothConnected = true;
    private int _bluetoothNotificationToken = 0;

    private void InitializeBluetoothNotificationController()
    {
        _bluetoothController.ShowRequested += BluetoothController_ShowRequested;
        _bluetoothController.DismissRequested += BluetoothController_DismissRequested;
    }

    private void BluetoothModule_DeviceConnected(object? sender, BluetoothDeviceInfo info)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _bluetoothController.TryShow(info, connected: true,
                _isNotchVisible, _isAnimating, _isExpanded, _notchState.IsMusicExpanded);
        });
    }

    private void BluetoothModule_DeviceDisconnected(object? sender, BluetoothDeviceInfo info)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _bluetoothController.TryShow(info, connected: false,
                _isNotchVisible, _isAnimating, _isExpanded, _notchState.IsMusicExpanded);
        });
    }

    private void BluetoothController_ShowRequested(BluetoothDeviceInfo device, bool connected)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _currentBluetoothConnected = connected;

            if (connected)
            {
                BluetoothDeviceName.Text = device.Name;
                BluetoothIcon.Data = BluetoothNotificationController.GetIconGeometry(device.DeviceType);
                BluetoothStatusText.Text = Loc.Get("bluetooth.connected");
            }
            else
            {
                BluetoothDisconnectDeviceName.Text = device.Name;
                BluetoothDisconnectIcon.Data = BluetoothNotificationController.GetIconGeometry(device.DeviceType);
                BluetoothDisconnectStatusText.Text = Loc.Get("bluetooth.disconnected");
            }

            AnimateBluetoothNotificationIn(connected);
        });
    }

    private void BluetoothController_DismissRequested()
    {
        Dispatcher.BeginInvoke(() =>
        {
            AnimateBluetoothNotificationOut();
        });
    }

    private Grid ActiveBluetoothGrid => _currentBluetoothConnected ? BluetoothNotification : BluetoothDisconnectNotification;
    private TranslateTransform ActiveBluetoothTranslate => _currentBluetoothConnected ? BluetoothNotificationTranslate : BluetoothDisconnectNotificationTranslate;
    private ScaleTransform ActiveBluetoothIconScale => _currentBluetoothConnected ? BluetoothIconScale : BluetoothDisconnectIconScale;

    private void AnimateBluetoothNotificationIn(bool connected)
    {
        if (!TryAcquireCompactSlot(VNotch.Controllers.CompactPillSlot.Bluetooth, out int token))
        {
            return;
        }

        _bluetoothNotificationToken = token;
        _isBluetoothNotificationVisible = true;

        SuppressPrivacyDot();

        var otherGrid = connected ? BluetoothDisconnectNotification : BluetoothNotification;
        otherGrid.Visibility = Visibility.Collapsed;
        otherGrid.Opacity = 0;

        var activeGrid = ActiveBluetoothGrid;
        var activeTranslate = ActiveBluetoothTranslate;
        var activeIconScale = ActiveBluetoothIconScale;

        activeGrid.Visibility = Visibility.Visible;
        activeGrid.Opacity = 0;

        PlayBluetoothBounce();

        CollapsedContent.BeginAnimation(OpacityProperty, null);
        CollapsedContent.Opacity = 0;
        CollapsedContent.Visibility = Visibility.Collapsed;

        if (MusicCompactContent.Visibility == Visibility.Visible)
        {
            MusicCompactContent.BeginAnimation(OpacityProperty, null);
            MusicCompactContent.Opacity = 0;
            MusicCompactContent.Visibility = Visibility.Collapsed;
        }

        var fadeIn = MakeAnim(0d, 1d, _dur350, _easeExpOut7, TimeSpan.FromMilliseconds(100));
        activeGrid.BeginAnimation(OpacityProperty, fadeIn);

        var slideUp = MakeAnim(6d, 0d, _dur350, _easeExpOut7, TimeSpan.FromMilliseconds(100));
        activeTranslate.BeginAnimation(
            TranslateTransform.YProperty, slideUp, HandoffBehavior.SnapshotAndReplace);

        var iconScale = MakeAnim(0.8d, 1d, _dur400, _easeSpring, TimeSpan.FromMilliseconds(150));
        activeIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, iconScale);
        activeIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, iconScale);
    }

    private void AnimateBluetoothNotificationOut()
    {
        var activeGrid = ActiveBluetoothGrid;
        var activeTranslate = ActiveBluetoothTranslate;
        int token = _bluetoothNotificationToken;

        var fadeOut = MakeAnim(1d, 0d, _dur250, _easePowerIn2, null);
        fadeOut.Completed += (s, e) =>
        {
            if (token != _bluetoothNotificationToken) return;
            if (IsCompactSlotStale(token)) return;

            activeGrid.Visibility = Visibility.Collapsed;
            _isBluetoothNotificationVisible = false;
            _bluetoothController.MarkDismissed();
            _compactPillArbiter.Release(token);
            RestoreCompactMediaPresentation();
            _bluetoothNotificationToken = 0;

            RestorePrivacyDotVisibility();

            if (_isMusicCompactMode && _currentMediaInfo != null)
            {
                MusicCompactContent.Visibility = Visibility.Visible;
                var fadeInMusic = MakeAnim(0d, 1d, _dur250, _easePowerOut3, null);
                MusicCompactContent.BeginAnimation(OpacityProperty, fadeInMusic);
            }
            else
            {
                CollapsedContent.Visibility = Visibility.Visible;
                var fadeInCollapsed = MakeAnim(0d, 1d, _dur250, _easePowerOut3, null);
                CollapsedContent.BeginAnimation(OpacityProperty, fadeInCollapsed);
            }
        };

        activeGrid.BeginAnimation(OpacityProperty, fadeOut);

        var slideDown = MakeAnim(0d, -4d, _dur250, _easePowerIn2, null);
        activeTranslate.BeginAnimation(
            TranslateTransform.YProperty, slideDown, HandoffBehavior.SnapshotAndReplace);
    }

    private void CancelBluetoothNotificationImmediate()
    {
        if (!_isBluetoothNotificationVisible) return;

        _isBluetoothNotificationVisible = false;
        _bluetoothNotificationToken = 0;
        _bluetoothController.MarkDismissed();

        BluetoothNotification.BeginAnimation(OpacityProperty, null);
        BluetoothNotificationTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        BluetoothNotification.Opacity = 0;
        BluetoothNotification.Visibility = Visibility.Collapsed;

        BluetoothDisconnectNotification.BeginAnimation(OpacityProperty, null);
        BluetoothDisconnectNotificationTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        BluetoothDisconnectNotification.Opacity = 0;
        BluetoothDisconnectNotification.Visibility = Visibility.Collapsed;
    }

    private void PlayBluetoothBounce()
    {
        if (_isExpanded || _isAnimating) return;

        var durPeak = TimeSpan.FromMilliseconds(140);
        var durEnd = TimeSpan.FromMilliseconds(700);

        var bounceX = new DoubleAnimationUsingKeyFrames();
        bounceX.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, KeyTime.FromTimeSpan(durPeak), _easeQuadOut));
        bounceX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(durEnd), _easeSoftSpring));
        Timeline.SetDesiredFrameRate(bounceX, VNotch.Services.AnimationConfig.TargetFps);

        var bounceY = new DoubleAnimationUsingKeyFrames();
        bounceY.KeyFrames.Add(new EasingDoubleKeyFrame(0.94, KeyTime.FromTimeSpan(durPeak), _easeQuadOut));
        bounceY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(durEnd), _easeSoftSpring));
        Timeline.SetDesiredFrameRate(bounceY, VNotch.Services.AnimationConfig.TargetFps);

        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceX);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceY);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceX);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceY);
    }

    #endregion
}
