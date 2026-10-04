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
            await WpfFrameWaiter.UntilAsync(() => window.ResultsList.Opacity == 1, "search rows restored", ct);
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
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Invoke(window, "Window_PreviewKeyDown", window, escape);
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
            Assert.Equal(!reducedMotion, window.StatusGlyph.HasAnimatedProperties);
            provider.Request!.SetResult(Array.Empty<SpotlightSearchItem>());
            await WpfFrameWaiter.UntilAsync(() => !vm.IsSearching, "empty search finished", ct);
            Assert.Null(Field<string?>(window, "_pendingLaunchQuery"));
            Assert.Equal(Loc.Get("spotlight.unavailable"), window.StatusTitle.Text);
            Assert.Equal(Visibility.Visible, window.StatusWarningIcon.Visibility);
            Assert.False(window.StatusGlyph.HasAnimatedProperties);
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
        var timer = Field<DispatcherTimer>(window, "_failureTimer");
        Invoke(window, "ShowSpotlightMessage", "replacement failure");
        Assert.False(timer.IsEnabled);
        Assert.Equal("replacement failure", window.FailureText.Text);
        await WpfFrameWaiter.UntilAsync(() => window.FailureBar.Visibility == Visibility.Collapsed, "failure message expired", ct);
        Assert.Null(Field<DispatcherTimer?>(window, "_failureTimer"));
        Invoke(window, "ShowSpotlightMessage", "cancelled failure");
        Invoke(window, "ClearLaunchFailure");
        Assert.Equal(Visibility.Collapsed, window.FailureBar.Visibility);
        Assert.Null(Field<DispatcherTimer?>(window, "_failureTimer"));
    });

    private static NotchSettings Settings() => new() { EnableLocalOnlyMode = true, NotchStyle = "default" };
    private static T Field<T>(SpotlightWindow window, string name) => (T)typeof(SpotlightWindow).GetField(name, Private)!.GetValue(window)!;
    private static object? Invoke(SpotlightWindow window, string name, params object?[] args) => typeof(SpotlightWindow).GetMethod(name, Private)!.Invoke(window, args);
    private sealed class ResultsProvider : ISpotlightProvider
    {
        public bool IsAvailable => true;
        public Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SpotlightSearchItem>>(Enumerable.Range(0, 8)
                .Select(i => new SpotlightSearchItem($"{query}-{i}", SpotlightResultKind.File, $"{query} result {i}", "fixture", $"C:\\missing-{query}-{i}.txt") { Score = 100 - i }).ToArray());
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
