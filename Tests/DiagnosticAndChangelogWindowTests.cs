using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
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
            Assert.Equal("60 FPS", window.FpsSummaryText.Text);
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
            Assert.Equal("144 FPS", window.FpsSummaryText.Text);
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
    [InlineData("1.0.0")]
    [InlineData("0.5.0")]
    public void ChangelogLoadsVersionsBadgesAndSafeMarkdown(string installed) => SharedStaTestRunner.RunAsync(async ct =>
    {
        LiquidGlassSpotlightTests.CreateApplicationResources();
        using var fixture = new ChangelogFixture(installed,
        [
            new() { Version = "2.0.0", PublishedAt = new DateTime(2026, 1, 2), ReleaseNotes = "# Main\n## Group\n### Detail\n\n- **Fixed** with *care* and `code`\n* More\nPlain [safe](https://example.com) and [blocked](file:///C:/private.txt) end" },
            new() { Version = "1.0.0", PublishedAt = new DateTime(2025, 1, 2), ReleaseNotes = "" },
            new() { Version = "bad-version", PublishedAt = new DateTime(2024, 1, 2), ReleaseNotes = "Old" }
        ]);
        var window = fixture.Window;
        await (Task)Invoke(window, "LoadChangelog")!;
        var buttons = window.VersionListPanel.Children.OfType<Button>().ToList();
        Assert.Equal(installed == "0.5.0" ? 4 : 3, buttons.Count);
        Assert.Equal("2.0.0", buttons[0].Tag);
        Assert.Equal("bad-version", buttons[^1].Tag);
        var blocks = Descendants<TextBlock>(window.ChangelogContent).ToList();
        Assert.Contains(blocks, text => text.Inlines.OfType<Bold>().Any());
        Assert.Contains(blocks, text => text.Inlines.OfType<Italic>().Any());
        Assert.Single(blocks.SelectMany(text => text.Inlines.OfType<Hyperlink>()));
        Assert.Contains(blocks, text => new TextRange(text.ContentStart, text.ContentEnd).Text.Contains("blocked"));
        foreach (var button in buttons)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Visible, window.ChangelogContent.Visibility);
            Assert.Contains(Descendants<TextBlock>(window.ChangelogContent), text => text.Text == Loc.Get("changelog.version", button.Tag));
        }
        buttons.Single(button => Equals(button.Tag, "1.0.0")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Contains(Descendants<TextBlock>(window.ChangelogContent), text => text.Text == Loc.Get("changelog.noNotes"));
        Invoke(window, "PlayEntranceAnimation");
        await WpfFrameWaiter.UntilAsync(() => window.MainShell.Opacity == 1, "changelog entrance", ct);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        Invoke(window, "CloseButton_Click", window, new RoutedEventArgs());
        await WpfFrameWaiter.UntilAsync(() => closed, "changelog close", ct);
    });

    [Fact]
    public void ChangelogFailureShowsErrorAndClearsLoading() => SharedStaTestRunner.RunAsync(async ct =>
    {
        LiquidGlassSpotlightTests.CreateApplicationResources();
        using var fixture = new ChangelogFixture("1.0.0", [], fail: true);
        await (Task)Invoke(fixture.Window, "LoadChangelog")!;
        Assert.Equal(Visibility.Visible, fixture.Window.ErrorPanel.Visibility);
        Assert.Equal(Visibility.Collapsed, fixture.Window.LoadingText.Visibility);
        Assert.Equal(Visibility.Collapsed, fixture.Window.ChangelogContent.Visibility);
        Assert.Contains("test failure", fixture.Window.ErrorText.Text);
        await WpfFrameWaiter.NextAsync(ct);
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
    private sealed class ChangelogFixture : IUpdateService, IDisposable
    {
        private readonly IReadOnlyList<UpdateInfo> _releases;
        private readonly bool _fail;
        public string CurrentVersion { get; }
        internal ChangelogWindow Window { get; }
        internal ChangelogFixture(string installed, IReadOnlyList<UpdateInfo> releases, bool fail = false)
        {
            CurrentVersion = installed; _releases = releases; _fail = fail;
            Window = new ChangelogWindow(this);
        }
        public Task<IReadOnlyList<UpdateInfo>> GetAllReleasesAsync() => _fail
            ? Task.FromException<IReadOnlyList<UpdateInfo>>(new InvalidOperationException("test failure")) : Task.FromResult(_releases);
        public Task<UpdateInfo?> CheckForUpdatesAsync() => Task.FromResult<UpdateInfo?>(null);
        public Task<bool> DownloadAndInstallUpdateAsync(UpdateInfo updateInfo, IProgress<double>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() => Window.Close();
    }
}
