using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using VNotch.Models;
using VNotch.Presenters;
using VNotch.Services;
using VNotch.Services.Spotlight;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class UpdatePresenterAndUsageStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _usagePath;
    private readonly DateTime _now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    public UpdatePresenterAndUsageStoreTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("vnotch-update-usage-").FullName;
        _usagePath = Path.Combine(_tempDir, "spotlight-usage.json");
    }

    public void Dispose()
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }

    [Fact]
    public void CheckForUpdatesAndDownload_ProceedWhenCompareVersionsFindsNewerDespiteIsNewerVersionFalse() =>
        SharedStaTestRunner.RunAsync(async () =>
        {
            var updateInfo = new UpdateInfo
            {
                Version = "99.0.0",
                IsNewerVersion = false,
                DownloadUrl = "https://github.com/w0otdev/V-Notch/releases/download/v99.0.0/V-Notch-Setup.exe"
            };
            var service = new FakeUpdateService(updateInfo);
            var refs = new SettingsUpdateViewRefs(new Window(), new TextBlock(), new Button(), new Button());
            using var presenter = new SettingsUpdatePresenter(service, refs, () => true);

            await presenter.CheckForUpdatesAsync();

            Assert.Equal(Loc.Get("settings.updateAvailable", "99.0.0"), refs.Status.Text);
            Assert.Equal(Visibility.Visible, refs.Download.Visibility);

            await presenter.DownloadUpdateAsync();

            Assert.Equal(1, service.DownloadCallCount);
            Assert.Same(updateInfo, service.LastDownloadedUpdate);
        });

    [Fact]
    public async Task SaveLoop_WritesEmptySnapshotOverExistingFileWhenEntriesBecomeEmpty()
    {
        var item = new SpotlightSearchItem(
            "app:one",
            SpotlightResultKind.Application,
            "One App",
            "Application",
            "shell:AppsFolder\\One!App");

        await using var store = new SpotlightUsageStore(_usagePath, () => _now);
        store.RecordLaunch(item);
        await store.WaitForPendingSavesAsync();

        Assert.True(File.Exists(_usagePath));
        var initialOnDisk = JsonSerializer.Deserialize<Dictionary<string, SpotlightUsageStore.UsageEntry>>(
            await File.ReadAllTextAsync(_usagePath));
        Assert.NotNull(initialOnDisk);
        Assert.Single(initialOnDisk);

        // Simulate entries becoming empty while the stale file remains on disk
        // (e.g. transient lock during ClearHistory or in-memory eviction) with _changeVersion incremented.
        var gateField = typeof(SpotlightUsageStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var entriesField = typeof(SpotlightUsageStore).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var versionField = typeof(SpotlightUsageStore).GetField("_changeVersion", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var workerRunningField = typeof(SpotlightUsageStore).GetField("_saveWorkerRunning", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var workerField = typeof(SpotlightUsageStore).GetField("_saveWorker", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var saveLoopMethod = typeof(SpotlightUsageStore).GetMethod("SaveLoop", BindingFlags.Instance | BindingFlags.NonPublic)!;

        object gate = gateField.GetValue(store)!;
        lock (gate)
        {
            var entries = (Dictionary<string, SpotlightUsageStore.UsageEntry>)entriesField.GetValue(store)!;
            entries.Clear();
            long nextVersion = (long)versionField.GetValue(store)! + 1;
            versionField.SetValue(store, nextVersion);
            workerRunningField.SetValue(store, true);
            workerField.SetValue(store, Task.Run(() => saveLoopMethod.Invoke(store, null)));
        }

        await store.WaitForPendingSavesAsync();

        Assert.True(File.Exists(_usagePath));
        var afterEmptyOnDisk = JsonSerializer.Deserialize<Dictionary<string, SpotlightUsageStore.UsageEntry>>(
            await File.ReadAllTextAsync(_usagePath));
        Assert.NotNull(afterEmptyOnDisk);
        Assert.Empty(afterEmptyOnDisk);

        await using var reloaded = new SpotlightUsageStore(_usagePath, () => _now);
        Assert.Equal(0, reloaded.GetBoost("app:one"));
        Assert.Empty(reloaded.GetRecentItems(10));
    }

    private sealed class FakeUpdateService(UpdateInfo? nextUpdate) : IUpdateService
    {
        public int DownloadCallCount { get; private set; }
        public UpdateInfo? LastDownloadedUpdate { get; private set; }
        public string CurrentVersion => "1.0.0";
        public UpdateInfo? LatestUpdateInfo => nextUpdate;
        public event EventHandler<UpdateInfo?>? UpdateCheckCompleted { add { } remove { } }

        public Task<UpdateInfo?> CheckForUpdatesAsync(bool includePrereleases = false, bool forceRefresh = false) =>
            Task.FromResult(nextUpdate);

        public Task<IReadOnlyList<UpdateInfo>> GetAllReleasesAsync() =>
            Task.FromResult<IReadOnlyList<UpdateInfo>>([]);

        public Task<bool> DownloadAndInstallUpdateAsync(
            UpdateInfo updateInfo,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadCallCount++;
            LastDownloadedUpdate = updateInfo;
            return Task.FromResult(true);
        }
    }
}
