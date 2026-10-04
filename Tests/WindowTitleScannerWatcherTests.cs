using System.IO;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class WindowTitleScannerWatcherTests
{
    [Fact]
    public async Task WatcherCreatesTheMissingFirstLaunchDirectoryAndObservesSettings()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vnotch-watcher-" + Guid.NewGuid().ToString("N"));
        using var changed = new AutoResetEvent(false);
        try
        {
            Assert.False(Directory.Exists(directory));
            using var watcher = WindowTitleScanner.CreateSettingsWatcher(directory, () => changed.Set());
            Assert.True(Directory.Exists(directory));
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), "{}");
            Assert.True(await Task.Run(() => changed.WaitOne(TimeSpan.FromSeconds(5))));
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
