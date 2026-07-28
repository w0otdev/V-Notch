using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Update Notification Handlers

    private async void UpdateCheckTimer_Tick(object? sender, EventArgs e)
    {
        if (_updateService == null) return;
        await CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        int generation = System.Threading.Interlocked.Increment(ref _updateCheckGeneration);

        if (_isUpdateInstalling)
            return;

        try
        {
            var updateInfo = await _updateService.CheckForUpdatesAsync();

            // A later manual/timer check owns the UI state. Never let an older
            // response overwrite it, or alter the notification during install.
            if (generation != System.Threading.Volatile.Read(ref _updateCheckGeneration) ||
                _isUpdateInstalling)
            {
                return;
            }

            if (updateInfo != null && updateInfo.IsNewerVersion)
            {
                _isUpdateAvailable = true;
                _availableUpdate = updateInfo;
                ShowUpdateNotification();
            }
            else
            {
                _isUpdateAvailable = false;
                _availableUpdate = null;
                HideUpdateNotification();
            }
        }
        catch
        {
        }
    }

    private void ShowUpdateNotification()
    {
        if (_availableUpdate == null || !_availableUpdate.IsNewerVersion)
        {
            _isUpdateAvailable = false;
            HideUpdateNotification();
            return;
        }

        int visibilityGeneration = ++_updateVisibilityGeneration;
        bool wasVisible = UpdateNotificationButton.Visibility == Visibility.Visible;

        // A visible button may actually be halfway through a stale fade-out.
        // Remove that clock before restoring its visible base state.
        UpdateNotificationButton.BeginAnimation(OpacityProperty, null);
        UpdateNotificationTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        UpdateNotificationButton.Visibility = Visibility.Visible;
        UpdateNotificationButton.IsHitTestVisible = true;
        UpdateNotificationButton.Tag = Loc.Get("update.version", _availableUpdate?.Version?.ToString() ?? "-");
        UpdateNotificationButton.Cursor = Cursors.Hand;
        UpdateNotificationButton.Opacity = 1.0;
        UpdateNotificationTranslate.Y = 0;
        SetUpdateInlineTooltipContent(
            Loc.Get("update.version", _availableUpdate?.Version?.ToString() ?? "-"),
            Loc.Get("update.clickToInstall"));

        if (wasVisible)
        {
            if (!_isUpdateInstalling)
            {
                StartUpdatePulseAnimation();
            }
            return;
        }

        UpdateIconBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        UpdateIconBrush.Color = Color.FromRgb(48, 209, 88);

        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var slideIn = new DoubleAnimation
        {
            From = -4,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        fadeIn.Completed += (s, e) =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (visibilityGeneration != _updateVisibilityGeneration ||
                    !_isUpdateAvailable ||
                    _isUpdateInstalling ||
                    UpdateNotificationButton.Visibility != Visibility.Visible)
                {
                    return;
                }

                StartUpdatePulseAnimation();
            }), DispatcherPriority.Render);
        };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
        UpdateNotificationButton.BeginAnimation(OpacityProperty, fadeIn);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(slideIn, VNotch.Services.AnimationConfig.TargetFps);
        UpdateNotificationTranslate.BeginAnimation(TranslateTransform.YProperty, slideIn);
    }

    private void HideUpdateNotification()
    {
        int visibilityGeneration = ++_updateVisibilityGeneration;
        HideUpdateInlineTooltip();
        StopUpdatePulseAnimation();

        if (UpdateNotificationButton.Visibility == Visibility.Collapsed)
        {
            UpdateNotificationButton.BeginAnimation(OpacityProperty, null);
            UpdateNotificationButton.Opacity = 0;
            return;
        }

        UpdateNotificationButton.IsHitTestVisible = false;
        UpdateNotificationButton.Cursor = Cursors.Hand;

        var fadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        fadeOut.Completed += (s, e) =>
        {
            if (visibilityGeneration != _updateVisibilityGeneration || _isUpdateAvailable)
                return;

            UpdateNotificationButton.Visibility = Visibility.Collapsed;
            UpdateNotificationButton.BeginAnimation(OpacityProperty, null);
            UpdateNotificationButton.Opacity = 0;
        };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOut, VNotch.Services.AnimationConfig.TargetFps);
        UpdateNotificationButton.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void StartUpdatePulseAnimation()
    {
        if (UpdateIconBrush == null ||
            !_isUpdateAvailable ||
            _isUpdateInstalling ||
            UpdateNotificationButton.Visibility != Visibility.Visible)
        {
            return;
        }

        UpdateIconBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        UpdateIconBrush.Color = Color.FromRgb(48, 209, 88);

        if (_updatePulseTimer == null)
        {
            _updatePulseTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _updatePulseTimer.Tick += UpdatePulseTimer_Tick;
        }

        _updatePulseStartedAtUtc = DateTime.UtcNow;
        _updatePulseTimer.Start();
    }

    private void StopUpdatePulseAnimation()
    {
        if (_updatePulseTimer != null)
        {
            _updatePulseTimer.Stop();
        }

        if (UpdateIconBrush != null)
        {
            UpdateIconBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            UpdateIconBrush.Color = Color.FromRgb(48, 209, 88);
        }
    }

    private void UpdatePulseTimer_Tick(object? sender, EventArgs e)
    {
        if (UpdateIconBrush == null) return;

        if (!_isUpdateAvailable || UpdateNotificationButton.Visibility != Visibility.Visible)
        {
            StopUpdatePulseAnimation();
            return;
        }

        const double periodMs = 3000.0;
        var elapsedMs = (DateTime.UtcNow - _updatePulseStartedAtUtc).TotalMilliseconds;
        var phase = (elapsedMs % periodMs) / periodMs;
        var mix = 0.5 - (0.5 * Math.Cos(phase * Math.PI * 2.0));

        byte r = (byte)Math.Round(48 + ((255 - 48) * mix));
        byte g = (byte)Math.Round(209 + ((255 - 209) * mix));
        byte b = (byte)Math.Round(88 + ((255 - 88) * mix));
        UpdateIconBrush.Color = Color.FromRgb(r, g, b);
    }

    private async void UpdateNotification_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (_availableUpdate == null || _isUpdateInstalling) return;

        _isUpdateInstalling = true;
        UpdateNotificationButton.Tag = Loc.Get("update.preparing");
        UpdateNotificationButton.Cursor = Cursors.Wait;
        UpdateNotificationButton.Opacity = 0.95;
        StopUpdatePulseAnimation();
        SetUpdateInlineTooltipContent(Loc.Get("update.preparing"), Loc.Get("update.openingUpdater"));
        ShowUpdateInlineTooltip();

        var updateProgressWindow = new UpdateDownloadWindow();
        updateProgressWindow.SetIndeterminate(Loc.Get("update.preparing"));
        updateProgressWindow.Show();

        var downloadProgress = new Progress<double>(p =>
        {
            if (p < 0)
            {
                updateProgressWindow.SetIndeterminate(Loc.Get("update.downloading"));
                UpdateNotificationButton.Tag = Loc.Get("update.downloading");
                SetUpdateInlineTooltipContent(Loc.Get("update.downloading"), Loc.Get("update.pleaseWait"));
                return;
            }

            updateProgressWindow.SetStatus(Loc.Get("update.downloadingPercent", (int)p));
            updateProgressWindow.SetProgress(p);
            UpdateNotificationButton.Tag = Loc.Get("update.downloadingPercent", (int)p);
            SetUpdateInlineTooltipContent(Loc.Get("update.downloadingPercent", (int)p), Loc.Get("update.pleaseWait"));
        });

        try
        {
            var installed = await _updateService.DownloadAndInstallUpdateAsync(_availableUpdate, downloadProgress);

            if (!installed)
            {
                updateProgressWindow.Close();
                _isUpdateInstalling = false;
                UpdateNotificationButton.Tag = Loc.Get("update.version", _availableUpdate.Version);
                UpdateNotificationButton.Cursor = Cursors.Hand;
                UpdateNotificationButton.Opacity = 1.0;
                SetUpdateInlineTooltipContent(
                    Loc.Get("update.version", _availableUpdate.Version),
                    Loc.Get("update.clickToInstall"));
                StartUpdatePulseAnimation();
                MessageBox.Show(
                    Loc.Get("error.updateFailed"),
                    Loc.Get("error.updateFailedTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch
        {
            updateProgressWindow.Close();
            _isUpdateInstalling = false;
            UpdateNotificationButton.Tag = Loc.Get("update.version", _availableUpdate?.Version?.ToString() ?? "-");
            UpdateNotificationButton.Cursor = Cursors.Hand;
            UpdateNotificationButton.Opacity = 1.0;
            SetUpdateInlineTooltipContent(
                Loc.Get("update.version", _availableUpdate?.Version?.ToString() ?? "-"),
                Loc.Get("update.clickToInstall"));
            StartUpdatePulseAnimation();
            MessageBox.Show(
                Loc.Get("error.updateError"),
                Loc.Get("error.updateErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void UpdateNotification_MouseEnter(object sender, MouseEventArgs e)
    {
        _suspendTopmostUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);
        AnimateUpdateNotificationHover(true);
        ShowUpdateInlineTooltip();

        if (_availableUpdate != null && !_isUpdateInstalling)
        {
            UpdateNotificationButton.Tag = Loc.Get("update.version", _availableUpdate.Version);
            SetUpdateInlineTooltipContent(
                Loc.Get("update.version", _availableUpdate.Version),
                Loc.Get("update.clickToInstall"));
        }
    }

    private void UpdateNotification_MouseLeave(object sender, MouseEventArgs e)
    {
        HideUpdateInlineTooltip();
        AnimateUpdateNotificationHover(false);
    }

    private void UpdateNotification_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        _isUpdateTooltipOpen = true;
        _suspendTopmostUntilUtc = DateTime.UtcNow.AddMilliseconds(1500);
    }

    private void UpdateNotification_ToolTipClosing(object sender, ToolTipEventArgs e)
    {
        _isUpdateTooltipOpen = false;
        _suspendTopmostUntilUtc = DateTime.UtcNow.AddMilliseconds(220);
    }

    private void AnimateUpdateNotificationHover(bool isEnter)
    {
        if (isEnter)
            UpdateNotificationButton.CacheMode ??= new System.Windows.Media.BitmapCache(1.5);

        int fps = VNotch.Services.AnimationConfig.TargetFps;
        var scaleAnim = new DoubleAnimation
        {
            To = isEnter ? 1.10 : 1.0,
            Duration = TimeSpan.FromMilliseconds(isEnter ? 180 : 220),
            EasingFunction = isEnter
                ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.20 }
                : _easeQuadOut
        };

        if (!isEnter)
        {
            scaleAnim.Completed += (_, _) =>
            {
                if (!UpdateNotificationButton.IsMouseOver)
                    UpdateNotificationButton.CacheMode = null;
            };
        }

        Timeline.SetDesiredFrameRate(scaleAnim, fps);
        UpdateNotificationScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim, HandoffBehavior.SnapshotAndReplace);
        UpdateNotificationScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim, HandoffBehavior.SnapshotAndReplace);
    }

    private void SetUpdateInlineTooltipContent(string status, string hint)
    {
        UpdateInlineStatusText.Text = status;
        UpdateInlineHintText.Text = hint;
    }

    private void ShowUpdateInlineTooltip()
    {
        if (!_isUpdateAvailable || UpdateNotificationButton.Visibility != Visibility.Visible)
            return;

        _isUpdateTooltipOpen = true;
        _suspendTopmostUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);
        UpdateInlineTooltip.BeginAnimation(OpacityProperty, null);
        UpdateInlineTooltip.Visibility = Visibility.Visible;

        var fadeIn = new DoubleAnimation
        {
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
        UpdateInlineTooltip.BeginAnimation(OpacityProperty, fadeIn, HandoffBehavior.SnapshotAndReplace);
    }

    private void HideUpdateInlineTooltip()
    {
        _isUpdateTooltipOpen = false;
        _suspendTopmostUntilUtc = DateTime.UtcNow.AddMilliseconds(220);

        if (UpdateInlineTooltip.Visibility != Visibility.Visible) return;

        UpdateInlineTooltip.BeginAnimation(OpacityProperty, null);
        var fadeOut = new DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        fadeOut.Completed += (_, _) =>
        {
            UpdateInlineTooltip.Visibility = Visibility.Collapsed;
            UpdateInlineTooltip.Opacity = 0;
        };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOut, VNotch.Services.AnimationConfig.TargetFps);
        UpdateInlineTooltip.BeginAnimation(OpacityProperty, fadeOut, HandoffBehavior.SnapshotAndReplace);
    }

    #endregion
}
