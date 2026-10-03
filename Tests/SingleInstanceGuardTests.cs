using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class SingleInstanceGuardTests
{
    private const string TestMutexName = "VNotch_Test_SingleInstance_Mutex";

    [Fact]
    public void ApplicationMutex_IsScopedToCurrentUserAndSession()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        Assert.Equal($@"Local\VNotch_SingleInstance_{identity.User!.Value}", SingleInstanceGuard.GetCurrentUserMutexName());
    }

    [Fact]
    public void TryAcquire_FirstCall_ReturnsTrue()
    {
        using var guard = new SingleInstanceGuard(TestMutexName + "_acquire_first");
        var result = guard.TryAcquire();
        Assert.True(result);
        Assert.True(guard.IsOwned);
    }

    [Fact]
    public void TryAcquire_SecondInstance_ReturnsFalse()
    {
        using var guard1 = new SingleInstanceGuard(TestMutexName + "_second_instance");
        using var guard2 = new SingleInstanceGuard(TestMutexName + "_second_instance");

        var firstResult = guard1.TryAcquire();
        Assert.True(firstResult);

        var secondResult = guard2.TryAcquire();
        Assert.False(secondResult);
    }

    [Fact]
    public void Release_ReleasesMutex_AllowsAnotherInstance()
    {
        SingleInstanceGuard? guard1 = null;
        SingleInstanceGuard? guard2 = null;
        try
        {
            guard1 = new SingleInstanceGuard(TestMutexName + "_release");
            Assert.True(guard1.TryAcquire());

            guard1.Release();
            Assert.False(guard1.IsOwned);

            // Dispose guard1 to close its handle so the kernel object is released
            guard1.Dispose();
            guard1 = null;

            guard2 = new SingleInstanceGuard(TestMutexName + "_release");
            Assert.True(guard2.TryAcquire());
        }
        finally
        {
            guard2?.Dispose();
            guard1?.Dispose();
        }
    }

    [Fact]
    public void TryWaitForPreviousInstance_WithoutAcquire_ReturnsFalse()
    {
        using var guard = new SingleInstanceGuard(TestMutexName + "_wait_no_acquire");
        var result = guard.TryWaitForPreviousInstance(TimeSpan.FromMilliseconds(100));
        Assert.False(result);
    }

    [Fact]
    public async Task TryWaitForPreviousInstance_WaitsAndAcquires()
    {
        using var guard1 = new SingleInstanceGuard(TestMutexName + "_wait_acquire");
        using var guard2 = new SingleInstanceGuard(TestMutexName + "_wait_acquire");

        Assert.True(guard1.TryAcquire());

        // guard2 tries to acquire (fails), then waits for guard1 to release
        Assert.False(guard2.TryAcquire());

        // Start a task that releases guard1 after a short delay
        var releaseTask = Task.Run(async () =>
        {
            await Task.Delay(200);
            guard1.Release();
        });

        var waited = guard2.TryWaitForPreviousInstance(TimeSpan.FromSeconds(5));
        Assert.True(waited);
        Assert.True(guard2.IsOwned);

        await releaseTask;
    }

    [Fact]
    public async Task TryWaitForPreviousInstance_TimesOutWhileOwnerRemainsActive()
    {
        var name = TestMutexName + "_timeout";
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var ownerTask = Task.Run(() =>
        {
            using var owner = new SingleInstanceGuard(name);
            Assert.True(owner.TryAcquire());
            ready.Set();
            release.Wait();
        });
        ready.Wait();
        using var nonOwner = new SingleInstanceGuard(name);
        Assert.False(nonOwner.TryAcquire());

        Assert.False(nonOwner.TryWaitForPreviousInstance(TimeSpan.FromMilliseconds(20)));
        Assert.False(nonOwner.IsOwned);
        release.Set();
        await ownerTask;
    }

    [Fact]
    public void Dispose_ReleasesAndCleansUp()
    {
        var guard = new SingleInstanceGuard(TestMutexName + "_dispose");
        Assert.True(guard.TryAcquire());

        guard.Dispose();
        Assert.False(guard.IsOwned);

        // After disposal, another guard should be able to acquire
        using var guard2 = new SingleInstanceGuard(TestMutexName + "_dispose");
        Assert.True(guard2.TryAcquire());
    }

    [Fact]
    public void Release_WhenNotOwned_DoesNotThrow()
    {
        using var guard = new SingleInstanceGuard(TestMutexName + "_release_not_owned");
        // Release without acquiring first — should be a no-op
        guard.Release();
        Assert.False(guard.IsOwned);
    }

    [Fact]
    public void DoubleDispose_DoesNotThrow()
    {
        var guard = new SingleInstanceGuard(TestMutexName + "_double_dispose");
        guard.TryAcquire();
        guard.Dispose();
        // Second dispose should be safe
        guard.Dispose();
    }
}
