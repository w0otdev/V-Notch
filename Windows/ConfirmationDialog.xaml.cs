using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using VNotch.Services;

namespace VNotch.Windows;

public partial class ConfirmationDialog : Window
{
    public enum DialogIcon
    {
        Warning,
        Question,
        Error,
        Info,
        Trash
    }

    public enum DialogStyle
    {
        Normal,
        Danger
    }

    public bool Confirmed { get; private set; }
    private bool _isClosing = false;

    private const string TrashIconPathData = "M410.886,43.93H301.533C299.778,19.793,280.576,0.093,256.005,0c-24.598,0.093-43.8,19.793-45.556,43.93H101.115c-22.787,0-41.407,18.628-41.407,41.398v1.792v15.543v14.822c0,5.692,4.648,10.35,10.34,10.35h0.674l23.859,342.87C96.152,493.408,116.075,512,138.853,512h75.745c22.76,0,60.027,0,82.814,0h75.726c22.769,0,42.701-18.592,44.281-41.296l23.84-342.87h0.675c5.702,0,10.358-4.658,10.358-10.35v-14.822V87.12v-1.792C452.292,62.558,433.654,43.93,410.886,43.93z";
    private const string WarningFilledPathData = "M12,1.67 C12.955,1.67 13.845,2.137 14.39,2.917 L14.495,3.077 L22.609,16.625 C23.63,18.33 22.4,20.99 20.302,21 L4.077,21 C1.979,20.99 0.749,18.33 1.77,16.625 L9.88,3.087 C10.425,2.137 11.315,1.67 12,1.67 Z M12.01,15 L11.883,15.007 A1,1 0 0,0 12.01,17 A1,1 0 0,0 12.01,15 Z M12,8 A1,1 0 0,0 11.007,8.883 L11,9 L11,13 A1,1 0 0,0 13,13 L13,9 A1,1 0 0,0 12,8 Z";

    public ConfirmationDialog()
    {
        InitializeComponent();
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.GetCulture().IetfLanguageTag);
        Title = Loc.Get("dialog.confirm.title");
        TitleText.Text = Loc.Get("dialog.confirm.title");
        ConfirmButton.Content = Loc.Get("dialog.confirm");
        CancelButton.Content = Loc.Get("dialog.cancel");
    }

    /// <summary>
    /// Show a confirmation dialog matching native V-Notch Settings design
    /// </summary>
    public static bool Show(
        Window? owner,
        string message,
        string title = "",
        string confirmText = "",
        string cancelText = "",
        DialogIcon icon = DialogIcon.Warning,
        DialogStyle style = DialogStyle.Normal,
        string? detailText = null)
    {
        try
        {
            var dialog = new ConfirmationDialog();

            if (owner != null && owner.IsVisible)
            {
                dialog.Owner = owner;
            }

            // Set title
            dialog.TitleText.Text = string.IsNullOrEmpty(title) ? Loc.Get("dialog.confirm.title") : title;

            // Set message
            dialog.MessageText.Text = message;

            // Set detail text in card if provided
            if (!string.IsNullOrEmpty(detailText))
            {
                dialog.DetailText.Text = detailText;
                dialog.DetailCard.Visibility = Visibility.Visible;
            }

            // Set button text
            dialog.ConfirmButton.Content = string.IsNullOrEmpty(confirmText) ? Loc.Get("dialog.confirm") : confirmText;
            dialog.CancelButton.Content = string.IsNullOrEmpty(cancelText) ? Loc.Get("dialog.cancel") : cancelText;

            // Set button style
            if (style == DialogStyle.Danger)
            {
                dialog.ConfirmButton.Style = (Style)dialog.FindResource("DangerButton");
            }

            // Set icon
            dialog.SetIcon(icon);

            dialog.ShowDialog();
            return dialog.Confirmed;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("CONFIRM-DIALOG", ex, "ConfirmationDialog.Show failed, falling back to MessageBox");
            var combinedMessage = string.IsNullOrEmpty(detailText) ? message : $"{message}\n\n{detailText}";
            var dlgTitle = string.IsNullOrEmpty(title) ? Loc.Get("dialog.confirm.title") : title;
            var msgBoxIcon = icon switch
            {
                DialogIcon.Error => MessageBoxImage.Error,
                DialogIcon.Question => MessageBoxImage.Question,
                DialogIcon.Info => MessageBoxImage.Information,
                _ => MessageBoxImage.Warning
            };

            var result = MessageBox.Show(combinedMessage, dlgTitle, MessageBoxButton.OKCancel, msgBoxIcon);
            return result == MessageBoxResult.OK;
        }
    }

    private void SetIcon(DialogIcon icon)
    {
        switch (icon)
        {
            case DialogIcon.Warning:
                DialogIconPath.Fill = Brushes.White;
                DialogIconPath.Stroke = null;
                DialogIconPath.StrokeThickness = 0;
                DialogIconPath.Data = Geometry.Parse(WarningFilledPathData);
                break;

            case DialogIcon.Trash:
            case DialogIcon.Question:
                DialogIconPath.Fill = Brushes.White;
                DialogIconPath.Stroke = null;
                DialogIconPath.StrokeThickness = 0;
                DialogIconPath.Data = Geometry.Parse(TrashIconPathData);
                break;

            case DialogIcon.Error:
                DialogIconPath.Fill = null;
                DialogIconPath.Stroke = new SolidColorBrush(Color.FromRgb(217, 56, 58));
                DialogIconPath.StrokeThickness = 2;
                DialogIconPath.StrokeStartLineCap = PenLineCap.Round;
                DialogIconPath.StrokeEndLineCap = PenLineCap.Round;
                DialogIconPath.StrokeLineJoin = PenLineJoin.Round;
                DialogIconPath.Data = Geometry.Parse("M12,2 C6.48,2 2,6.48 2,12 C2,17.52 6.48,22 12,22 C17.52,22 22,17.52 22,12 C22,6.48 17.52,2 12,2 Z M15,9 L9,15 M9,9 L15,15");
                break;

            case DialogIcon.Info:
            default:
                DialogIconPath.Fill = null;
                DialogIconPath.Stroke = Brushes.White;
                DialogIconPath.StrokeThickness = 2;
                DialogIconPath.StrokeStartLineCap = PenLineCap.Round;
                DialogIconPath.StrokeEndLineCap = PenLineCap.Round;
                DialogIconPath.StrokeLineJoin = PenLineJoin.Round;
                DialogIconPath.Data = Geometry.Parse("M12,2 C6.48,2 2,6.48 2,12 C2,17.52 6.48,22 12,22 C17.52,22 22,17.52 22,12 C22,6.48 17.52,2 12,2 Z M12,7 L12,7.01 M12,11 L12,17");
                break;
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var easeOut = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
        var dur = TimeSpan.FromMilliseconds(280);
        int fps = AnimationConfig.TargetFps;

        var scaleX = new DoubleAnimation(0.9, 1.0, dur) { EasingFunction = easeOut };
        var scaleY = new DoubleAnimation(0.9, 1.0, dur) { EasingFunction = easeOut };
        var transY = new DoubleAnimation(12.0, 0.0, dur) { EasingFunction = easeOut };
        var opacity = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(220)) { EasingFunction = easeOut };

        Timeline.SetDesiredFrameRate(scaleX, fps);
        Timeline.SetDesiredFrameRate(scaleY, fps);
        Timeline.SetDesiredFrameRate(transY, fps);
        Timeline.SetDesiredFrameRate(opacity, fps);

        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        CardTranslate.BeginAnimation(TranslateTransform.YProperty, transY);
        DialogCard.BeginAnimation(UIElement.OpacityProperty, opacity);
    }

    private void CloseWithAnimation(bool confirmed)
    {
        if (_isClosing) return;
        _isClosing = true;

        Confirmed = confirmed;

        var easeIn = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 6 };
        var dur = TimeSpan.FromMilliseconds(180);
        int fps = AnimationConfig.TargetFps;

        var scaleX = new DoubleAnimation(1.0, 0.92, dur) { EasingFunction = easeIn };
        var scaleY = new DoubleAnimation(1.0, 0.92, dur) { EasingFunction = easeIn };
        var transY = new DoubleAnimation(0.0, 8.0, dur) { EasingFunction = easeIn };
        var opacity = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(160)) { EasingFunction = easeIn };

        Timeline.SetDesiredFrameRate(scaleX, fps);
        Timeline.SetDesiredFrameRate(scaleY, fps);
        Timeline.SetDesiredFrameRate(transY, fps);
        Timeline.SetDesiredFrameRate(opacity, fps);

        opacity.Completed += (s, e) =>
        {
            Close();
        };

        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        CardTranslate.BeginAnimation(TranslateTransform.YProperty, transY);
        DialogCard.BeginAnimation(UIElement.OpacityProperty, opacity);
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (!_isClosing)
        {
            e.Cancel = true;
            CloseWithAnimation(Confirmed);
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseWithAnimation(false);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CloseWithAnimation(true);
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        CloseWithAnimation(true);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseWithAnimation(false);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            int exStyle = Win32Interop.GetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE);
            Win32Interop.SetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE, exStyle | Win32Interop.WS_EX_TOOLWINDOW);
        }
    }
}
