using System;
using System.IO;
using System.Threading.Tasks;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class RuntimeLogTests
{
    [Fact]
    public async Task LongRunningSession_RotatesAtSizeLimit_AndBoundsOversizedEntries()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vnotch-log-cap-{Guid.NewGuid():N}.log");
        var previous = RuntimeLog.MinimumLevel;
        const int limit = 5 * 1024 * 1024;
        try
        {
            RuntimeLog.MinimumLevel = LogLevel.Info;
            RuntimeLog.InitializeNewSession(path);
            File.WriteAllText(path, new string('x', limit - 100));
            RuntimeLog.Info("SIZE-TEST", new string('y', 200));
            await RuntimeLog.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(path + ".old"));
            Assert.True(new FileInfo(path).Length <= limit);
            Assert.True(new FileInfo(path + ".old").Length <= limit);
            Assert.Contains("SIZE-TEST", File.ReadAllText(path));

            RuntimeLog.Info("OVERSIZED-TEST", new string('z', limit + 1));
            await RuntimeLog.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(new FileInfo(path).Length <= limit);
            Assert.Contains("[truncated]", File.ReadAllText(path));

            File.WriteAllText(path, new string('x', limit - 100));
            using (var lockedBackup = new FileStream(path + ".old", FileMode.Open, FileAccess.Read, FileShare.None))
            {
                RuntimeLog.Info("LOCKED-BACKUP", new string('a', 200));
                await RuntimeLog.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(limit - 100, new FileInfo(path).Length);
            }
            RuntimeLog.Info("AFTER-UNLOCK", new string('b', 200));
            await RuntimeLog.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("AFTER-UNLOCK", File.ReadAllText(path));
        }
        finally
        {
            RuntimeLog.Shutdown(TimeSpan.FromSeconds(5));
            RuntimeLog.MinimumLevel = previous;
            File.Delete(path); File.Delete(path + ".old");
        }
    }

    [Fact]
    public async Task FlushAsync_WritesQueuedEntriesInOrder()
    {
        string logPath = Path.Combine(Path.GetTempPath(), $"vnotch-runtime-log-{Guid.NewGuid():N}.log");
        LogLevel previousMinimumLevel = RuntimeLog.MinimumLevel;

        try
        {
            RuntimeLog.MinimumLevel = LogLevel.Trace;
            RuntimeLog.InitializeNewSession(logPath);

            for (int i = 0; i < 300; i++)
            {
                RuntimeLog.Info("ASYNC-TEST", $"entry-{i:D3}");
            }

            await RuntimeLog.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

            string contents;
            using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                contents = await reader.ReadToEndAsync();
            }
            int previousPosition = -1;
            for (int i = 0; i < 300; i++)
            {
                int position = contents.IndexOf($"entry-{i:D3}", StringComparison.Ordinal);
                Assert.True(position > previousPosition, $"Log entry {i} was missing or out of order.");
                previousPosition = position;
            }
        }
        finally
        {
            RuntimeLog.Shutdown(TimeSpan.FromSeconds(5));
            RuntimeLog.MinimumLevel = previousMinimumLevel;

            try
            {
                File.Delete(logPath);
                File.Delete(logPath + ".old");
            }
            catch
            {
                // Best-effort cleanup for test log files.
            }
        }
    }

    [Fact]
    public void ClearLog_PurgesLogFilesAndExecutesWithoutException()
    {
        string logPath = Path.Combine(Path.GetTempPath(), $"vnotch-clearlog-{Guid.NewGuid():N}.log");
        LogLevel previousMinimumLevel = RuntimeLog.MinimumLevel;

        try
        {
            RuntimeLog.MinimumLevel = LogLevel.Debug;
            RuntimeLog.InitializeNewSession(logPath);
            RuntimeLog.Log("TEST", "Sample test log entry");

            RuntimeLog.ClearLog();

            Assert.NotNull(RuntimeLog.LogPath);
        }
        finally
        {
            RuntimeLog.Shutdown(TimeSpan.FromSeconds(5));
            RuntimeLog.MinimumLevel = previousMinimumLevel;
            try
            {
                if (File.Exists(logPath)) File.Delete(logPath);
                if (File.Exists(logPath + ".old")) File.Delete(logPath + ".old");
            }
            catch
            {
                // Best-effort cleanup for test log files.
            }
        }
    }

    [Fact]
    public async Task InitializeNewSession_RotatesPreviousSessionToOldFile()
    {
        string logPath = Path.Combine(Path.GetTempPath(), $"vnotch-session-rotate-{Guid.NewGuid():N}.log");
        string oldPath = logPath + ".old";
        LogLevel previousMinimumLevel = RuntimeLog.MinimumLevel;

        try
        {
            RuntimeLog.MinimumLevel = LogLevel.Info;
            RuntimeLog.InitializeNewSession(logPath);
            RuntimeLog.Info("SESSION-1", "This is session 1 data");
            await RuntimeLog.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(File.Exists(logPath));
            string session1Content = await File.ReadAllTextAsync(logPath);
            Assert.Contains("SESSION-1", session1Content);

            // Start session 2
            RuntimeLog.InitializeNewSession(logPath);
            RuntimeLog.Info("SESSION-2", "This is session 2 data");
            await RuntimeLog.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(File.Exists(oldPath), "Previous session log was not rotated to .old");
            string oldContent = await File.ReadAllTextAsync(oldPath);
            Assert.Contains("SESSION-1", oldContent);

            string currentContent = await File.ReadAllTextAsync(logPath);
            Assert.Contains("SESSION-2", currentContent);
            Assert.DoesNotContain("SESSION-1", currentContent);
        }
        finally
        {
            RuntimeLog.Shutdown(TimeSpan.FromSeconds(5));
            RuntimeLog.MinimumLevel = previousMinimumLevel;
            try
            {
                if (File.Exists(logPath)) File.Delete(logPath);
                if (File.Exists(oldPath)) File.Delete(oldPath);
            }
            catch
            {
                // Best-effort cleanup for test log files.
            }
        }
    }
}
