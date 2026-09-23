using System.Reflection;
using VNotch.Controls;
using Xunit;

namespace VNotch.Tests;

public sealed class MusicVisualizerLeaseTests
{
    [Fact]
    public void ColdLeaseAndLastReleaseDoNotWaitForCaptureInitialization()
    {
        SharedStaTestRunner.Run(() =>
        {
            const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.NonPublic;
            var visualizer = new MusicVisualizer();
            var acquire = typeof(MusicVisualizer).GetMethod("AcquireCaptureLease", instanceFlags)!.CreateDelegate<Action>(visualizer);
            var release = typeof(MusicVisualizer).GetMethod("ReleaseCaptureLease", instanceFlags)!.CreateDelegate<Action>(visualizer);
            var captureLock = typeof(MusicVisualizer).GetField("_lockObj", staticFlags)!.GetValue(null)!;
            var request = typeof(MusicVisualizer).GetMethod("RequestCaptureUpdate", staticFlags)!.CreateDelegate<Action<bool>>();
            var count = typeof(MusicVisualizer).GetField("_captureLeaseCount", staticFlags)!;
            int initialCount = (int)count.GetValue(null)!;
            using var entered = new ManualResetEventSlim();
            using var allowExit = new ManualResetEventSlim();
            Task worker = Task.Run(() =>
            {
                lock (captureLock)
                {
                    entered.Set();
                    if (!allowExit.Wait(TimeSpan.FromSeconds(3)))
                        throw new TimeoutException("Cold visualizer interaction blocked behind capture initialization");
                }
            });
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
                acquire();
                request(false);
                Assert.Equal(initialCount + 1, (int)count.GetValue(null)!);
                release();
                Assert.Equal(initialCount, (int)count.GetValue(null)!);
                Assert.False(worker.IsCompleted);
            }
            finally
            {
                allowExit.Set();
                try { worker.GetAwaiter().GetResult(); }
                finally { release(); }
            }
        });
    }

    [Fact]
    public void ExistingLeaseDoesNotWaitForAudioCallbackLockOrIncreaseLeaseCount()
    {
        SharedStaTestRunner.Run(() =>
        {
            const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.NonPublic;
            var visualizer = new MusicVisualizer();
            var acquire = typeof(MusicVisualizer).GetMethod("AcquireCaptureLease", instanceFlags)!.CreateDelegate<Action>(visualizer);
            var release = typeof(MusicVisualizer).GetMethod("ReleaseCaptureLease", instanceFlags)!.CreateDelegate<Action>(visualizer);
            var count = typeof(MusicVisualizer).GetField("_captureLeaseCount", staticFlags)!;
            var captureLock = typeof(MusicVisualizer).GetField("_lockObj", staticFlags)!.GetValue(null)!;
            int initialCount = (int)count.GetValue(null)!;
            acquire();
            using var entered = new ManualResetEventSlim();
            using var allowExit = new ManualResetEventSlim();
            Task worker = Task.Run(() =>
            {
                lock (captureLock)
                {
                    entered.Set();
                    if (!allowExit.Wait(TimeSpan.FromSeconds(3)))
                        throw new TimeoutException("UI frame waited behind the audio callback");
                }
            });
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
                for (int frame = 0; frame < 240; frame++) acquire();
                Assert.False(worker.IsCompleted);
                Assert.Equal(initialCount + 1, (int)count.GetValue(null)!);
            }
            finally
            {
                allowExit.Set();
                try { worker.GetAwaiter().GetResult(); }
                finally { release(); }
            }
            Assert.Equal(initialCount, (int)count.GetValue(null)!);
        });
    }
}
