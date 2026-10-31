using System;
using System.IO;
using System.Threading.Tasks;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("DiagnosticLogging")]
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
            await RuntimeLog.InitializeNewSessionAsync(path);
            await File.WriteAllTextAsync(path, new string('x', limit - 100));
            RuntimeLog.Info("SIZE-TEST", new string('y', 200));
            await RuntimeLog.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(path + ".old"));
            Assert.True(new FileInfo(path).Length <= limit);
            Assert.True(new FileInfo(path + ".old").Length <= limit);
            Assert.Contains("SIZE-TEST", await ReadSharedTextAsync(path));

            RuntimeLog.Info("OVERSIZED-TEST", new string('z', limit + 1));
            await RuntimeLog.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(new FileInfo(path).Length <= limit);
            Assert.Contains("[truncated]", await ReadSharedTextAsync(path));

            await File.WriteAllTextAsync(path, new string('x', limit - 100));
            using (var lockedBackup = new FileStream(path + ".old", FileMode.Open, FileAccess.Read, FileShare.None))
            {
                RuntimeLog.Info("OTHER-SERVICE", "background entry.");
                await RuntimeLog.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
                long lengthBeforeOverflow = new FileInfo(path).Length;
                Assert.True(lengthBeforeOverflow <= limit);
                RuntimeLog.Info("LOCKED-BACKUP", new string('a', 200));
                await RuntimeLog.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(lengthBeforeOverflow, new FileInfo(path).Length);
            }
            RuntimeLog.Info("AFTER-UNLOCK", new string('b', 200));
            await RuntimeLog.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("AFTER-UNLOCK", await ReadSharedTextAsync(path));
        }
        finally
        {
            await RuntimeLog.ShutdownAsync(TimeSpan.FromSeconds(5));
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
            await RuntimeLog.InitializeNewSessionAsync(logPath);

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
            await RuntimeLog.ShutdownAsync(TimeSpan.FromSeconds(5));
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
    public async Task ClearLog_PurgesLogFilesAndExecutesWithoutException()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"vnotch-clearlog-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string logPath = Path.Combine(tempDir, "vnotch-debug.log");
        string primaryCrash = Path.Combine(tempDir, "primary-vnotch-crash.log");
        string secondaryCrash = Path.Combine(tempDir, "secondary-vnotch-crash.log");
        string fallbackCrash = Path.Combine(tempDir, "fallback-vnotch-crash.log");

        LogLevel previousMinimumLevel = RuntimeLog.MinimumLevel;
        string originalPrimaryCrash = CrashReporter.CrashLogPath;
        string originalSecondaryCrash = CrashReporter.SecondaryCrashLogPath;
        string originalFallbackCrash = CrashReporter.FallbackCrashLogPath;

        try
        {
            CrashReporter.CrashLogPath = primaryCrash;
            CrashReporter.SecondaryCrashLogPath = secondaryCrash;
            CrashReporter.FallbackCrashLogPath = fallbackCrash;

            RuntimeLog.MinimumLevel = LogLevel.Debug;
            await RuntimeLog.InitializeNewSessionAsync(logPath);
            RuntimeLog.Log("TEST", "Sample test log entry");
            await RuntimeLog.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllTextAsync(logPath + ".old", "old debug log");

            await File.WriteAllTextAsync(primaryCrash, "primary crash");
            await File.WriteAllTextAsync(primaryCrash + ".old", "primary crash old");
            await File.WriteAllTextAsync(secondaryCrash, "secondary crash");
            await File.WriteAllTextAsync(secondaryCrash + ".old", "secondary crash old");
            await File.WriteAllTextAsync(fallbackCrash, "fallback crash");
            await File.WriteAllTextAsync(fallbackCrash + ".old", "fallback crash old");

            await RuntimeLog.ClearLogAsync();

            Assert.NotNull(RuntimeLog.LogPath);
            Assert.False(File.Exists(logPath + ".old"));
            Assert.False(File.Exists(primaryCrash));
            Assert.False(File.Exists(primaryCrash + ".old"));
            Assert.False(File.Exists(secondaryCrash));
            Assert.False(File.Exists(secondaryCrash + ".old"));
            Assert.False(File.Exists(fallbackCrash));
            Assert.False(File.Exists(fallbackCrash + ".old"));
        }
        finally
        {
            await RuntimeLog.ShutdownAsync(TimeSpan.FromSeconds(5));
            RuntimeLog.MinimumLevel = previousMinimumLevel;
            CrashReporter.CrashLogPath = originalPrimaryCrash;
            CrashReporter.SecondaryCrashLogPath = originalSecondaryCrash;
            CrashReporter.FallbackCrashLogPath = originalFallbackCrash;
            try
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
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
            await RuntimeLog.InitializeNewSessionAsync(logPath);
            RuntimeLog.Info("SESSION-1", "This is session 1 data");
            await RuntimeLog.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(File.Exists(logPath));
            string session1Content = await ReadSharedTextAsync(logPath);
            Assert.Contains("SESSION-1", session1Content);

            // Start session 2
            await RuntimeLog.InitializeNewSessionAsync(logPath);
            RuntimeLog.Info("SESSION-2", "This is session 2 data");
            await RuntimeLog.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(File.Exists(oldPath), "Previous session log was not rotated to .old");
            string oldContent = await ReadSharedTextAsync(oldPath);
            Assert.Contains("SESSION-1", oldContent);

            string currentContent = await ReadSharedTextAsync(logPath);
            Assert.Contains("SESSION-2", currentContent);
            Assert.DoesNotContain("SESSION-1", currentContent);
        }
        finally
        {
            await RuntimeLog.ShutdownAsync(TimeSpan.FromSeconds(5));
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

    private static async Task<string> ReadSharedTextAsync(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
