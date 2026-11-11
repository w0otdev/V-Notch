using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class RuntimeLogPrivacyObsTests
{
    [Fact]
    public async Task AsyncLogWriter_ConcurrentEnqueueFlushAndDispose_CompletesPromptlyWithoutSpinning()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "VNotch_RuntimeLogTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            for (int iteration = 0; iteration < 15; iteration++)
            {
                string logPath = Path.Combine(tempDir, $"test-{iteration}.log");
                var writer = new RuntimeLog.AsyncLogWriter(logPath);
                using var barrier = new ManualResetEventSlim(false);

                var tasks = new Task[6];
                for (int p = 0; p < 4; p++)
                {
                    int producerId = p;
                    tasks[p] = Task.Run(() =>
                    {
                        barrier.Wait();
                        for (int i = 0; i < 200; i++)
                        {
                            writer.TryEnqueue($"[INFO] [TEST] producer={producerId} index={i}{Environment.NewLine}", LogLevel.Info);
                        }
                    });
                }

                tasks[4] = Task.Run(async () =>
                {
                    barrier.Wait();
                    for (int i = 0; i < 10; i++)
                    {
                        await writer.EnqueueFlush().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                });

                tasks[5] = Task.Run(async () =>
                {
                    barrier.Wait();
                    await Task.Delay(5);
                    writer.Dispose();
                });

                barrier.Set();

                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
                await writer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(writer.Completion.IsCompletedSuccessfully);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* Best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task PrivacyIndicatorService_ConcurrentStateAndCandidatesReadWrite_NoTornOrStaleReferences()
    {
        using var service = new PrivacyIndicatorService(
            TimeSpan.FromMilliseconds(50),
            static _ => Array.Empty<CapabilityUsage>(),
            static () => false,
            new ScreenRecordingProbe());

        var candidatesA = new CapabilityUsage[]
        {
            new("Discord.exe", "Discord", 100L),
            new("Zoom.exe", "Zoom", 200L)
        };
        var candidatesNamesA = new string[] { "Discord", "Zoom" };
        var camsA = new string[] { "OBS" };
        var locsA = new string[] { "Maps" };

        var scanActive = new PrivacyIndicatorService.PrivacyScanResult(
            MicrophoneCandidates: candidatesA,
            MicrophoneCandidateNames: candidatesNamesA,
            CameraConsumers: camsA,
            CameraInUse: true,
            ScreenRecordingActive: true,
            LocationConsumers: locsA,
            UtcNow: DateTime.UtcNow);

        var scanIdle = new PrivacyIndicatorService.PrivacyScanResult(
            MicrophoneCandidates: Array.Empty<CapabilityUsage>(),
            MicrophoneCandidateNames: Array.Empty<string>(),
            CameraConsumers: Array.Empty<string>(),
            CameraInUse: false,
            ScreenRecordingActive: false,
            LocationConsumers: Array.Empty<string>(),
            UtcNow: DateTime.UtcNow);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var writerTask = Task.Run(() =>
        {
            for (int i = 0; i < 2000 && !cts.Token.IsCancellationRequested; i++)
            {
                service.ApplyScanResult((i & 1) == 0 ? scanActive : scanIdle);
            }
        });

        var readerTasks = new Task[4];
        for (int r = 0; r < readerTasks.Length; r++)
        {
            readerTasks[r] = Task.Run(() =>
            {
                while (!writerTask.IsCompleted && !cts.Token.IsCancellationRequested)
                {
                    var candidates = service.MicrophoneCandidates;
                    Assert.NotNull(candidates);
                    int count = candidates.Count;
                    Assert.True(count == 0 || count == 2);
                    if (count == 2)
                    {
                        Assert.Equal("Discord", candidates[0].DisplayName);
                        Assert.Equal("Zoom", candidates[1].DisplayName);
                    }

                    var state = service.CurrentState;
                    Assert.NotNull(state);
                    Assert.NotNull(state.CameraConsumers);
                    Assert.NotNull(state.LocationConsumers);
                    Assert.NotNull(state.MicrophoneConsumers);
                    if (state.CameraInUse)
                    {
                        Assert.Single(state.CameraConsumers);
                        Assert.Equal("OBS", state.CameraConsumers[0]);
                        Assert.True(state.ScreenRecordingActive);
                        Assert.True(state.LocationInUse);
                    }
                    else
                    {
                        Assert.Empty(state.CameraConsumers);
                        Assert.False(state.ScreenRecordingActive);
                        Assert.False(state.LocationInUse);
                    }
                }
            });
        }

        await writerTask.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(readerTasks).WaitAsync(TimeSpan.FromSeconds(5));

        service.ApplyScanResult(scanActive);
        Assert.Equal(2, service.MicrophoneCandidates.Count);
        Assert.True(service.CurrentState.CameraInUse);

        service.ApplyScanResult(scanIdle);
        Assert.Empty(service.MicrophoneCandidates);
        Assert.False(service.CurrentState.AnyInUse);
    }
}
