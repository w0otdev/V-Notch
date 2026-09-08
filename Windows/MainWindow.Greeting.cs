using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Greeting Animation (Apple-style "Hello" handwriting on startup)

    private bool _isGreetingActive = false;
    private DispatcherTimer? _greetingDismissTimer;
    private bool _isVietnameseGreeting = false;

    private void PlayGreetingAnimation()
    {
        if (GreetingOverlay == null || HelloPath1 == null || HelloPath2 == null) return;

        _isGreetingActive = true;
        _isAnimating = true;
        _isVietnameseGreeting = Loc.CurrentLanguage == "vi";

        CollapsedContent.Opacity = 0;
        CollapsedContent.Visibility = Visibility.Collapsed;
        MusicCompactContent.Opacity = 0;
        MusicCompactContent.Visibility = Visibility.Collapsed;
        ExpandedContent.Opacity = 0;
        ExpandedContent.Visibility = Visibility.Collapsed;
        SecondaryContent.Opacity = 0;
        SecondaryContent.Visibility = Visibility.Collapsed;
        TimerContent.Opacity = 0;
        TimerContent.Visibility = Visibility.Collapsed;
        PrivacyIndicatorPanel.Opacity = 0;
        PrivacyIndicatorPanel.Visibility = Visibility.Collapsed;
        BluetoothNotification.Opacity = 0;
        BluetoothNotification.Visibility = Visibility.Collapsed;
        BluetoothDisconnectNotification.Opacity = 0;
        BluetoothDisconnectNotification.Visibility = Visibility.Collapsed;
        ChargingNotification.Opacity = 0;
        ChargingNotification.Visibility = Visibility.Collapsed;
        VolumeIndicatorContainer.Opacity = 0;
        VolumeIndicatorContainer.Visibility = Visibility.Collapsed;

        GreetingOverlay.Visibility = Visibility.Visible;
        GreetingOverlay.Opacity = 1;

        if (_isVietnameseGreeting)
        {
            HelloPathContainer.Visibility = Visibility.Collapsed;
            XinChaoPathContainer.Visibility = Visibility.Visible;

            PreparePath(ViPath1);
            PreparePath(ViPath2);
            PreparePath(ViPath3);
            PreparePath(ViPath4);
            PreparePath(ViPath5);
            PreparePath(ViPath6);
            PreparePath(ViPath7);
            PreparePath(ViPath8);
            PreparePath(ViPath9);
            PreparePath(ViPath10);
        }
        else
        {
            HelloPathContainer.Visibility = Visibility.Visible;
            XinChaoPathContainer.Visibility = Visibility.Collapsed;

            PreparePath(HelloPath1);
            PreparePath(HelloPath2);
        }

        NotchBorder.BeginAnimation(WidthProperty, null);
        NotchBorder.BeginAnimation(HeightProperty, null);
        NotchBorder.Width = _collapsedWidth;
        NotchBorder.Height = _collapsedHeight;

        var widthAnim = MakeAnim(_expandedWidth, _dur600, _easeExpOut6, VNotch.Services.AnimationConfig.TargetFps);
        var heightAnim = MakeAnim(_expandedHeight, _dur600, _easeExpOut6, VNotch.Services.AnimationConfig.TargetFps);

        AnimateCornerRadius(_cornerRadiusExpanded, TimeSpan.FromMilliseconds(400));

        heightAnim.Completed += (s, e) =>
        {
            if (_isVietnameseGreeting)
                PlayXinChaoStrokeAnimation();
            else
                PlayHelloStrokeAnimation();
        };

        NotchBorder.BeginAnimation(WidthProperty, widthAnim);
        NotchBorder.BeginAnimation(HeightProperty, heightAnim);
    }

    private void PlayXinChaoStrokeAnimation()
    {
        var paths = new (Path path, double durationMs, double delayMs)[]
        {
            (ViPath1,  110,    0),
            (ViPath2,  230,  130),
            (ViPath3,  210,  340),
            (ViPath4,  150,  520),
            (ViPath5,  400,  640),
            (ViPath6,  520, 1010),
            (ViPath7,  500, 1490),
            (ViPath8,  560, 1950),
            (ViPath9,  900, 2460),
            (ViPath10, 480, 3420),
        };

        foreach (var (path, durationMs, delayMs) in paths)
        {
            double pathLength = (double)path.Tag;

            if (delayMs > 0)
            {
                path.Opacity = 0;
                var showTimer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(delayMs)
                };
                var capturedPath = path;
                showTimer.Tick += (s, e) =>
                {
                    ((DispatcherTimer)s!).Stop();
                    capturedPath.Opacity = 1;
                };
                showTimer.Start();
            }
            else
            {
                path.Opacity = 1;
            }

            var anim = new DoubleAnimation
            {
                From = pathLength,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };

            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);
            path.BeginAnimation(Shape.StrokeDashOffsetProperty, anim);
        }

        var totalDurationMs = 3420 + 480 + 1500;

        ViDotI.Visibility = Visibility.Visible;
        ViDotI.Opacity = 0;
        var dotFadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(250),
            BeginTime = TimeSpan.FromMilliseconds(340),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(dotFadeIn, VNotch.Services.AnimationConfig.TargetFps);
        ViDotI.BeginAnimation(OpacityProperty, dotFadeIn);

        _greetingDismissTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(totalDurationMs)
        };
        _greetingDismissTimer.Tick += (s, e) =>
        {
            _greetingDismissTimer.Stop();
            _greetingDismissTimer = null;
            DismissGreeting();
        };
        _greetingDismissTimer.Start();
    }

    private static void PreparePath(Path path)
    {
        path.Visibility = Visibility.Visible;

        var geometry = path.Data.GetFlattenedPathGeometry();
        double length = GetPathLength(geometry);
        double normalizedLength = length / path.StrokeThickness;

        path.StrokeDashCap = PenLineCap.Flat;
        path.StrokeStartLineCap = PenLineCap.Flat;
        path.StrokeEndLineCap = PenLineCap.Flat;
        path.StrokeDashArray = new DoubleCollection { normalizedLength, normalizedLength * 3 };
        path.StrokeDashOffset = normalizedLength;
        path.Tag = normalizedLength;
    }

    private static double GetPathLength(PathGeometry geometry)
    {
        double totalLength = 0;

        foreach (var figure in geometry.Figures)
        {
            var lastPoint = figure.StartPoint;

            foreach (var segment in figure.Segments)
            {
                if (segment is PolyLineSegment polyLine)
                {
                    foreach (var point in polyLine.Points)
                    {
                        totalLength += (point - lastPoint).Length;
                        lastPoint = point;
                    }
                }
                else if (segment is LineSegment line)
                {
                    totalLength += (line.Point - lastPoint).Length;
                    lastPoint = line.Point;
                }
            }
        }

        return totalLength;
    }

    private void PlayHelloStrokeAnimation()
    {
        double path1Length = (double)HelloPath1.Tag;
        double path2Length = (double)HelloPath2.Tag;

        var path1Anim = new DoubleAnimation
        {
            From = path1Length,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(900),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };

        var path2Anim = new DoubleAnimation
        {
            From = path2Length,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(3000),
            BeginTime = TimeSpan.FromMilliseconds(700),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };

        path2Anim.Completed += (s, e) =>
        {
            _greetingDismissTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            _greetingDismissTimer.Tick += (s2, e2) =>
            {
                _greetingDismissTimer.Stop();
                _greetingDismissTimer = null;
                DismissGreeting();
            };
            _greetingDismissTimer.Start();
        };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(path1Anim, VNotch.Services.AnimationConfig.TargetFps);
        HelloPath1.BeginAnimation(Shape.StrokeDashOffsetProperty, path1Anim);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(path2Anim, VNotch.Services.AnimationConfig.TargetFps);
        HelloPath2.BeginAnimation(Shape.StrokeDashOffsetProperty, path2Anim);
    }

    private void DismissGreeting()
    {
        var fadeOut = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(500),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        fadeOut.Completed += (s, e) =>
        {
            GreetingOverlay.Visibility = Visibility.Collapsed;
            GreetingOverlay.Opacity = 0;

            if (_isVietnameseGreeting)
            {
                ViPath1.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                ViPath2.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                ViPath3.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                ViPath4.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                ViPath5.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                ViPath6.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                ViPath7.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                ViPath8.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                ViPath9.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                ViPath10.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                ViDotI.BeginAnimation(OpacityProperty, null);
                ViDotI.Opacity = 0;
                ViDotI.Visibility = Visibility.Collapsed;
            }
            else
            {
                HelloPath1.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                HelloPath2.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            }

            CollapseAfterGreeting();
        };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOut, VNotch.Services.AnimationConfig.TargetFps);
        GreetingOverlay.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void CollapseAfterGreeting()
    {
        var widthAnim = MakeAnim(_collapsedWidth, _dur500, _easeExpOut6, VNotch.Services.AnimationConfig.TargetFps);
        var heightAnim = MakeAnim(_collapsedHeight, _dur500, _easeExpOut6, VNotch.Services.AnimationConfig.TargetFps);

        AnimateCornerRadius(_cornerRadiusCollapsed, TimeSpan.FromMilliseconds(350));

        heightAnim.Completed += (s, e) =>
        {
            _isAnimating = false;
            _isGreetingActive = false;
            RestorePrivacyDotVisibility();

            CollapsedContent.Visibility = Visibility.Visible;
            var restoreFade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(restoreFade, VNotch.Services.AnimationConfig.TargetFps);
            CollapsedContent.BeginAnimation(OpacityProperty, restoreFade);

            if (_isStartupLayoutReady)
            {
                _mediaService.Start();
                StartCoreModules();
            }
        };

        NotchBorder.BeginAnimation(WidthProperty, widthAnim);
        NotchBorder.BeginAnimation(HeightProperty, heightAnim);
    }

    #endregion
}
