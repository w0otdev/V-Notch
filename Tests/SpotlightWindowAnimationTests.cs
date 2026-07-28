using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;
using VNotch.Services.Spotlight.Providers;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SpotlightWindowAnimationCollection
{
    public const string Name = "Spotlight window animation";
}

[Collection(SpotlightWindowAnimationCollection.Name)]
public sealed class SpotlightWindowAnimationTests
{
    [Fact]
    public void RapidToggleAcrossViewShapes_DoesNotLeakStaleMorphState()
    {
        RunSta(() =>
        {
            bool originalReduceMotion = AnimationConfig.ReduceMotion;
            string usagePath = Path.Combine(
                Path.GetTempPath(), $"vnotch-spotlight-view-stress-{Guid.NewGuid():N}.json");
            Application? application = null;
            SpotlightWindow? window = null;

            try
            {
                application = CreateApplicationResources();
                AnimationConfig.SetReduceMotion(false);

                var service = new SpotlightSearchService([new DelayedProvider()]);
                var viewModel = new SpotlightViewModel(
                    service,
                    new SpotlightUsageStore(usagePath, () => DateTime.UtcNow));
                var morphHost = new FakeMorphHost();
                window = new SpotlightWindow(viewModel, new SpotlightLauncher())
                {
                    Opacity = 0
                };

                // First exercise the no-notch fallback fade. It has different
                // clocks from the geometric morph and used to reveal opacity 1
                // when an exit was reversed.
                window.ShowSpotlight();
                window.HideSpotlight();
                PumpUntil(() => !window.IsVisible, TimeSpan.FromSeconds(2));
                Assert.Equal(Visibility.Hidden, window.Shell.Visibility);
                Assert.Equal(Visibility.Hidden, window.NotchMorphSnapshot.Visibility);
                Assert.InRange(window.Shell.Opacity, 0, 0.001);

                window.ShowSpotlight();
                window.HideSpotlight();
                PumpFor(TimeSpan.FromMilliseconds(45));
                Assert.InRange(window.Shell.Opacity, 0.01, 0.99);
                window.ToggleFromHotkey();
                PumpFor(TimeSpan.FromMilliseconds(30));
                Assert.InRange(window.Shell.Opacity, 0.01, 0.99);
                window.DismissFromGlobalShortcut();
                window.DismissFromGlobalShortcut();

                window.MorphHostOverride = morphHost;

                var viewShapes = new[]
                {
                    new MorphRect(310, 0, 230, 32, 8, 8),       // collapsed
                    new MorphRect(95, 0, 660, 360, 24, 24),     // primary
                    new MorphRect(80, 0, 690, 390, 20, 20),     // secondary/timer
                    new MorphRect(65, 0, 720, 378, 18, 18)      // audio/camera
                };
                int[] entranceDelays = [15, 90, 300, 610];
                int[] reverseDelays = [15, 160, 400, 575];

                for (int i = 0; i < viewShapes.Length; i++)
                {
                    morphHost.Rect = viewShapes[i];
                    window.ShowSpotlight();
                    PumpFor(TimeSpan.FromMilliseconds(entranceDelays[i]));

                    Assert.True(window.IsVisible);
                    Assert.True(morphHost.MorphActive);

                    // Simulate the underlying notch changing view/size while
                    // Spotlight owns its visibility.
                    morphHost.Rect = viewShapes[(i + 1) % viewShapes.Length];
                    window.HideSpotlight();
                    PumpFor(TimeSpan.FromMilliseconds(reverseDelays[i]));

                    Assert.True(window.IsVisible);
                    double beforeOpacity = window.Shell.Opacity;
                    double beforeWidth = window.Shell.Width;
                    double beforeHeight = window.Shell.Height;
                    double beforeBlur = (window.ShellContent.Effect as BlurEffect)?.Radius ?? 0;

                    window.ToggleFromHotkey();

                    Assert.True(window.IsVisible);
                    Assert.True(morphHost.MorphActive);
                    AssertClose(beforeOpacity, window.Shell.Opacity, 0.08);
                    AssertClose(beforeWidth, window.Shell.Width, 3.0);
                    AssertClose(beforeHeight, window.Shell.Height, 3.0);
                    AssertClose(
                        beforeBlur,
                        (window.ShellContent.Effect as BlurEffect)?.Radius ?? 0,
                        1.0);

                    // Exercise exit -> entrance -> exit -> immediate finish. Any
                    // queued completion from an older generation must stay inert.
                    PumpFor(TimeSpan.FromMilliseconds(35));
                    window.ToggleFromHotkey();
                    PumpFor(TimeSpan.FromMilliseconds(35));
                    window.ToggleFromHotkey();
                    PumpFor(TimeSpan.FromMilliseconds(35));
                    window.DismissFromGlobalShortcut();
                    window.DismissFromGlobalShortcut();

                    Assert.False(window.IsVisible);
                    Assert.False(morphHost.MorphActive);
                    Assert.Equal(Visibility.Hidden, window.Shell.Visibility);
                    Assert.Equal(Visibility.Hidden, window.NotchMorphSnapshot.Visibility);
                    Assert.InRange(window.Shell.Opacity, 0, 0.001);
                }

                // Let every duration used above expire. Stale Completed handlers
                // must not resurrect a hidden window or reacquire the notch.
                PumpFor(TimeSpan.FromMilliseconds(850));
                Assert.False(window.IsVisible);
                Assert.False(morphHost.MorphActive);
                Assert.True(morphHost.ReturnHandoffCount >= 1);

                // A grace timer created for an older query must not reveal its
                // searching panel in the middle of a newer query's grace period.
                window.ShowSpotlight();
                PumpFor(TimeSpan.FromMilliseconds(620));
                window.SearchBox.Text = "old";
                PumpFor(TimeSpan.FromMilliseconds(180));
                window.SearchBox.Text = "new";
                PumpFor(TimeSpan.FromMilliseconds(100));
                Assert.Equal(Visibility.Collapsed, window.StatusPanel.Visibility);
                PumpFor(TimeSpan.FromMilliseconds(180));
                Assert.Equal(Visibility.Visible, window.StatusPanel.Visibility);
                window.DismissFromGlobalShortcut();
                window.DismissFromGlobalShortcut();
            }
            finally
            {
                window?.Shutdown();
                AnimationConfig.SetReduceMotion(originalReduceMotion);
                if (File.Exists(usagePath)) File.Delete(usagePath);
                application?.Shutdown();
            }
        });
    }

    [Fact]
    public void ExitWithVisibleResults_FreezesContentAndRestoresItOnReverse()
    {
        RunSta(() =>
        {
            bool originalReduceMotion = AnimationConfig.ReduceMotion;
            string usagePath = Path.Combine(
                Path.GetTempPath(), $"vnotch-spotlight-exit-freeze-{Guid.NewGuid():N}.json");
            Application? application = null;
            SpotlightWindow? window = null;

            try
            {
                application = CreateApplicationResources();
                AnimationConfig.SetReduceMotion(false);

                var service = new SpotlightSearchService([new InstantResultsProvider()]);
                var viewModel = new SpotlightViewModel(
                    service,
                    new SpotlightUsageStore(usagePath, () => DateTime.UtcNow));
                var morphHost = new FakeMorphHost();
                window = new SpotlightWindow(viewModel, new SpotlightLauncher())
                {
                    Opacity = 0
                };
                window.MorphHostOverride = morphHost;

                window.ShowSpotlight();
                PumpFor(TimeSpan.FromMilliseconds(700));
                window.SearchBox.Text = "app";
                PumpUntil(
                    () => window.ContentRegion.Visibility == Visibility.Visible,
                    TimeSpan.FromSeconds(3));
                PumpFor(TimeSpan.FromMilliseconds(320));

                // Closing with live results must freeze the region into a
                // fixed clipped box so the shrinking shell stops re-measuring
                // the list (and re-blurring it) on every animation tick.
                window.HideSpotlight();
                Assert.True(double.IsFinite(window.ContentRegion.Width));
                Assert.True(double.IsFinite(window.ContentRegion.Height));
                Assert.True(window.ContentRegion.ClipToBounds);
                Assert.Equal(HorizontalAlignment.Left, window.ContentRegion.HorizontalAlignment);
                // The exit must not start the blur ramp over live results;
                // that per-frame GPU pass is what starved the morph of frames.
                Assert.False(
                    HasActiveBlurAnimation(window),
                    "Blur ramp must not run over a live results panel.");

                // Reopening mid-close must hand the region back to auto layout
                // before the reopen target is measured.
                PumpFor(TimeSpan.FromMilliseconds(60));
                window.ToggleFromHotkey();
                Assert.True(double.IsNaN(window.ContentRegion.Width));
                Assert.True(double.IsNaN(window.ContentRegion.Height));
                Assert.False(window.ContentRegion.ClipToBounds);
                Assert.Equal(HorizontalAlignment.Stretch, window.ContentRegion.HorizontalAlignment);
                Assert.Equal(Visibility.Visible, window.ContentRegion.Visibility);

                // Let the reverse land, then run a full close: once the content
                // fade finishes the frozen region must leave layout entirely.
                PumpFor(TimeSpan.FromMilliseconds(700));
                window.ToggleFromHotkey();
                PumpFor(TimeSpan.FromMilliseconds(320));
                Assert.True(window.IsVisible);
                Assert.Equal(Visibility.Collapsed, window.ContentRegion.Visibility);

                PumpUntil(() => !window.IsVisible, TimeSpan.FromSeconds(3));
                Assert.True(double.IsNaN(window.ContentRegion.Width));
                Assert.True(double.IsNaN(window.ContentRegion.Height));
                Assert.False(window.ContentRegion.ClipToBounds);
                Assert.Equal(HorizontalAlignment.Stretch, window.ContentRegion.HorizontalAlignment);
            }
            finally
            {
                window?.Shutdown();
                AnimationConfig.SetReduceMotion(originalReduceMotion);
                if (File.Exists(usagePath)) File.Delete(usagePath);
                application?.Shutdown();
            }
        });
    }

    private static bool HasActiveBlurAnimation(SpotlightWindow window) =>
        window.ShellContent.Effect is BlurEffect blur
        && blur.HasAnimatedProperties;

    private static Application CreateApplicationResources()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources["SFProDisplay"] = new FontFamily("Segoe UI");
        application.Resources["SFProText"] = new FontFamily("Segoe UI");
        application.Resources["IconFont"] = new FontFamily("Segoe MDL2 Assets");
        return application;
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Spotlight animation did not complete.");
            PumpFor(TimeSpan.FromMilliseconds(10));
        }
    }

    private static void AssertClose(double expected, double actual, double tolerance)
    {
        Assert.True(
            double.IsFinite(expected) &&
            double.IsFinite(actual) &&
            Math.Abs(expected - actual) <= tolerance,
            $"Expected {actual:F3} to remain within {tolerance:F3} of {expected:F3}.");
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA test thread timed out.");
        if (failure != null) throw failure;
    }

    private readonly record struct MorphRect(
        double Left,
        double Top,
        double Width,
        double Height,
        double TopCornerRadius,
        double BottomCornerRadius);

    private sealed class FakeMorphHost : ISpotlightMorphHost
    {
        private readonly DrawingImage _snapshot;

        internal FakeMorphHost()
        {
            var drawing = new GeometryDrawing(
                Brushes.Black,
                null,
                new RectangleGeometry(new Rect(0, 0, 720, 400)));
            _snapshot = new DrawingImage(drawing);
            _snapshot.Freeze();
        }

        internal MorphRect Rect { get; set; } = new(310, 0, 230, 32, 8, 8);

        internal bool MorphActive { get; private set; }

        internal int ReturnHandoffCount { get; private set; }

        public (
            double Left,
            double Top,
            double Width,
            double Height,
            double TopCornerRadius,
            double BottomCornerRadius) GetSpotlightMorphRect() =>
            (Rect.Left, Rect.Top, Rect.Width, Rect.Height, Rect.TopCornerRadius, Rect.BottomCornerRadius);

        public ImageSource? CaptureSpotlightMorphVisual() => _snapshot;

        public void SetSpotlightMorphActive(bool active) => MorphActive = active;

        public void BeginSpotlightReturnHandoff(TimeSpan duration) => ++ReturnHandoffCount;
    }

    private sealed class InstantResultsProvider : ISpotlightProvider
    {
        public bool IsAvailable => true;

        public bool IsInstant => true;

        public Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<SpotlightSearchItem> items =
            [
                new($"test:{query}:1", SpotlightResultKind.Application,
                    $"{query} One", "Test app", "one.exe"),
                new($"test:{query}:2", SpotlightResultKind.File,
                    $"{query} Two", "Test file", @"C:\two.txt"),
                new($"test:{query}:3", SpotlightResultKind.Folder,
                    $"{query} Three", "Test folder", @"C:\three")
            ];
            return Task.FromResult(items);
        }
    }

    private sealed class DelayedProvider : ISpotlightProvider
    {
        public bool IsAvailable => true;

        public async Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            return Array.Empty<SpotlightSearchItem>();
        }
    }
}
