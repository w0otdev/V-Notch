using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;
using VNotch.Services.Spotlight.Providers;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SpotlightSearchPresentationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SearchSuggestionDoesNotSurviveDismissalAndReopenIntoAi(bool reducedMotion) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reducedMotion);
        try
        {
            var settings = Settings();
            settings.SpotlightDefaultAi = true;
            using var fixture = new SpotlightWindowFixture(settings, searchProvider: new ResultsProvider());
            var window = fixture.Window;
            window.ShowSpotlight();
            Invoke(window, "ToggleAiMode");
            window.SearchBox.Text = "fixture";
            await WpfFrameWaiter.UntilAsync(() => window.AutocompleteText.Visibility == Visibility.Visible,
                "search suggestion shown", ct);
            window.DismissFromGlobalShortcut();
            await WpfFrameWaiter.UntilAsync(() => !window.IsSpotlightOpen && !Field<bool>(window, "_isClosing"),
                "search dismissed", ct);
            window.ShowSpotlight();
            Assert.True(Field<bool>(window, "_aiMode"));
            Assert.Equal(string.Empty, window.SearchBox.Text);
            Assert.Equal(Visibility.Collapsed, window.AutocompleteText.Visibility);
            Assert.Equal(Visibility.Visible, window.PlaceholderText.Visibility);
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_entranceActive"), "AI reopened", ct);
            Assert.Equal(Visibility.Collapsed, window.AutocompleteText.Visibility);
            Invoke(window, "ToggleAiMode");
            window.SearchBox.Text = "replacement";
            await WpfFrameWaiter.UntilAsync(() => window.AutocompleteText.Visibility == Visibility.Visible,
                "new search suggestion shown", ct);
            Assert.Equal("replacement", window.AutocompleteTypedRun.Text);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResultsAutocompleteAndKeyboardNavigationStayAlignedThroughReplacementQueries(bool reducedMotion) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reducedMotion);
        try
        {
            using var fixture = new SpotlightWindowFixture(Settings(), searchProvider: new ResultsProvider());
            var window = fixture.Window;
            window.ShowSpotlight();
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_entranceActive"), "search entrance", ct);
            window.SearchBox.Text = "fixture";
            var vm = Field<SpotlightViewModel>(window, "_viewModel");
            await WpfFrameWaiter.UntilAsync(() => vm.Results.Count == 8 && !vm.IsSearching, "fixture results published", ct);
            window.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, window.StatusPanel.Visibility);
            Assert.Equal(Visibility.Visible, window.AutocompleteText.Visibility);
            Assert.Equal("fixture", window.AutocompleteTypedRun.Text);
            Assert.Equal(" result 0", window.AutocompleteSuffixRun.Text);
            Assert.Equal(0, window.ResultsList.SelectedIndex);
            foreach (var (key, expected) in new[] { (Key.Down, 1), (Key.PageDown, 5), (Key.PageUp, 1), (Key.Up, 0), (Key.Up, 7), (Key.Down, 0) })
            {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                Invoke(window, "Window_PreviewKeyDown", window, args);
                Assert.True(args.Handled);
                Assert.True(expected == window.ResultsList.SelectedIndex, $"{key}: expected row {expected}, selected {window.ResultsList.SelectedIndex}");
                await WpfFrameWaiter.NextAsync(ct);
            }
            Invoke(window, "UpdateSelectionGlide");
            Assert.True(Field<bool>(window, "_glideVisible"));
            Assert.True(Field<Border>(window, "_selectionGlide").Width > 0);
            window.ResultsList.SelectedIndex = -1;
            Invoke(window, "MoveSelection", -4);
            Assert.Equal(7, window.ResultsList.SelectedIndex);
            Invoke(window, "MoveSelection", 4);
            Assert.Equal(7, window.ResultsList.SelectedIndex);
            window.ResultsList.SelectedIndex = -1;
            Invoke(window, "MoveSelection", 1);
            Assert.Equal(0, window.ResultsList.SelectedIndex);
            Invoke(window, "SetResultsDimmed", true, false);
            Assert.Equal(0.55, window.ResultsList.Opacity);
            Invoke(window, "SetResultsDimmed", false, true);
            await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.ResultsList.Opacity - 1) < 1e-6, "search rows restored", ct);
            window.SearchBox.Text = "fixture old";
            window.SearchBox.Text = "replacement";
            await WpfFrameWaiter.UntilAsync(() => vm.Query == "replacement" && !vm.IsSearching && vm.Results.Count == 8 && vm.Results[0].Title.StartsWith("replacement"), "latest query wins", ct);
            Assert.All(vm.Results, result => Assert.StartsWith("replacement", result.Title));
            window.SearchBox.Text = "";
            await WpfFrameWaiter.UntilAsync(() => vm.Results.Count == 0 && !vm.IsSearching, "query clear resets panel", ct);
            Assert.Equal(Visibility.Visible, window.PlaceholderText.Visibility);
            Assert.Equal(Visibility.Collapsed, window.AutocompleteText.Visibility);
            Invoke(window, "UpdateSelectionGlide");
            Assert.False(Field<bool>(window, "_glideVisible"));
            var toggle = new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), Environment.TickCount, Key.Tab) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Invoke(window, "Window_PreviewKeyDown", window, toggle);
            Assert.True(Field<bool>(window, "_aiMode"));
            var toggleBack = new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), Environment.TickCount, Key.Tab) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Invoke(window, "Window_PreviewKeyDown", window, toggleBack);
            Assert.False(Field<bool>(window, "_aiMode"));
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingSearchShowsGraceStatusAndQueuedEnterCannotLaunchAnEmptyResult(bool reducedMotion) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reducedMotion);
        try
        {
            var provider = new PendingProvider();
            using var fixture = new SpotlightWindowFixture(Settings(), searchProvider: provider);
            var window = fixture.Window;
            window.ShowSpotlight();
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_entranceActive"), "pending search entrance", ct);
            window.SearchBox.Text = "pending fixture";
            var vm = Field<SpotlightViewModel>(window, "_viewModel");
            await WpfFrameWaiter.UntilAsync(() => vm.IsSearching && provider.Request != null, "search request pending", ct);
            Invoke(window, "LaunchSelected");
            Assert.Equal("pending fixture", Field<string?>(window, "_pendingLaunchQuery"));
            await WpfFrameWaiter.UntilAsync(() => window.StatusPanel.Visibility == Visibility.Visible, "searching panel after grace", ct);
            Assert.Equal(Loc.Get("spotlight.searching"), window.StatusTitle.Text);
            Assert.Equal(Visibility.Visible, window.SearchActivity.Visibility);
            Assert.Equal(Visibility.Collapsed, window.StatusGlyph.Visibility);
            Assert.Equal(!reducedMotion, window.SearchActivityRotation.HasAnimatedProperties);
            if (!reducedMotion)
            {
                double angle = window.SearchActivityRotation.Angle;
                await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.SearchActivityRotation.Angle - angle) > 2,
                    "search spinner advances across rendered frames", ct);
            }
            provider.Request!.SetResult(Array.Empty<SpotlightSearchItem>());
            await WpfFrameWaiter.UntilAsync(() => !vm.IsSearching, "empty search finished", ct);
            Assert.Null(Field<string?>(window, "_pendingLaunchQuery"));
            Assert.Equal(Loc.Get("spotlight.unavailable"), window.StatusTitle.Text);
            Assert.Equal(Visibility.Visible, window.StatusWarningIcon.Visibility);
            Assert.False(window.StatusGlyph.HasAnimatedProperties);
            Assert.False(window.SearchActivityRotation.HasAnimatedProperties);
            Assert.Equal(Visibility.Collapsed, window.SearchActivity.Visibility);
            vm.IsWindowsSearchUnavailable = false;
            vm.HasNoResults = true;
            Invoke(window, "RefreshStatus");
            Assert.Equal(Loc.Get("spotlight.noResults"), window.StatusTitle.Text);
            Invoke(window, "LaunchSelected");
            Invoke(window, "PlayShake");
            if (!reducedMotion)
                await WpfFrameWaiter.UntilAsync(() => !window.ShellShake.HasAnimatedProperties, "empty search shake settled", ct);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(1, 1, true)]
    [InlineData(8, 8, false)]
    [InlineData(8, 8, true)]
    [InlineData(1, 8, false)]
    [InlineData(1, 8, true)]
    [InlineData(8, 1, false)]
    [InlineData(8, 1, true)]
    public void InstantResultsCanFinishGrowingWhileDeferredSearchIsPending(int initialCount, int count, bool reducedMotion) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reducedMotion);
        try
        {
            var deferred = new PendingProvider();
            using var fixture = new SpotlightWindowFixture(Settings(), searchProvider: new InstantResultsProvider(initialCount, count),
                deferredSearchProvider: deferred);
            var window = fixture.Window;
            var vm = Field<SpotlightViewModel>(window, "_viewModel");
            window.ShowSpotlight();
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_entranceActive"), "instant results entrance", ct);
            window.SearchBox.Text = "ba";
            await WpfFrameWaiter.UntilAsync(() => vm.Results.Count == initialCount, "first instant results published", ct);
            await WpfFrameWaiter.NextAsync(ct);
            window.SearchBox.Text = "back";
            await WpfFrameWaiter.UntilAsync(() => vm.Results.Count == count && vm.Results[0].Title.StartsWith("back")
                && deferred.Request != null, "replacement instant results published", ct);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            await WpfFrameWaiter.UntilAsync(() => elapsed.ElapsedMilliseconds >= 600, "instant results resize settled", ct);
            window.UpdateLayout();
            var row = Assert.IsAssignableFrom<FrameworkElement>(window.ResultsList.ItemContainerGenerator.ContainerFromIndex(0));
            var bounds = row.TransformToAncestor(window.ContentRegion).TransformBounds(new Rect(row.RenderSize));
            Assert.True(bounds.Bottom <= window.ContentRegion.ActualHeight + 1,
                $"First result clipped while deferred search is pending: row bottom={bounds.Bottom}, region={window.ContentRegion.ActualHeight}");
            Assert.True(window.ContentRegion.ActualHeight + 1 >= window.ResultsList.DesiredSize.Height,
                "The viewport must grow to fit the published results before deferred search finishes.");
            Assert.True(vm.IsSearching);
            deferred.Request!.SetResult(Array.Empty<SpotlightSearchItem>());
            await WpfFrameWaiter.UntilAsync(() => !vm.IsSearching && !window.ContentRegion.ClipToBounds,
                "deferred search completed", ct);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(8, false)]
    [InlineData(8, true)]
    public void SearchingAfterResultsKeepsTheWholeStatusInsideTheShell(int initialCount, bool reducedMotion) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reducedMotion);
        try
        {
            var provider = new PendingProvider();
            using var fixture = new SpotlightWindowFixture(Settings(), searchProvider: provider);
            var window = fixture.Window;
            var vm = Field<SpotlightViewModel>(window, "_viewModel");
            window.ShowSpotlight();
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_entranceActive"), "search entrance", ct);
            window.SearchBox.Text = "initial";
            await WpfFrameWaiter.UntilAsync(() => provider.Request != null, "initial provider request", ct);
            provider.Request!.SetResult(Enumerable.Range(0, initialCount).Select(i => new SpotlightSearchItem(
                $"initial-{i}", SpotlightResultKind.Application, $"Initial {i}", "fixture", $"C:\\missing-{i}.exe")).ToArray());
            await WpfFrameWaiter.UntilAsync(() => vm.Results.Count == initialCount && !vm.IsSearching
                && !window.ContentRegion.ClipToBounds, "initial results sized", ct);
            window.UpdateLayout();
            double width = window.Shell.ActualWidth;
            var previousRequest = provider.Request;
            window.SearchBox.Text = "cursor";
            await WpfFrameWaiter.UntilAsync(() => provider.Request != previousRequest
                && window.SearchActivity.Visibility == Visibility.Visible, "replacement searching status", ct);
            previousRequest = provider.Request;
            window.SearchBox.Text = "cursor editor";
            await WpfFrameWaiter.UntilAsync(() => provider.Request != previousRequest,
                "typing continues during searching resize", ct);
            // Sample after the resize duration while the provider remains pending.
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            await WpfFrameWaiter.UntilAsync(() => elapsed.ElapsedMilliseconds >= 500, "search status layout settled", ct);
            window.UpdateLayout();
            var hintBounds = window.StatusHint.TransformToAncestor(window.ContentRegion)
                .TransformBounds(new Rect(window.StatusHint.RenderSize));
            Assert.True(hintBounds.Bottom + window.StatusPanel.Margin.Bottom <= window.ContentRegion.ActualHeight + 1,
                $"Searching content clipped: hint bottom={hintBounds.Bottom}, margin={window.StatusPanel.Margin.Bottom}, region={window.ContentRegion.ActualHeight}");
            var shellHintBounds = window.StatusHint.TransformToAncestor(window.Shell)
                .TransformBounds(new Rect(window.StatusHint.RenderSize));
            Assert.True(shellHintBounds.Bottom + window.StatusPanel.Margin.Bottom <= window.Shell.ActualHeight + 1,
                "The shell must also contain the full searching message.");
            window.ContentRegion.Child.Measure(new Size(window.ContentRegion.ActualWidth, double.PositiveInfinity));
            double naturalHeight = window.ContentRegion.Child.DesiredSize.Height + window.ContentRegion.BorderThickness.Top
                + window.ContentRegion.BorderThickness.Bottom;
            Assert.InRange(window.ContentRegion.ActualHeight, naturalHeight - 1, naturalHeight + 1);
            Assert.Equal(width, window.Shell.ActualWidth, precision: 3);
            Assert.True(vm.IsSearching);
            provider.Request!.SetResult([new SpotlightSearchItem(
                "replacement", SpotlightResultKind.Application, "Cursor", "fixture", "C:\\missing-cursor.exe")]);
            await WpfFrameWaiter.UntilAsync(() => vm.Results.Count == 1 && !vm.IsSearching
                && !window.ContentRegion.ClipToBounds, "replacement results sized", ct);
            Assert.True(double.IsNaN(window.ContentRegion.Height));
            Assert.Equal(Visibility.Collapsed, window.StatusPanel.Visibility);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void SearchActivitySurvivesTypingAndHonorsMotionChangesAndDismissal() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            var provider = new PendingProvider();
            using var fixture = new SpotlightWindowFixture(Settings(), searchProvider: provider);
            var window = fixture.Window;
            window.ShowSpotlight();
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_entranceActive"), "search entrance", ct);
            window.SearchBox.Text = "curs";
            await WpfFrameWaiter.UntilAsync(() => window.SearchActivityRotation.Angle > 20,
                "loading indicator rotating", ct);
            if (Environment.GetEnvironmentVariable("VNOTCH_SEARCH_MOTION_PREVIEW") is { Length: > 0 } previewPath)
            {
                await WpfFrameWaiter.UntilAsync(() => window.StatusPanel.Opacity > 0.99,
                    "loading presentation settled", ct);
                window.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)Math.Ceiling(window.Shell.ActualWidth), (int)Math.Ceiling(window.Shell.ActualHeight),
                    96, 96, PixelFormats.Pbgra32);
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen())
                    context.DrawRectangle(new VisualBrush(window.Shell), null,
                        new Rect(0, 0, window.Shell.ActualWidth, window.Shell.ActualHeight));
                bitmap.Render(drawing);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file = System.IO.File.Create(previewPath);
                encoder.Save(file);
            }
            var oldRequest = provider.Request;
            double angle = window.SearchActivityRotation.Angle;
            window.SearchBox.Text = "cursor";
            window.SearchBox.Text = "cursor editor";
            Assert.Equal(Visibility.Visible, window.SearchActivity.Visibility);
            Assert.Equal(angle, window.SearchActivityRotation.Angle);
            Assert.Equal(Loc.Get("spotlight.searching"), window.StatusTitle.Text);
            await WpfFrameWaiter.UntilAsync(() => provider.Request != oldRequest,
                "replacement query started", ct);
            AnimationConfig.SetReduceMotion(true);
            Assert.False(window.SearchActivityRotation.HasAnimatedProperties);
            Assert.Equal(0, window.SearchActivityRotation.Angle);
            Assert.Equal(1, window.StatusPanel.Opacity);
            Assert.Equal(Visibility.Visible, window.SearchActivity.Visibility);
            AnimationConfig.SetReduceMotion(false);
            await WpfFrameWaiter.UntilAsync(() => window.SearchActivityRotation.Angle > 20,
                "motion preference resumes spinner", ct);
            provider.Request!.SetResult([new SpotlightSearchItem(
                "cursor-result", SpotlightResultKind.Application, "Cursor", "fixture", "C:\\missing-cursor.exe")]);
            var vm = Field<SpotlightViewModel>(window, "_viewModel");
            await WpfFrameWaiter.UntilAsync(() => vm.Results.Count == 1 && !vm.IsSearching,
                "search results replace loading", ct);
            Assert.Equal(Visibility.Collapsed, window.SearchActivity.Visibility);
            Assert.False(window.SearchActivityRotation.HasAnimatedProperties);
            window.SearchBox.Text = "pending again";
            await WpfFrameWaiter.UntilAsync(() => window.SearchActivityRotation.Angle > 20,
                "another pending search", ct);
            window.DismissFromGlobalShortcut();
            await WpfFrameWaiter.UntilAsync(() => !window.IsSpotlightOpen && !Field<bool>(window, "_isClosing"),
                "pending search dismissed", ct);
            Assert.False(window.SearchActivityRotation.HasAnimatedProperties);
            Assert.False(window.StatusPanel.HasAnimatedProperties);
            window.ShowSpotlight();
            window.SearchBox.Text = "";
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_entranceActive"), "reopened empty search", ct);
            Assert.Equal(Visibility.Collapsed, window.SearchActivity.Visibility);
            Assert.False(window.SearchActivityRotation.HasAnimatedProperties);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void StaleResultFailureSelectsNextRowAndMessageExpires() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new SpotlightWindowFixture(Settings(), searchProvider: new ResultsProvider());
        var window = fixture.Window;
        window.ShowSpotlight();
        window.SearchBox.Text = "fixture";
        var vm = Field<SpotlightViewModel>(window, "_viewModel");
        await WpfFrameWaiter.UntilAsync(() => vm.Results.Count == 8 && !vm.IsSearching, "failure fixture results", ct);
        var stale = vm.SelectedResult!;
        Invoke(window, "ShowLaunchFailure", stale);
        Assert.DoesNotContain(stale, vm.Results);
        Assert.Same(vm.Results[0], vm.SelectedResult);
        Assert.Equal(Visibility.Visible, window.FailureBar.Visibility);
        Assert.Contains(stale.DisplayTitle, window.FailureText.Text);
        var timer = Field<UiScheduledTimer>(window, "_failureTimer");
        Invoke(window, "ShowSpotlightMessage", "replacement failure");
        Assert.False(timer.IsEnabled);
        Assert.Equal("replacement failure", window.FailureText.Text);
        var activeTimer = Field<UiScheduledTimer>(window, "_failureTimer");
        activeTimer.Interval = TimeSpan.FromMilliseconds(10);
        await WpfFrameWaiter.UntilAsync(() => window.FailureBar.Visibility == Visibility.Collapsed, "failure message expired", ct);
        Assert.Null(Field<UiScheduledTimer?>(window, "_failureTimer"));
        Invoke(window, "ShowSpotlightMessage", "cancelled failure");
        Invoke(window, "ClearLaunchFailure");
        Assert.Equal(Visibility.Collapsed, window.FailureBar.Visibility);
        Assert.Null(Field<UiScheduledTimer?>(window, "_failureTimer"));
    });

    private static NotchSettings Settings() => new() { EnableLocalOnlyMode = true, NotchStyle = "default" };
    private static T Field<T>(SpotlightWindow window, string name) => (T)typeof(SpotlightWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Invoke(SpotlightWindow window, string name, params object?[] args) => typeof(SpotlightWindow).GetMethod(name, Private)!.Invoke(window, args);

    [Fact]
    public void GlowingCaretAdvancesStrictlyPerCharacterWithoutDuplicateEndSlot() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new SpotlightWindowFixture(Settings());
        var window = fixture.Window;
        window.ShowSpotlight();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        window.SearchBox.Text = "aaaa";
        window.SearchBox.Focus();
        window.SearchBox.UpdateLayout();

        var positions = new double[5];
        for (int i = 0; i <= 4; i++)
        {
            window.SearchBox.CaretIndex = i;
            Invoke(window, "UpdateGlowingCaret");
            positions[i] = window.GlowingCaret.Margin.Left;
        }

        for (int i = 1; i <= 4; i++)
        {
            Assert.True(positions[i] > positions[i - 1], $"Expected caret X at index {i} ({positions[i]}) > index {i - 1} ({positions[i - 1]})");
        }
    });

    private sealed class ResultsProvider : ISpotlightProvider
    {
        public bool IsAvailable => true;
        public Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SpotlightSearchItem>>(Enumerable.Range(0, 8)
                .Select(i => new SpotlightSearchItem($"{query}-{i}", SpotlightResultKind.File, $"{query} result {i}", "fixture", $"C:\\missing-{query}-{i}.txt") { Score = 100 - i }).ToArray());
    }
    private sealed class InstantResultsProvider(int initialCount, int count) : ISpotlightProvider
    {
        public bool IsAvailable => true;
        public bool IsInstant => true;
        public Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SpotlightSearchItem>>(Enumerable.Range(0, query == "ba" ? initialCount : count)
                .Select(i => new SpotlightSearchItem($"{query}-{i}", SpotlightResultKind.Application,
                    $"{query} result {i}", "fixture", $"C:\\missing-{i}.exe")
                { Score = 100 - i }).ToArray());
    }

    private sealed class PendingProvider : ISpotlightProvider
    {
        public bool IsAvailable => true;
        public TaskCompletionSource<IReadOnlyList<SpotlightSearchItem>>? Request { get; private set; }
        public Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
        {
            Request = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return Request.Task.WaitAsync(cancellationToken);
        }
    }
    private sealed class TestPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
