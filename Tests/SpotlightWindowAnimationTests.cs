using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
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
    public void MorphLifecycle_AcrossViewShapesAndRestoredSearch_DoesNotLeakState()
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
                ExerciseMainWindowFreshCaptureAfterReturn();

                var service = new SpotlightSearchService([new DelayedProvider()]);
                var viewModel = new SpotlightViewModel(
                    service,
                    new SpotlightUsageStore(usagePath, () => DateTime.UtcNow));
                var morphHost = new FakeMorphHost();
                window = new SpotlightWindow(viewModel, new SpotlightLauncher())
                {
                    Opacity = 0,
                    SuppressForegroundActivationForTests = true
                };
                Assert.False(window.ShowActivated);

                // First exercise the no-notch fallback fade. It has different
                // clocks from the geometric morph and used to reveal opacity 1
                // when an exit was reversed.
                window.ShowSpotlight();
                window.HideSpotlight();
                PumpUntil(() => !window.IsSpotlightOpen, TimeSpan.FromSeconds(2));
                Assert.Equal(Visibility.Hidden, window.Shell.Visibility);
                Assert.Equal(Visibility.Hidden, window.NotchMorphSnapshot.Visibility);
                Assert.InRange(window.Shell.Opacity, 0, 0.001);

                window.ShowSpotlight();
                PumpUntil(
                    () => window.Shell.HasAnimatedProperties,
                    TimeSpan.FromSeconds(1));
                window.HideSpotlight();
                PumpFor(TimeSpan.FromMilliseconds(45));
                Assert.InRange(window.Shell.Opacity, 0.01, 0.99);
                window.ToggleFromHotkey();
                PumpFor(TimeSpan.FromMilliseconds(30));
                Assert.InRange(window.Shell.Opacity, 0.01, 0.99);
                window.DismissFromGlobalShortcut();
                window.DismissFromGlobalShortcut();

                window.MorphHostOverride = morphHost;
                window.IsVisibleChanged += (_, args) =>
                    morphHost.ObserveSpotlightVisibility(args.NewValue is true);

                var viewShapes = new[]
                {
                    new MorphRect(310, 0, 230, 32, 8, 8),       // collapsed
                    new MorphRect(125, 0, 600, 310.4, 24, 24),  // clock/source from the reported repro
                    new MorphRect(80, 0, 690, 390, 20, 20),     // secondary/timer
                    new MorphRect(65, 0, 720, 378, 18, 18)      // audio/camera
                };
                int[] entranceDelays = [15, 90, 300, 610];
                int[] reverseDelays = [15, 160, 400, 575];

                for (int i = 0; i < viewShapes.Length; i++)
                {
                    morphHost.Rect = viewShapes[i];
                    window.ShowSpotlight();
                    Assert.True(window.IsSpotlightOpen);
                    Assert.True(morphHost.SessionActive);
                    Assert.False(
                        morphHost.MorphActive,
                        "A fresh Show must not hide the source before the prepared snapshot frame is committed.");
                    Assert.Equal(Visibility.Visible, window.NotchMorphSnapshot.Visibility);
                    PumpUntil(
                        () => morphHost.MorphActive,
                        TimeSpan.FromSeconds(1));
                    PumpFor(TimeSpan.FromMilliseconds(entranceDelays[i]));

                    Assert.True(window.IsSpotlightOpen);
                    Assert.True(morphHost.SessionActive);
                    Assert.True(morphHost.MorphActive);
                    Assert.True(morphHost.SessionWasActiveWhenSnapshotCaptured);
                    Assert.Equal(0, morphHost.VisibleWithoutSessionCount);
                    Assert.Equal(Stretch.None, window.NotchMorphSnapshotBrush.Stretch);
                    Assert.True(double.IsNaN(window.NotchMorphSnapshot.Width));
                    Assert.True(double.IsNaN(window.NotchMorphSnapshot.Height));

                    if (i == 1)
                    {
                        // A fresh entrance from the reported 600x310.4 clock
                        // view must animate a rounded crop viewport. Stretching
                        // that bitmap independently to 720x66 is the visible
                        // squash that only the fresh (non-reverse) path used.
                        Assert.Equal(Visibility.Visible, window.NotchMorphSnapshot.Visibility);
                        Assert.NotNull(window.NotchMorphSnapshotBrush.ImageSource);
                        AssertClose(
                            window.Shell.ActualWidth,
                            window.NotchMorphSnapshot.ActualWidth,
                            2.0);
                        AssertClose(
                            window.Shell.ActualHeight,
                            window.NotchMorphSnapshot.ActualHeight,
                            2.0);
                        Assert.Equal(
                            window.Shell.CornerRadius,
                            window.NotchMorphSnapshot.CornerRadius);
                    }

                    // Simulate the underlying notch changing view/size while
                    // Spotlight owns its visibility.
                    morphHost.Rect = viewShapes[(i + 1) % viewShapes.Length];
                    window.HideSpotlight();
                    PumpFor(TimeSpan.FromMilliseconds(reverseDelays[i]));

                    Assert.True(
                        window.IsSpotlightOpen,
                        $"Exit completed before reverse for view index {i} after {reverseDelays[i]} ms.");
                    double beforeOpacity = window.Shell.Opacity;
                    double beforeWidth = window.Shell.Width;
                    double beforeHeight = window.Shell.Height;
                    double beforeBlur = (window.ShellContent.Effect as BlurEffect)?.Radius ?? 0;

                    window.ToggleFromHotkey();

                    Assert.True(window.IsSpotlightOpen);
                    Assert.True(morphHost.SessionActive);
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

                    Assert.False(window.IsSpotlightOpen);
                    Assert.False(morphHost.SessionActive);
                    Assert.False(morphHost.MorphActive);
                    Assert.Equal(Visibility.Hidden, window.Shell.Visibility);
                    Assert.Equal(Visibility.Hidden, window.NotchMorphSnapshot.Visibility);
                    Assert.InRange(window.Shell.Opacity, 0, 0.001);
                }

                // Let every duration used above expire. Stale Completed handlers
                // must not resurrect a hidden window or reacquire the notch.
                PumpFor(TimeSpan.FromMilliseconds(850));
                Assert.False(window.IsSpotlightOpen);
                Assert.False(morphHost.SessionActive);
                Assert.False(morphHost.MorphActive);
                Assert.Equal(0, morphHost.VisibleWithoutSessionCount);
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
                PumpFor(TimeSpan.FromMilliseconds(300));
                Assert.Equal(Visibility.Visible, window.StatusPanel.Visibility);
                window.DismissFromGlobalShortcut();
                window.DismissFromGlobalShortcut();

                // WPF permits only one Application per AppDomain. Keep all
                // Spotlight window animation scenarios in this STA test, but
                // replace the delayed-search window with an instant-results
                // window for the content-heavy exit/reopen coverage.
                window.Shutdown();
                window = null;
                ExerciseVisibleResultsMorph(ref window, usagePath);
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

    private static void ExerciseMainWindowFreshCaptureAfterReturn()
    {
        string settingsDirectory = Path.Combine(
            Path.GetTempPath(), $"vnotch-main-morph-test-{Guid.NewGuid():N}");
        string settingsPath = Path.Combine(settingsDirectory, "settings.json");
        ServiceProvider? provider = null;
        MainWindow? host = null;
        try
        {
            var services = new ServiceCollection();
            var configureServices = typeof(App).GetMethod(
                "ConfigureServices",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(configureServices);
            var appConfigurationHost =
                (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
            configureServices.Invoke(appConfigurationHost, [services]);
            services.AddSingleton<ISettingsService>(
                new SettingsService(settingsPath, _ => { }));
            provider = services.BuildServiceProvider();
            host = provider.GetRequiredService<MainWindow>();

            host.NotchBorder.Width = 600;
            host.NotchBorder.Height = 310.4;
            host.NotchBorder.Measure(new Size(600, 310.4));
            host.NotchBorder.Arrange(new Rect(0, 0, 600, 310.4));
            host.NotchBorder.UpdateLayout();

            host.NotchWrapper.Opacity = 0.84;
            host.NotchShadowWrapper.Opacity = 0.62;

            host.SetSpotlightMorphSessionActive(true);
            var firstSnapshot = Assert.IsAssignableFrom<BitmapSource>(
                host.CaptureSpotlightMorphVisual());
            AssertSnapshotHasContent(firstSnapshot);
            host.SetSpotlightMorphActive(true);

            var handoffDuration = TimeSpan.FromMilliseconds(40);
            host.BeginSpotlightReturnHandoff(handoffDuration);
            PumpFor(handoffDuration + TimeSpan.FromMilliseconds(80));
            host.SetSpotlightMorphActive(false);
            host.SetSpotlightMorphSessionActive(false);

            AssertClose(0.84, host.NotchWrapper.Opacity, 0.001);
            AssertClose(0.62, host.NotchShadowWrapper.Opacity, 0.001);
            AssertClose(1, host.NotchScale.ScaleX, 0.001);
            AssertClose(1, host.NotchScale.ScaleY, 0.001);
            AssertClose(1, host.NotchShadowScale.ScaleX, 0.001);
            AssertClose(1, host.NotchShadowScale.ScaleY, 0.001);

            // This is the exact boundary of a fresh second Spotlight entrance:
            // acquire the source view before Show(), then capture it. Completed
            // handoff clocks use HoldEnd by default, so their presented values can
            // look correct while stale clocks still own the properties. The next
            // session must commit and remove them before the new snapshot.
            host.SetSpotlightMorphSessionActive(true);
            Assert.False(host.NotchWrapper.HasAnimatedProperties);
            Assert.False(host.NotchShadowWrapper.HasAnimatedProperties);
            Assert.False(host.NotchScale.HasAnimatedProperties);
            Assert.False(host.NotchShadowScale.HasAnimatedProperties);

            var secondSnapshot = Assert.IsAssignableFrom<BitmapSource>(
                host.CaptureSpotlightMorphVisual());
            AssertSnapshotHasContent(secondSnapshot);
            AssertSnapshotsEqual(firstSnapshot, secondSnapshot);

            host.SetSpotlightMorphSessionActive(false);
        }
        finally
        {
            host?.Close();
            provider?.Dispose();
            if (Directory.Exists(settingsDirectory))
                Directory.Delete(settingsDirectory, recursive: true);
        }
    }

    private static void AssertSnapshotHasContent(BitmapSource source)
    {
        byte[] pixels = CopyPixels(source);
        Assert.Contains(
            Enumerable.Range(0, pixels.Length / 4).Select(i => pixels[(i * 4) + 3]),
            alpha => alpha != 0);
    }

    private static void AssertSnapshotsEqual(BitmapSource expected, BitmapSource actual)
    {
        Assert.Equal(expected.PixelWidth, actual.PixelWidth);
        Assert.Equal(expected.PixelHeight, actual.PixelHeight);
        Assert.Equal(CopyPixels(expected), CopyPixels(actual));
    }

    private static byte[] CopyPixels(BitmapSource source)
    {
        int stride = checked(source.PixelWidth * 4);
        var pixels = new byte[checked(stride * source.PixelHeight)];
        source.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void ExerciseVisibleResultsMorph(
        ref SpotlightWindow? window,
        string usagePath)
    {
        var service = new SpotlightSearchService([new InstantResultsProvider()]);
        var viewModel = new SpotlightViewModel(
            service,
            new SpotlightUsageStore(usagePath, () => DateTime.UtcNow));
        var morphHost = new FakeMorphHost();
        var activeWindow = new SpotlightWindow(viewModel, new SpotlightLauncher())
        {
            Opacity = 0,
            SuppressForegroundActivationForTests = true
        };
        Assert.False(activeWindow.ShowActivated);
        window = activeWindow;
        activeWindow.MorphHostOverride = morphHost;
        activeWindow.IsVisibleChanged += (_, args) =>
            morphHost.ObserveSpotlightVisibility(args.NewValue is true);

        activeWindow.ShowSpotlight();
        Assert.True(morphHost.SessionActive);
        Assert.Equal(0, morphHost.VisibleWithoutSessionCount);
        PumpFor(TimeSpan.FromMilliseconds(700));
        activeWindow.SearchBox.Text = "app";
        PumpUntil(
            () => activeWindow.ContentRegion.Visibility == Visibility.Visible,
            TimeSpan.FromSeconds(3));
        PumpFor(TimeSpan.FromMilliseconds(320));

        // Closing with live results must freeze the region into a
        // fixed clipped box so the shrinking shell stops re-measuring
        // the list (and re-blurring it) on every animation tick.
        activeWindow.HideSpotlight();
        Assert.True(double.IsFinite(activeWindow.ContentRegion.Width));
        Assert.True(double.IsFinite(activeWindow.ContentRegion.Height));
        Assert.True(activeWindow.ContentRegion.ClipToBounds);
        Assert.Equal(HorizontalAlignment.Left, activeWindow.ContentRegion.HorizontalAlignment);
        // The exit must not start the blur ramp over live results;
        // that per-frame GPU pass is what starved the morph of frames.
        Assert.False(
            HasActiveBlurAnimation(activeWindow),
            "Blur ramp must not run over a live results panel.");

        // Reopening mid-close must hand the region back to auto layout
        // before the reopen target is measured.
        PumpFor(TimeSpan.FromMilliseconds(60));
        activeWindow.ToggleFromHotkey();
        Assert.True(double.IsNaN(activeWindow.ContentRegion.Width));
        Assert.True(double.IsNaN(activeWindow.ContentRegion.Height));
        Assert.False(activeWindow.ContentRegion.ClipToBounds);
        Assert.Equal(HorizontalAlignment.Stretch, activeWindow.ContentRegion.HorizontalAlignment);
        Assert.True(morphHost.SessionActive);
        Assert.Equal(Visibility.Visible, activeWindow.ContentRegion.Visibility);

        // Let the reverse land, then run a full close: once the content
        // fade finishes the frozen region must leave layout entirely.
        PumpFor(TimeSpan.FromMilliseconds(700));
        activeWindow.ToggleFromHotkey();
        PumpFor(TimeSpan.FromMilliseconds(320));
        Assert.True(activeWindow.IsSpotlightOpen);
        Assert.Equal(Visibility.Collapsed, activeWindow.ContentRegion.Visibility);

        PumpUntil(() => !activeWindow.IsSpotlightOpen, TimeSpan.FromSeconds(3));
        Assert.True(double.IsNaN(activeWindow.ContentRegion.Width));
        Assert.True(double.IsNaN(activeWindow.ContentRegion.Height));
        Assert.False(activeWindow.ContentRegion.ClipToBounds);
        Assert.Equal(HorizontalAlignment.Stretch, activeWindow.ContentRegion.HorizontalAlignment);
        Assert.False(morphHost.SessionActive);

        // A normal focus-loss dismissal preserves the query. On the
        // next open an instant provider can publish synchronously from
        // SearchBox.Text's setter, before PlayEntrance is reached.
        // An auxiliary view can be taller than the empty 66px search bar but
        // shorter than the restored results panel. The fresh entrance must
        // reserve that final panel height while keeping its live content out
        // of rendering; otherwise the shell first shrinks to 66px and then
        // grows back to the results height after the morph lands.
        morphHost.Rect = new MorphRect(185, 0, 480, 154, 24, 24);
        for (int cycle = 0; cycle < 2; cycle++)
        {
            activeWindow.ShowSpotlight();
            Assert.True(morphHost.SessionActive);
            Assert.Equal(0, morphHost.VisibleWithoutSessionCount);
            PumpFor(TimeSpan.FromMilliseconds(700));
            activeWindow.SearchBox.Text = "app";
            PumpUntil(
                () => activeWindow.ContentRegion.Visibility == Visibility.Visible,
                TimeSpan.FromSeconds(3));
            PumpFor(TimeSpan.FromMilliseconds(320));
            activeWindow.HideSpotlight();
            PumpUntil(() => !activeWindow.IsSpotlightOpen, TimeSpan.FromSeconds(3));

            activeWindow.ShowSpotlight();
            Assert.True(morphHost.SessionActive);
            Assert.Equal(0, morphHost.VisibleWithoutSessionCount);
            Assert.Equal("app", activeWindow.SearchBox.Text);
            Assert.NotEmpty(viewModel.Results);
            Assert.Equal(Visibility.Hidden, activeWindow.ContentRegion.Visibility);
            Assert.True(double.IsFinite(activeWindow.ContentRegion.Width));
            Assert.True(double.IsFinite(activeWindow.ContentRegion.Height));
            PumpUntil(() => morphHost.MorphActive, TimeSpan.FromSeconds(1));

            // Let the exit's 170ms content fade finish before reversing. A
            // reserved Hidden region must not be collapsed by that stale fade
            // callback or the reverse will target the empty search-bar height.
            if (cycle == 0)
            {
                activeWindow.HideSpotlight();
                PumpFor(TimeSpan.FromMilliseconds(220));
                Assert.Equal(Visibility.Hidden, activeWindow.ContentRegion.Visibility);
                activeWindow.ToggleFromHotkey();
                Assert.Equal(Visibility.Hidden, activeWindow.ContentRegion.Visibility);
            }

            PumpFor(TimeSpan.FromMilliseconds(300));
            Assert.Equal(Visibility.Hidden, activeWindow.ContentRegion.Visibility);
            Assert.True(
                activeWindow.Shell.Height >= morphHost.Rect.Height - 1,
                $"Fresh entrance undershot its {morphHost.Rect.Height:F1}px auxiliary source " +
                $"to {activeWindow.Shell.Height:F1}px before results were revealed.");
            PumpUntil(
                () => activeWindow.ContentRegion.Visibility == Visibility.Visible,
                TimeSpan.FromSeconds(3));
            Assert.True(double.IsNaN(activeWindow.ContentRegion.Width));
            Assert.True(double.IsNaN(activeWindow.ContentRegion.Height));
            Assert.False(activeWindow.ContentRegion.ClipToBounds);
            Assert.Equal(HorizontalAlignment.Stretch, activeWindow.ContentRegion.HorizontalAlignment);

            activeWindow.HideSpotlight();
            PumpUntil(() => !activeWindow.IsSpotlightOpen, TimeSpan.FromSeconds(3));
            Assert.False(morphHost.SessionActive);
        }

        // Clearing a restored query while its entrance is still using the
        // frozen results reservation must retire that space smoothly after the
        // morph lands, rather than snapping straight from results height to
        // the empty 66px search bar.
        activeWindow.ShowSpotlight();
        Assert.Equal(Visibility.Hidden, activeWindow.ContentRegion.Visibility);
        Assert.True(double.IsFinite(activeWindow.ContentRegion.Height));
        activeWindow.SearchBox.Text = string.Empty;
        Assert.Empty(viewModel.Results);
        PumpUntil(() => double.IsNaN(activeWindow.Shell.Height), TimeSpan.FromSeconds(2));
        Assert.Equal(Visibility.Hidden, activeWindow.ContentRegion.Visibility);
        Assert.True(double.IsFinite(activeWindow.ContentRegion.Height));
        Assert.True(activeWindow.ContentRegion.Height > 0);
        PumpUntil(
            () => activeWindow.ContentRegion.Visibility == Visibility.Collapsed,
            TimeSpan.FromSeconds(1));
        activeWindow.HideSpotlight();
        PumpUntil(() => !activeWindow.IsSpotlightOpen, TimeSpan.FromSeconds(3));
    }

    private static bool HasActiveBlurAnimation(SpotlightWindow window) =>
        window.ShellContent.Effect is BlurEffect blur
        && blur.HasAnimatedProperties;

    private static Application CreateApplicationResources()
    {
        if (Application.Current != null) return Application.Current;
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "STA test thread timed out.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
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

        internal bool SessionActive { get; private set; }

        internal bool SessionWasActiveWhenSnapshotCaptured { get; private set; }

        internal int VisibleWithoutSessionCount { get; private set; }

        internal int ReturnHandoffCount { get; private set; }

        public (
            double Left,
            double Top,
            double Width,
            double Height,
            double TopCornerRadius,
            double BottomCornerRadius) GetSpotlightMorphRect() =>
            (Rect.Left, Rect.Top, Rect.Width, Rect.Height, Rect.TopCornerRadius, Rect.BottomCornerRadius);

        public ImageSource? CaptureSpotlightMorphVisual()
        {
            SessionWasActiveWhenSnapshotCaptured = SessionActive;
            return _snapshot;
        }

        public void SetSpotlightMorphSessionActive(bool active) => SessionActive = active;

        public void SetSpotlightMorphActive(bool active) => MorphActive = active;

        public void BeginSpotlightReturnHandoff(TimeSpan duration) => ++ReturnHandoffCount;

        internal void ObserveSpotlightVisibility(bool visible)
        {
            if (visible && !SessionActive) ++VisibleWithoutSessionCount;
        }
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
