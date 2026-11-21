using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Windows;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class DiagnosticAndChangelogWindowTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    [Theory]
    [InlineData(PerformanceHealthLevel.Nominal, "NOMINAL")]
    [InlineData(PerformanceHealthLevel.Warning, "WARNING")]
    [InlineData(PerformanceHealthLevel.Critical, "CRITICAL")]
    public void DiagnosticSnapshotFormatsMetricsAndClampsBars(PerformanceHealthLevel health, string badge) => SharedStaTestRunner.Run(() =>
    {
        LiquidGlassSpotlightTests.CreateApplicationResources();
        var window = new DebugWindow();
        try
        {
            var snapshot = new PerformanceDebugSnapshot
            {
                HealthLevel = health,
                HealthStatusSummary = "Test health",
                Fps = 59.6,
                RefreshRateHz = 120,
                FrameTimeMs = 8.3,
                DispatcherLatencyMs = 1.2,
                ProcessCpuPercent = 150,
                ProcessThreadCount = 7,
                ProcessHandleCount = 50,
                ProcessWorkingSetBytes = 5 * 1024 * 1024,
                ProcessPrivateBytes = 6 * 1024 * 1024,
                ManagedHeapBytes = 2 * 1024 * 1024,
                AllocBytesPerSec = 2 * 1024 * 1024,
                GcGen0Count = 1,
                GcGen1Count = 2,
                GcGen2Count = 3,
                ProcessGpuPercent = -5,
                GlobalCpuPercent = 35,
                GlobalRamTotalBytes = 8UL * 1024 * 1024 * 1024,
                GlobalRamUsedBytes = 4UL * 1024 * 1024 * 1024,
                GlobalRamAvailBytes = 4UL * 1024 * 1024 * 1024,
                GlobalRamPercent = 50,
                GlobalGpuPercent = 200,
                GpuName = "Test GPU",
                DedicatedVramBytes = 2UL * 1024 * 1024 * 1024,
                NetDownBytesPerSec = 2048,
                NetUpBytesPerSec = -10
            };
            window.UpdateSnapshot(snapshot);
            window.UpdateSnapshot(snapshot);
            Assert.Equal(badge, window.HealthBadgeText.Text);
            Assert.Equal("Test health", window.HealthSummaryText.Text);
            Assert.Equal("UI 60 FPS", window.FpsSummaryText.Text);
            Assert.Equal("(120 Hz)", window.HzSummaryText.Text);
            Assert.Equal(1, window.VNotchCpuScale.ScaleX);
            Assert.Equal(0, window.VNotchGpuScale.ScaleX);
            Assert.Equal(.5, window.GlobalRamScale.ScaleX);
            Assert.Equal(1, window.GlobalGpuScale.ScaleX);
            Assert.Contains("5", window.VNotchRamText.Text);
            Assert.Contains("2", window.NetDownText.Text);
            Assert.Equal("0 B/s", window.NetUpText.Text);
            window.UpdateSnapshot(new PerformanceDebugSnapshot());
            Assert.Equal("—", window.GlobalRamText.Text);
            Assert.Equal("DirectX Display Adapter", window.GpuNameText.Text);
            window.UpdateFps(144);
            window.UpdateFps(144);
            window.UpdateRefreshRate(0);
            window.UpdateRefreshRate(0);
            Assert.Equal("UI 144 FPS", window.FpsSummaryText.Text);
            Assert.Equal("(-- Hz)", window.HzSummaryText.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void DiagnosticControlsPublishViewLockDragAndPositionCallbacks() => SharedStaTestRunner.Run(() =>
    {
        LiquidGlassSpotlightTests.CreateApplicationResources();
        var locks = new List<bool>();
        var drags = new List<bool>();
        var views = new List<string>();
        int resets = 0, closes = 0;
        var window = new DebugWindow(new DebugWindowOptions
        {
            InitialX = 25,
            InitialY = 30,
            OnLockViewChanged = locks.Add,
            OnDragNotchChanged = drags.Add,
            OnViewStateChanged = views.Add,
            OnResetPosition = () => resets++,
            OnClose = () => closes++
        });
        try
        {
            Invoke(window, "DebugWindow_Loaded", window, new RoutedEventArgs());
            Assert.Equal(25, window.Left);
            Assert.Equal(30, window.Top);
            foreach (var item in window.ViewStateComboBox.Items.OfType<ComboBoxItem>())
                window.ViewStateComboBox.SelectedItem = item;
            Assert.Contains("Current", views);
            Assert.True(window.LockStateCheckBox.IsChecked);
            window.LockStateCheckBox.IsChecked = false;
            window.DragNotchCheckBox.IsChecked = true;
            window.DragNotchCheckBox.IsChecked = false;
            Assert.Contains(true, locks);
            Assert.Contains(false, locks);
            Assert.Equal(new[] { true, false }, drags);
            foreach (var (method, element) in new[]
            {
                ("ToggleProcessUsage_Click", window.ProcessUsageContent),
                ("ToggleGlobalUsage_Click", window.GlobalUsageContent),
                ("ToggleBottlenecks_Click", window.BottlenecksContent)
            })
            {
                var initial = element.Visibility;
                Invoke(window, method, window, MouseArgs());
                Assert.NotEqual(initial, element.Visibility);
                Invoke(window, method, window, MouseArgs());
                Assert.Equal(initial, element.Visibility);
            }
            window.ServiceLogsPanel.Visibility = Visibility.Collapsed;
            Invoke(window, "ToggleServiceLogsBtn_Click", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, window.ServiceLogsPanel.Visibility);
            Invoke(window, "CloseServiceLogsBtn_Click", window, new RoutedEventArgs());
            Assert.Equal(Visibility.Collapsed, window.ServiceLogsPanel.Visibility);
            Invoke(window, "ResetPositionBtn_Click", window, new RoutedEventArgs());
            Invoke(window, "CloseButton_Click", window, new RoutedEventArgs());
            Assert.Equal(1, resets);
            Assert.Equal(1, closes);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("Media (Playback & Track)", "MEDIA-TRACK")]
    [InlineData("Audio Mixer", "AUDIO-VOLUME")]
    [InlineData("Battery & Power", "BATTERY")]
    [InlineData("Bluetooth", "BLUETOOTH")]
    [InlineData("Spotify", "SPOTIFY")]
    [InlineData("Weather", "WEATHER")]
    [InlineData("Spotlight Search", "SPOTLIGHT")]
    [InlineData("Subtitles & Lyrics", "LYRICS")]
    [InlineData("Liquid Glass", "GLASS")]
    [InlineData("Memory & GC", "GC")]
    public void DiagnosticLogFilteringAndRetentionKeepOnlyMatchingNewestEntries(string filter, string category) => SharedStaTestRunner.Run(() =>
    {
        LiquidGlassSpotlightTests.CreateApplicationResources();
        var window = new DebugWindow();
        try
        {
            var logs = Enumerable.Range(0, 520).Select(i => new DiagnosticLogEntry(DateTime.UtcNow.AddMilliseconds(i),
                (PerformanceHealthLevel)(i % 3), i % 2 == 0 ? category : "UNRELATED", "Event " + i)).ToList();
            Invoke(window, "PopulateServiceLogs", logs, filter, true, 0);
            var shown = Field<ObservableCollection<DiagnosticLogViewModel>>(window, "_serviceLogs");
            Assert.Equal(260, shown.Count);
            Assert.All(shown, log => Assert.Equal(category, log.Category));
            logs.Add(new(DateTime.UtcNow.AddSeconds(1), PerformanceHealthLevel.Nominal, category, "Latest"));
            Invoke(window, "PopulateServiceLogs", logs, filter, false, 520);
            Assert.Equal("Latest", shown[^1].Message);
            Invoke(window, "PopulateServiceLogs", logs, "All Categories", true, 0);
            Invoke(window, "TrimAndScrollServiceLogs");
            Assert.Equal(500, shown.Count);
            Assert.Equal("Latest", shown[^1].Message);
            Assert.Equal("500 service events logged", window.ServiceLogCountText.Text);
            Assert.Contains(category, shown.First(log => log.Category == category).FullText);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("2.0.0")]
    [InlineData("2.0")]
    [InlineData("1.0.0")]
    [InlineData("0.5.0")]
    public void ChangelogLoadsVersionsBadgesAndSafeMarkdown(string installed) => SharedStaTestRunner.RunAsync(async () =>
    {
        LiquidGlassSpotlightTests.CreateApplicationResources();
        var fixture = new ChangelogFixture(installed,
        [
            new() { Version = "1.0.0", PublishedAt = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc), ReleaseNotes = "" },
            new() { Version = "2.0.0", ReleaseTag = "v2.0.0", PublishedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), ReleaseNotes = "# Main\n## Group\n### Detail\n\n- **Fixed** with *care* and `code`\n* More\nPlain [safe](https://example.com) and [blocked](file:///C:/private.txt) end" },
            new() { Version = "bad-version", PublishedAt = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), ReleaseNotes = "Old" }
        ]);
        var view = new ChangelogView();
        await view.LoadAsync(fixture, () => true);
        var rows = view.VersionList.Items.Cast<ListBoxItem>().ToList();
        Assert.Equal(installed == "0.5.0" ? 4 : 3, rows.Count);
        Assert.Equal("2.0.0", rows[0].Tag);
        Assert.Equal("bad-version", rows[^1].Tag);
        var latestRow = Assert.Single(rows, b => Descendants<TextBlock>(b).Any(t => t.Text == Loc.Get("changelog.latest")));
        Assert.Equal("2.0.0", latestRow.Tag);
        Assert.Contains(Descendants<TextBlock>(latestRow), t => t.Text == "v2.0.0");
        var installedRow = Assert.Single(rows, b => Descendants<TextBlock>(b).Any(t => t.Text == Loc.Get("changelog.current")));
        Assert.Equal(installed == "2.0" ? "2.0.0" : installed, installedRow.Tag);
        if (installed == "0.5.0") Assert.Equal(2, Descendants<TextBlock>(installedRow).Count());
        var blocks = Descendants<TextBlock>(view.ChangelogContent).ToList();
        Assert.Contains(blocks, text => text.Inlines.OfType<Bold>().Any());
        Assert.Contains(blocks, text => text.Inlines.OfType<Italic>().Any());
        Assert.Single(blocks.SelectMany(text => text.Inlines.OfType<Hyperlink>()));
        Assert.Contains(blocks, text => new TextRange(text.ContentStart, text.ContentEnd).Text.Contains("blocked"));
        foreach (var row in rows)
        {
            view.VersionList.SelectedItem = row;
            Assert.Equal(Visibility.Visible, view.ChangelogContent.Visibility);
            Assert.Contains(Descendants<TextBlock>(view.ChangelogContent), text => text.Text == Loc.Get("changelog.version", row.Tag));
        }
        view.VersionList.SelectedItem = rows.Single(row => Equals(row.Tag, "1.0.0"));
        Assert.Contains(Descendants<TextBlock>(view.ChangelogContent), text => text.Text == Loc.Get("changelog.noNotes"));
    });

    [Fact]
    public void ChangelogColorsFollowTheReleaseAndKeepBothStatusesDistinct() => SharedStaTestRunner.RunAsync(async () =>
    {
        LiquidGlassSpotlightTests.CreateApplicationResources();
        var view = new ChangelogView();
        var window = new BackgroundWindow { Content = view, Width = 600, Height = 480 };
        var releases = new UpdateInfo[]
        {
            new() { Version = "2.4.2" },
            new() { Version = "2.3.0-beta2" },
            new() { Version = "2.2.0", IsPrerelease = true }
        };
        static TextBlock Title(ListBoxItem row) => (TextBlock)((StackPanel)row.Content).Children[0];
        try
        {
            window.Show();
            await view.LoadAsync(new ChangelogFixture("2.4.2", releases), () => true);
            window.UpdateLayout();
            var stable = (ListBoxItem)view.VersionList.SelectedItem;
            var beta = (ListBoxItem)view.VersionList.Items[1];
            var flagged = (ListBoxItem)view.VersionList.Items[2];
            Assert.Same(view.FindResource("StableVersionBrush"), ((TextBlock)view.ReleaseHeader.Children[0]).Foreground);
            Assert.Same(view.FindResource("PrereleaseVersionBrush"), Title(beta).Foreground);
            Assert.Same(Title(beta).Foreground, Title(flagged).Foreground);
            var selectedStable = ((SolidColorBrush)Title(stable).Foreground).Color;

            foreach (var name in new[] { "changelog.latest", "changelog.current" })
            {
                foreach (var host in new FrameworkElement[] { stable, view.ReleaseHeader })
                {
                    var text = Assert.Single(Descendants<TextBlock>(host), t => t.Text == Loc.Get(name));
                    var title = host == stable ? Title(stable) : (TextBlock)view.ReleaseHeader.Children[0];
                    Assert.Same(title.Foreground, text.Foreground);
                    Assert.Same(text.Foreground, ((Border)text.Parent).BorderBrush);
                }
            }
            view.VersionList.SelectedItem = beta;
            window.UpdateLayout();
            Assert.Same(view.FindResource("StableVersionBrush"), Title(stable).Foreground);
            Assert.Same(Title(flagged).Foreground, ((TextBlock)view.ReleaseHeader.Children[0]).Foreground);
            var selectedBeta = ((SolidColorBrush)Title(beta).Foreground).Color;
            Assert.NotEqual(selectedStable, selectedBeta);
            Assert.All(AccentTheme.Swatches, swatch =>
            {
                Assert.True(ContrastSearchTests.GetContrastRatio(selectedStable, swatch.Color) >= 4.5);
                Assert.True(ContrastSearchTests.GetContrastRatio(selectedBeta, swatch.Color) >= 4.5);
            });

            await view.LoadAsync(new ChangelogFixture("2.1.0-beta-1", releases), () => true);
            window.UpdateLayout();
            var installedBeta = (ListBoxItem)view.VersionList.Items[^1];
            Assert.Equal("2.1.0-beta-1", installedBeta.Tag);
            Assert.Same(view.FindResource("PrereleaseVersionBrush"), Title(installedBeta).Foreground);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ScrollingLoadsOlderChangelogsOnceAndResolvesTheInstalledRow() => SharedStaTestRunner.RunAsync(async ct =>
    {
        LiquidGlassSpotlightTests.CreateApplicationResources();
        var requests = new List<int>();
        var pending = new TaskCompletionSource<ReleaseHistoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recent = Enumerable.Range(1, 12).Select(i => new UpdateInfo { Version = $"2.3.{i}" }).ToList();
        var fixture = new ChangelogFixture("2.4.2", [])
        {
            LoadPage = page =>
            {
                requests.Add(page);
                return page == 1 ? Task.FromResult(new ReleaseHistoryPage(recent, true)) : pending.Task;
            }
        };
        var view = new ChangelogView();
        var window = new Window { Content = view, Width = 500, Height = 220, ShowInTaskbar = false };
        try
        {
            window.Show();
            await view.LoadAsync(fixture, () => true, ct);
            window.UpdateLayout();
            Assert.Equal(new[] { 1 }, requests);
            var firstRow = (ListBoxItem)view.VersionList.SelectedItem;
            Assert.Equal("2.4.2", firstRow.Tag);
            var existingRow = view.VersionList.Items[1];
            Assert.True(view.VersionScrollViewer.ScrollableHeight > 0);
            view.VersionScrollViewer.ScrollToBottom();
            await view.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.Equal(new[] { 1, 2 }, requests);
            await view.LoadOlderVersionsAsync();
            Assert.Equal(new[] { 1, 2 }, requests);
            pending.SetResult(new([recent[0], new() { Version = "2.4.2", ReleaseNotes = "Published notes" }], false));
            await WpfFrameWaiter.UntilAsync(() => view.OlderVersionsButton.Visibility == Visibility.Collapsed,
                "the final history page to replace the installed placeholder", ct);
            var rows = view.VersionList.Items.Cast<ListBoxItem>().ToList();
            Assert.Equal(13, rows.Count);
            Assert.Same(existingRow, rows[1]);
            var published = Assert.Single(rows, r => Equals(r.Tag, "2.4.2"));
            Assert.Same(published, view.VersionList.SelectedItem);
            Assert.Contains(Descendants<TextBlock>(published), t => t.Text == Loc.Get("changelog.current"));
            Assert.Contains(Descendants<TextBlock>(published), t => t.Text == Loc.Get("changelog.latest"));
            var note = Assert.Single(Descendants<TextBlock>(view.ReleaseNotes));
            Assert.Equal("Published notes", new TextRange(note.ContentStart, note.ContentEnd).Text.Trim());
            Assert.Equal(Visibility.Collapsed, view.OlderVersionsButton.Visibility);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void SelectionMorphContinuesOnOlderRowsWhileAnotherPageArrives() => SharedStaTestRunner.RunAsync(async ct =>
    {
        LiquidGlassSpotlightTests.CreateApplicationResources();
        static UpdateInfo[] Releases(int major) => Enumerable.Range(1, 12)
            .Select(i => new UpdateInfo { Version = $"{major}.0.{i}" }).ToArray();
        var thirdPage = new TaskCompletionSource<ReleaseHistoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new ChangelogFixture("3.0.12", [])
        {
            LoadPage = page => page switch
            {
                1 => Task.FromResult(new ReleaseHistoryPage(Releases(3), true)),
                2 => Task.FromResult(new ReleaseHistoryPage(Releases(2), true)),
                3 => thirdPage.Task,
                _ => throw new InvalidOperationException()
            }
        };
        var view = new ChangelogView();
        var window = new Window { Content = view, Width = 500, Height = 250, ShowInTaskbar = false };
        var previousMotion = AnimationConfig.ReduceMotion;
        var previousSystem = AnimationConfig.ClientAreaAnimationOverride;
        try
        {
            AnimationConfig.SetReduceMotion(false);
            AnimationConfig.ClientAreaAnimationOverride = true;
            window.Show();
            await view.LoadAsync(fixture, () => true, ct);
            window.UpdateLayout();
            view.VersionScrollViewer.ScrollToBottom();
            await WpfFrameWaiter.UntilAsync(() => view.VersionList.Items.Count == 24, "older rows to load", ct);
            view.VersionScrollViewer.ScrollToBottom();
            await WpfFrameWaiter.UntilAsync(() => !view.OlderVersionsButton.IsEnabled, "the next page request", ct);

            var older = (ListBoxItem)view.VersionList.Items[22];
            double target = older.TranslatePoint(new Point(0, 1), view.VersionList).Y;
            double startCenter = view.MarkOffset.Y + view.SelectionMark.Height / 2;
            Assert.True(target - view.MarkOffset.Y > view.VersionScrollViewer.ViewportHeight);
            view.VersionList.SelectedItem = older;
            Assert.Equal(startCenter, view.MarkOffset.Y + view.SelectionMark.Height / 2, precision: 3);
            thirdPage.SetResult(new(Releases(1), false));
            await WpfFrameWaiter.UntilAsync(() => view.VersionList.Items.Count == 36 && view.MarkScale.ScaleY > 1.01 &&
                view.MarkOffset.Y > startCenter && view.MarkOffset.Y < target,
                "the focus indicator to travel between rows while the next page loads", ct);
            await WpfFrameWaiter.UntilAsync(() => view.MarkScale.ScaleY == 1 && Math.Abs(view.MarkOffset.Y - target) < .01,
                "the focus indicator to settle on the older row", ct);
            Assert.Same(older, view.VersionList.SelectedItem);
        }
        finally
        {
            window.Close();
            AnimationConfig.ClientAreaAnimationOverride = previousSystem;
            AnimationConfig.SetReduceMotion(previousMotion);
        }
    });

    [Fact]
    public void UnavailableChangelogExplainsWhetherLocalOnlyModeIsActive() => SharedStaTestRunner.RunAsync(async () =>
    {
        LiquidGlassSpotlightTests.CreateApplicationResources();
        bool online = true;
        var fixture = new ChangelogFixture("2.4.1", []);
        var view = new ChangelogView();
        await view.LoadAsync(fixture, () => online);
        Assert.Equal(Loc.Get("update.releaseUnavailable"), view.ErrorText.Text);
        online = false;
        await view.LoadAsync(fixture, () => online);
        Assert.Equal(Loc.Get("update.localOnly"), view.ErrorText.Text);
        Assert.Equal(Visibility.Visible, view.ErrorPanel.Visibility);
        Assert.Equal(Visibility.Collapsed, view.NotesSkeleton.Visibility);
    });

    [Fact]
    public void ChangelogFailureShowsErrorAndClearsLoading() => SharedStaTestRunner.RunAsync(async () =>
    {
        LiquidGlassSpotlightTests.CreateApplicationResources();
        var fixture = new ChangelogFixture("1.0.0", [], fail: true);
        var view = new ChangelogView();
        await view.LoadAsync(fixture, () => true);
        Assert.Equal(Visibility.Visible, view.ErrorPanel.Visibility);
        Assert.Equal(Visibility.Collapsed, view.NotesSkeleton.Visibility);
        Assert.Equal(Visibility.Collapsed, view.ChangelogContent.Visibility);
        Assert.Contains("test failure", view.ErrorText.Text);
    });

    private static MouseButtonEventArgs MouseArgs() => new(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is T match) yield return match;
            if (child is DependencyObject dependency)
                foreach (var descendant in Descendants<T>(dependency)) yield return descendant;
        }
    }
    private sealed class ChangelogFixture : IUpdateService
    {
        private readonly IReadOnlyList<UpdateInfo> _releases;
        private readonly bool _fail;
        internal Func<int, Task<ReleaseHistoryPage>>? LoadPage { get; init; }
        public string CurrentVersion { get; }
        public event EventHandler<UpdateInfo?>? UpdateCheckCompleted { add { /* no-op */ } remove { /* no-op */ } }
        public UpdateInfo? LatestUpdateInfo => null;
        internal ChangelogFixture(string installed, IReadOnlyList<UpdateInfo> releases, bool fail = false)
        {
            CurrentVersion = installed; _releases = releases; _fail = fail;
        }
        public Task<IReadOnlyList<UpdateInfo>> GetAllReleasesAsync() => _fail
            ? Task.FromException<IReadOnlyList<UpdateInfo>>(new InvalidOperationException("test failure")) : Task.FromResult(_releases);
        public async Task<ReleaseHistoryPage> GetChangelogPageAsync(int page, CancellationToken token) =>
            LoadPage != null ? await LoadPage(page).WaitAsync(token) : new(await GetAllReleasesAsync(), false);
        public Task<UpdateInfo?> CheckForUpdatesAsync(bool includePrereleases = false, bool forceRefresh = false) => Task.FromResult<UpdateInfo?>(null);
        public Task<bool> DownloadAndInstallUpdateAsync(UpdateInfo updateInfo, IProgress<double>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
