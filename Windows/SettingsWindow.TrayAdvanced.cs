using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    private static readonly string SeenTraySettingsPath = Path.Combine(AppPaths.LocalAppDataRoot, "seen-tray-settings.txt");
    private readonly HashSet<string> _seenTraySettings = new(StringComparer.Ordinal);

    private void LoadTrayAdvanced(NotchSettings settings)
    {
        TraySensitiveCheck.IsChecked = settings.ClipboardAutoDeleteSensitive;
        TrayTextCheck.IsChecked = settings.ClipboardCaptureText;
        TrayImagesCheck.IsChecked = settings.ClipboardCaptureImages;
        TrayFilesCheck.IsChecked = settings.ClipboardCaptureFiles;
        TrayDelaySlider.Value = settings.ClipboardCaptureDelay;
    }

    private void LocalizeTrayAdvanced()
    {
        TrayAdvancedTitle.Text = Loc.Get("settings.trayAdvanced");
        NavFileTrayText.Text = Loc.Get("settings.trayAdvanced");
        TrayAdvancedHint.Text = Loc.Get("settings.trayAdvanced.hint");
        TraySensitiveCheck.Content = Loc.Get("settings.trayDeleteSensitive");
        TraySensitiveCheck.ToolTip = Loc.Get("settings.trayDeleteSensitive.hint");
        TrayTextLabel.Text = Loc.Get("settings.trayCaptureText");
        TrayImagesLabel.Text = Loc.Get("settings.trayCaptureImages");
        TrayFilesLabel.Text = Loc.Get("settings.trayCaptureFiles");
        TrayDelaySlider.Label = Loc.Get("settings.trayCaptureDelay");
        TrayDelaySlider.Description = Loc.Get("settings.trayCaptureDelay.hint");
    }

    private void TrayAdvanced_Changed(object sender, RoutedEventArgs e) => PushLivePreview();
    private void TrayDelay_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => PushLivePreview();

    private void InitializeTrayBadges()
    {
        try
        {
            if (File.Exists(SeenTraySettingsPath))
                _seenTraySettings.UnionWith(File.ReadAllLines(SeenTraySettingsPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Intentionally ignored. */ }
        AddTrayBadge(TrayTextBadgeHost, "capture-text-v1", TrayTextRow);
        AddTrayBadge(TrayImagesBadgeHost, "capture-images-v1", TrayImagesRow);
        AddTrayBadge(TrayFilesBadgeHost, "capture-files-v1", TrayFilesRow);
        AddTrayBadge(TrayDelayRow, "capture-delay-v1");
        if (NavFileTray.Child is StackPanel nav)
            AddTrayBadge(nav, "file-tray-v1", NavFileTray);
    }

    private void AddTrayBadge(Panel panel, string key, FrameworkElement? hoverTarget = null)
    {
        if (_seenTraySettings.Contains(key)) return;
        var scale = new ScaleTransform(1, 1);
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(157, 218, 184)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 2, 5, 2),
            Margin = new Thickness(8, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = panel is WrapPanel ? VerticalAlignment.Center : VerticalAlignment.Top,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale,
            Child = new TextBlock
            {
                Text = "NEW",
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(21, 48, 34))
            }
        };
        bool dismissed = false;
        DoubleAnimation Motion(double from, double to, int milliseconds)
        {
            var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 },
                FillBehavior = FillBehavior.Stop
            };
            Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
            return animation;
        }
        void StopMotion()
        {
            badge.BeginAnimation(OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
        badge.IsVisibleChanged += (_, _) =>
        {
            if (!badge.IsVisible)
            {
                StopMotion();
                if (dismissed) badge.Visibility = Visibility.Collapsed;
                return;
            }
            if (dismissed) return;
            StopMotion();
            if (AnimationConfig.ReduceMotion) return;
            badge.BeginAnimation(OpacityProperty, Motion(0, 1, 300));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion(0.86, 1, 300));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion(0.86, 1, 300));
        };
        panel.Children.Add(badge);
        var target = hoverTarget ?? panel;
        void Dismiss()
        {
            if (!_seenTraySettings.Add(key)) return;
            dismissed = true;
            double opacity = badge.Opacity;
            double size = scale.ScaleX;
            StopMotion();
            if (AnimationConfig.ReduceMotion || !badge.IsVisible)
                badge.Visibility = Visibility.Collapsed;
            else
            {
                badge.Opacity = 0;
                var fade = Motion(opacity, 0, 180);
                fade.Completed += (_, _) => badge.Visibility = Visibility.Collapsed;
                badge.BeginAnimation(OpacityProperty, fade);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion(size, 0.9, 180));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion(size, 0.9, 180));
            }
            string tempPath = SeenTraySettingsPath + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SeenTraySettingsPath)!);
                File.WriteAllLines(tempPath, _seenTraySettings);
                File.Move(tempPath, SeenTraySettingsPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (IOException) { /* Intentionally ignored. */ }
            }
        }
        target.MouseEnter += (_, _) => Dismiss();
        target.GotKeyboardFocus += (_, _) => Dismiss();
    }
}
