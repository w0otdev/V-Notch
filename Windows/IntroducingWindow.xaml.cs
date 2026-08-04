using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using VNotch.Services;

namespace VNotch;

public partial class IntroducingWindow : Window
{
    public IntroducingWindow()
    {
        InitializeComponent();

        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.GetCulture().IetfLanguageTag);
        Title = Loc.Get("intro.headline");
        TitleText.Text = Loc.Get("intro.title");
        HeadlineText.Text = Loc.Get("intro.headline");
        BodyText.Text = Loc.Get("intro.body");
        GotItButton.Content = Loc.Get("intro.close");
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateLayout();

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            MainShell.Opacity = 0;
            ShellScale.ScaleX = 0.95;
            ShellScale.ScaleY = 0.95;
            ShellTranslate.Y = 15;

            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            var easeOutStrong = new PowerEase { EasingMode = EasingMode.EaseOut, Power = 3 };
            int fps = VNotch.Services.AnimationConfig.TargetFps;

            var opacityAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(500))
            {
                EasingFunction = easeOut
            };
            Timeline.SetDesiredFrameRate(opacityAnim, fps);

            var scaleXAnim = new DoubleAnimation(0.95, 1.0, TimeSpan.FromMilliseconds(500))
            {
                EasingFunction = easeOutStrong
            };
            Timeline.SetDesiredFrameRate(scaleXAnim, fps);

            var scaleYAnim = new DoubleAnimation(0.95, 1.0, TimeSpan.FromMilliseconds(500))
            {
                EasingFunction = easeOutStrong
            };
            Timeline.SetDesiredFrameRate(scaleYAnim, fps);

            var translateYAnim = new DoubleAnimation(15.0, 0.0, TimeSpan.FromMilliseconds(500))
            {
                EasingFunction = easeOutStrong
            };
            Timeline.SetDesiredFrameRate(translateYAnim, fps);

            opacityAnim.Completed += (s, ev) =>
            {
                var shadow = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = System.Windows.Media.Colors.Black,
                    BlurRadius = 30,
                    ShadowDepth = 0,
                    Opacity = 0.42
                };
                MainShell.Effect = shadow;
            };

            MainShell.BeginAnimation(OpacityProperty, opacityAnim);
            ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
            ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
            ShellTranslate.BeginAnimation(TranslateTransform.YProperty, translateYAnim);
        }));

        try
        {
            var gifPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Introduction", "spotlight.gif");
            if (System.IO.File.Exists(gifPath))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(gifPath, UriKind.Absolute);
                image.EndInit();
                WpfAnimatedGif.ImageBehavior.SetAnimatedSource(IntroGif, image);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("INTRO-WINDOW", ex, "Failed to load intro gif");
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseWindowWithAnimation();
    }

    private void GotItButton_Click(object sender, RoutedEventArgs e)
    {
        CloseWindowWithAnimation();
    }

    private void CloseWindowWithAnimation()
    {
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        var opacityAnim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = easeIn
        };
        Timeline.SetDesiredFrameRate(opacityAnim, fps);

        var scaleXAnim = new DoubleAnimation(1.0, 0.95, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = easeIn
        };
        Timeline.SetDesiredFrameRate(scaleXAnim, fps);

        var scaleYAnim = new DoubleAnimation(1.0, 0.95, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = easeIn
        };
        Timeline.SetDesiredFrameRate(scaleYAnim, fps);

        opacityAnim.Completed += (s, e) => Close();

        MainShell.BeginAnimation(OpacityProperty, opacityAnim);
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseWindowWithAnimation();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }
}
