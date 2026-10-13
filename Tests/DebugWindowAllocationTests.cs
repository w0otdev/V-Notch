using VNotch.Models;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class DebugWindowAllocationTests
{
    [Fact]
    public void UnchangedMetricsAndRoundedFpsDoNotAllocateDisplayStrings() => SharedStaTestRunner.Run(() =>
    {
        LiquidGlassSpotlightTests.CreateApplicationResources();
        var window = new DebugWindow();
        try
        {
            var snapshot = new PerformanceDebugSnapshot
            {
                Fps = 60,
                RefreshRateHz = 120,
                FrameTimeMs = 16.7,
                DispatcherLatencyMs = 0.5,
                ProcessCpuPercent = 2.5,
                ProcessThreadCount = 12,
                ProcessHandleCount = 100,
                ProcessWorkingSetBytes = 5 * 1024 * 1024,
                ProcessPrivateBytes = 4 * 1024 * 1024,
                ManagedHeapBytes = 3 * 1024 * 1024,
                AllocBytesPerSec = 1024,
                GcGen0Count = 1,
                GcGen1Count = 2,
                GcGen2Count = 3,
                ProcessGpuPercent = 1.5,
                GlobalCpuPercent = 25,
                GlobalRamTotalBytes = 8UL * 1024 * 1024 * 1024,
                GlobalRamUsedBytes = 4UL * 1024 * 1024 * 1024,
                GlobalRamAvailBytes = 4UL * 1024 * 1024 * 1024,
                GlobalRamPercent = 50,
                GlobalGpuPercent = 20,
                DedicatedVramBytes = 2UL * 1024 * 1024 * 1024,
                NetDownBytesPerSec = 2048,
                NetUpBytesPerSec = 512
            };

            // Warm WPF property access, formatting and generic value comparers.
            for (int i = 0; i < 50; i++) window.UpdateSnapshot(snapshot);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                window.UpdateSnapshot(snapshot);
                window.UpdateFps(i % 2 == 0 ? 59.8 : 60.2);
                window.UpdateRefreshRate(120);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal("60 FPS", window.FpsSummaryText.Text);
            Assert.True(allocated < 4096, $"Unchanged metric updates allocated {allocated} bytes.");
        }
        finally { window.Close(); }
    });
}
