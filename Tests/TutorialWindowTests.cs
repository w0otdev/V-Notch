using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class TutorialWindowTests
{
    [Fact]
    public void InvitationDoesNotTouchApp_AndExploreClosesWithoutStarting()
    {
        SharedStaTestRunner.Run(() =>
        {
            int changes = 0;
            var window = new IntroducingWindow(_ => changes++);
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            Assert.False(window.IsGuiding);
            Assert.Equal(0, changes);
            Click(window, "BackButton");
            Assert.True(closed);
            Assert.Equal(0, changes);
        });
    }

    [Fact]
    public void ConsentStartsRealSteps_BackAndSkipNavigate_AndFinishCloses()
    {
        SharedStaTestRunner.Run(() =>
        {
            var steps = new List<int>();
            var window = new IntroducingWindow(steps.Add);
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                Click(window, "NextButton");
                Assert.True(window.IsGuiding);
                Assert.Equal(new[] { 0 }, steps);
                Assert.False(GetButton(window, "BackButton").IsEnabled);
                Click(window, "NextButton");
                Click(window, "BackButton");
                Assert.Equal(new[] { 0, 1, 0 }, steps);
                window.SetProgress(true);
                Assert.Equal(Loc.Get("tour.live.done"), ((TextBlock)window.FindName("HintText")).Text);
                for (int i = 0; i < 6; i++) Click(window, "NextButton");
                Assert.True(closed);
            }
            finally { if (!closed) window.Close(); }
        });
    }

    [Fact]
    public void InvitationAndLiveControlsFitAllLocales()
    {
        SharedStaTestRunner.Run(() =>
        {
            try
            {
                foreach (var language in Loc.GetAvailableLanguages())
                {
                    Loc.SetLanguage(language.Code);
                    var window = new IntroducingWindow(_ => { });
                    try
                    {
                        for (int step = -1; step < 6; step++)
                        {
                            var root = (FrameworkElement)window.Content;
                            root.Measure(new Size(430, 350));
                            root.Arrange(new Rect(0, 0, 430, 350));
                            root.UpdateLayout();
                            foreach (string name in new[] { "BackButton", "NextButton", "CloseButton" })
                            {
                                var button = GetButton(window, name);
                                var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                                Assert.True(bounds.Width > 0 && bounds.Left >= 0 && bounds.Right <= 430 && bounds.Bottom <= 350);
                            }
                            Assert.DoesNotContain("tour.", ((TextBlock)window.FindName("HeadlineText")).Text);
                            if (step < 5) Click(window, "NextButton");
                        }
                    }
                    finally { window.Close(); }
                }
            }
            finally { Loc.SetLanguage("en"); }
        });
    }

    [Fact]
    public void GlowAttachesWithoutReplacingTheControlOrInterceptingInput()
    {
        SharedStaTestRunner.Run(() =>
        {
            var target = new Border { Width = 100, Height = 40, Background = Brushes.Black };
            var root = new AdornerDecorator { Child = target };
            root.Measure(new Size(120, 60));
            root.Arrange(new Rect(0, 0, 120, 60));
            root.UpdateLayout();
            var layer = AdornerLayer.GetAdornerLayer(target);
            Assert.NotNull(layer);
            var glow = new TutorialHighlightAdorner(target);
            layer.Add(glow);
            Assert.False(glow.IsHitTestVisible);
            Assert.Same(target, root.Child);
            Assert.Contains(glow, layer.GetAdorners(target));
            layer.Remove(glow);
            Assert.Null(layer.GetAdorners(target));
        });
    }

    [Fact]
    public void AnimatedNavigationCoalescesClicks_AndClosingCancelsPendingStep()
    {
        SharedStaTestRunner.Run(() =>
        {
            var steps = new List<int>();
            var window = new IntroducingWindow(steps.Add)
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000
            };
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                window.Show();
                Pump(50);
                Click(window, "NextButton");
                if (SystemParameters.ClientAreaAnimation && !AnimationConfig.ReduceMotion)
                    Click(window, "NextButton");
                Pump(450);
                Assert.Equal(new[] { 0 }, steps);
                Click(window, "NextButton");
                window.Close();
                Pump(450);
                Assert.True(closed);
                if (SystemParameters.ClientAreaAnimation && !AnimationConfig.ReduceMotion)
                    Assert.Equal(new[] { 0 }, steps);
            }
            finally { if (!closed) { window.Close(); Pump(250); } }
        });
    }

    [Fact]
    public void GlowIlluminatesTheCenterOfTheActualControl_NotOnlyItsCorners()
    {
        SharedStaTestRunner.Run(() =>
        {
            var target = new Border { Width = 100, Height = 40, Background = Brushes.Black, CornerRadius = new CornerRadius(10) };
            var root = new AdornerDecorator { Child = target };
            root.Measure(new Size(100, 40));
            root.Arrange(new Rect(0, 0, 100, 40));
            root.UpdateLayout();
            byte[] before = SampleCenter(root);
            var layer = AdornerLayer.GetAdornerLayer(target)!;
            var glow = new TutorialHighlightAdorner(target);
            layer.Add(glow);
            root.UpdateLayout();
            byte[] after = SampleCenter(root);
            Assert.True(after[0] > before[0] + 2, "The focus must illuminate the entire surface, including its center.");
            Assert.False(glow.IsHitTestVisible);
            layer.Remove(glow);
        });
    }

    [Fact]
    public void BreathingStopsForReducedMotionAndWhenTheFocusIsRemoved()
    {
        SharedStaTestRunner.Run(() =>
        {
            bool original = AnimationConfig.ReduceMotion;
            var target = new Border { Width = 100, Height = 40, Background = Brushes.Black };
            var root = new AdornerDecorator { Child = target };
            var window = new Window
            {
                Content = root,
                Width = 140,
                Height = 90,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000
            };
            try
            {
                AnimationConfig.SetReduceMotion(false);
                window.Show();
                var layer = AdornerLayer.GetAdornerLayer(target)!;
                var glow = new TutorialHighlightAdorner(target);
                layer.Add(glow);
                Pump(50);
                Assert.Equal(SystemParameters.ClientAreaAnimation, glow.IsBreathing);
                AnimationConfig.SetReduceMotion(true);
                Assert.False(glow.IsBreathing);
                AnimationConfig.SetReduceMotion(false);
                Assert.Equal(SystemParameters.ClientAreaAnimation, glow.IsBreathing);
                layer.Remove(glow);
                Pump(50); // WPF dispatches Unloaded after removing an adorner.
                Assert.False(glow.IsBreathing);
            }
            finally
            {
                window.Close();
                AnimationConfig.SetReduceMotion(original);
            }
        });
    }

    private static byte[] SampleCenter(Visual visual)
    {
        var bitmap = new RenderTargetBitmap(100, 40, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[100 * 40 * 4];
        bitmap.CopyPixels(pixels, 100 * 4, 0);
        return pixels.Skip((20 * 100 + 50) * 4).Take(4).ToArray();
    }

    [Fact]
    public void RepeatedPositionChecksSnapToStableAnchorWithoutWindowAnimation()
    {
        SharedStaTestRunner.Run(() =>
        {
            var window = new IntroducingWindow(_ => { })
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000
            };
            try
            {
                window.Show();
                Pump(50);
                window.MoveBeside(-9700, -9700);
                Assert.Equal(-9700, window.Left);
                Assert.Equal(-9700, window.Top);
                Pump(100);
                for (int i = 0; i < 10; i++)
                    window.MoveBeside(-9698, -9698);
                Pump(300);
                Assert.InRange(window.Left, -9700.5, -9699.5);
                Assert.InRange(window.Top, -9700.5, -9699.5);
            }
            finally { window.Close(); Pump(250); }
        });
    }

    [Fact]
    public void CompletedCrossfadeUnlocksNavigation_AndProgressTracksBothDirections()
    {
        SharedStaTestRunner.Run(() =>
        {
            var steps = new List<int>();
            var window = new IntroducingWindow(steps.Add)
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000
            };
            try
            {
                window.Show();
                Pump(40);
                bool animate = SystemParameters.ClientAreaAnimation && !AnimationConfig.ReduceMotion;
                Click(window, "NextButton");
                if (animate) Click(window, "NextButton");
                Pump(420);
                Assert.Equal(new[] { 0 }, steps);

                Click(window, "NextButton");
                if (animate) Click(window, "NextButton");
                Pump(420);
                Assert.Equal(new[] { 0, 1 }, steps);
                Assert.Equal(1, window.StepIndex);
                var progress = (ScaleTransform)window.FindName("ProgressScale");
                Assert.Equal(2d / IntroducingWindow.Steps.Length, progress.ScaleX, 4);
                var incoming = (FrameworkElement)window.FindName("StepContent");
                var outgoing = (FrameworkElement)window.FindName("OutgoingContent");
                Assert.Equal(1, incoming.Opacity);
                Assert.Equal(Visibility.Collapsed, outgoing.Visibility);
                Assert.False(DependencyPropertyHelper.GetValueSource(incoming, UIElement.OpacityProperty).IsAnimated);
                Assert.False(DependencyPropertyHelper.GetValueSource(progress, ScaleTransform.ScaleXProperty).IsAnimated);

                Click(window, "BackButton");
                Pump(420);
                Assert.Equal(new[] { 0, 1, 0 }, steps);
                Assert.Equal(1d / IntroducingWindow.Steps.Length, progress.ScaleX, 4);
            }
            finally { window.Close(); Pump(220); }
        });
    }

    [Fact]
    public void OutgoingPageKeepsTheScrolledViewportWidthDuringNavigation()
    {
        SharedStaTestRunner.Run(() =>
        {
            var window = new IntroducingWindow(_ => { })
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000
            };
            try
            {
                window.Show();
                Pump(40);
                Click(window, "NextButton");
                Pump(400);
                var body = (TextBlock)window.FindName("BodyText");
                var scroller = (ScrollViewer)window.FindName("StepScroller");
                body.Text = string.Join(" ", Enumerable.Repeat("A sufficiently long tutorial description.", 40));
                window.UpdateLayout();
                scroller.ScrollToVerticalOffset(30);
                window.UpdateLayout();
                double viewport = scroller.ViewportWidth;
                Click(window, "NextButton");
                if (SystemParameters.ClientAreaAnimation && !AnimationConfig.ReduceMotion)
                {
                    var outgoing = (FrameworkElement)window.FindName("OutgoingContent");
                    Assert.Equal(Visibility.Visible, outgoing.Visibility);
                    Assert.Equal(viewport, outgoing.Width, 2);
                }
            }
            finally { window.Close(); Pump(220); }
        });
    }

    [Fact]
    public void ReduceMotionDuringCrossfadeCommitsCurrentStepAndAcceptsNextClick()
    {
        SharedStaTestRunner.Run(() =>
        {
            bool original = AnimationConfig.ReduceMotion;
            IntroducingWindow? window = null;
            try
            {
                AnimationConfig.SetReduceMotion(false);
                window = new IntroducingWindow(_ => { })
                {
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000,
                    Top = -10000
                };
                window.Show();
                Pump(40);
                Click(window, "NextButton");
                AnimationConfig.SetReduceMotion(true);
                Assert.Equal(0, window.StepIndex);
                Assert.Equal(1d / IntroducingWindow.Steps.Length,
                    ((ScaleTransform)window.FindName("ProgressScale")).ScaleX, 4);
                Click(window, "NextButton");
                Assert.Equal(1, window.StepIndex);
            }
            finally
            {
                window?.Close();
                AnimationConfig.SetReduceMotion(original);
            }
        });
    }

    private static void Pump(int milliseconds)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds)
        };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static Button GetButton(Window window, string name) => (Button)window.FindName(name);
    private static void Click(Window window, string name) => GetButton(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
