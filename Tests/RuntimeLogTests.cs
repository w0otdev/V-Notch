using System;
using System.IO;
using System.Threading.Tasks;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class RuntimeLogTests
{
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
            }
        }
    }
}
