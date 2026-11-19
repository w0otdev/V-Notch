using VNotch.Controls;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MusicVisualizerLeaseTests
{
    [Fact]
    public void ColdLeaseAndLastReleaseDoNotWaitForCaptureInitialization()
    {
        SharedStaTestRunner.Run(() =>
        {
            var visualizer = new MusicVisualizer();
            int initialCount = MusicVisualizer.CaptureLeaseCount;
            using var entered = new ManualResetEventSlim();
            using var allowExit = new ManualResetEventSlim();
            Task worker = Task.Run(() =>
            {
                lock (MusicVisualizer.AudioStateLock)
                {
                    entered.Set();
                    if (!allowExit.Wait(TimeSpan.FromSeconds(3)))
                        throw new TimeoutException("Cold visualizer interaction blocked behind capture initialization");
                }
            });
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
                visualizer.AcquireCaptureLease();
                MusicVisualizer.RequestCaptureUpdate(false);
                Assert.Equal(initialCount + 1, MusicVisualizer.CaptureLeaseCount);
                visualizer.ReleaseCaptureLease();
                Assert.Equal(initialCount, MusicVisualizer.CaptureLeaseCount);
                Assert.False(worker.IsCompleted);
            }
            finally
            {
                allowExit.Set();
                try { worker.GetAwaiter().GetResult(); }
                finally { visualizer.ReleaseCaptureLease(); }
            }
        });
    }

    [Fact]
    public void ExistingLeaseDoesNotWaitForAudioCallbackLockOrIncreaseLeaseCount()
    {
        SharedStaTestRunner.Run(() =>
        {
            var visualizer = new MusicVisualizer();
            int initialCount = MusicVisualizer.CaptureLeaseCount;
            visualizer.AcquireCaptureLease();
            using var entered = new ManualResetEventSlim();
            using var allowExit = new ManualResetEventSlim();
            Task worker = Task.Run(() =>
            {
                lock (MusicVisualizer.AudioStateLock)
                {
                    entered.Set();
                    if (!allowExit.Wait(TimeSpan.FromSeconds(3)))
                        throw new TimeoutException("UI frame waited behind the audio callback");
                }
            });
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
                for (int frame = 0; frame < 240; frame++) visualizer.AcquireCaptureLease();
                Assert.False(worker.IsCompleted);
                Assert.Equal(initialCount + 1, MusicVisualizer.CaptureLeaseCount);
            }
            finally
            {
                allowExit.Set();
                try { worker.GetAwaiter().GetResult(); }
                finally { visualizer.ReleaseCaptureLease(); }
            }
            Assert.Equal(initialCount, MusicVisualizer.CaptureLeaseCount);
        });
    }
}
