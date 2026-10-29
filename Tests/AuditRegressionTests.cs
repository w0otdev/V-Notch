using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using VNotch.Controllers;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class AuditRegressionTests
{
    [Fact]
    public async Task BluetoothController_NewShowCancelsPendingTimerAndNeverFiresStaleDismiss()
    {
        using var controller = new BluetoothNotificationController { AutoDismissMs = 160 };
        using var dismissed = new ManualResetEventSlim(false);
        int shows = 0;
        int dismisses = 0;
        controller.ShowRequested += (_, _) => Interlocked.Increment(ref shows);
        controller.DismissRequested += () => { Interlocked.Increment(ref dismisses); dismissed.Set(); };

        var first = new BluetoothDeviceInfo { Name = "Mouse", DeviceType = BluetoothDeviceType.Mouse };
        var second = new BluetoothDeviceInfo { Name = "Buds", DeviceType = BluetoothDeviceType.Headphones };

        Assert.True(controller.TryShow(first, connected: true, isNotchVisible: true, isAnimating: false, isExpanded: false, isMusicExpanded: false));
        await Task.Delay(80);
        long secondShownAt = Environment.TickCount64;
        Assert.True(controller.TryShow(second, connected: true, isNotchVisible: true, isAnimating: false, isExpanded: false, isMusicExpanded: false));
        await Task.Delay(100);

        // First timer window has elapsed, but second toast is still within its 160 ms window.
        Assert.Equal(2, Volatile.Read(ref shows));
        if (Environment.TickCount64 - secondShownAt < 120)
        {
            Assert.Equal(0, Volatile.Read(ref dismisses));
            Assert.True(controller.IsVisible);
        }

        Assert.True(dismissed.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref dismisses));
        Assert.False(controller.IsVisible);
    }

    [Fact]
    public void MediaSourceCache_PreservesUpdatedLruOrderAcrossSaveAndLoad()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"vnotch-lru-{Guid.NewGuid():N}.json");
        try
        {
            var cache = new MediaSourceCache(tempPath, capacity: 2);
            cache.SetBoth("oldest", "", "Spotify");
            cache.SetBoth("newest", "", "YouTube");

            // Touch "oldest" so it becomes the most recently used entry; "newest" is now the LRU eviction target.
            Assert.True(cache.TryGet("oldest", out _));
            cache.Save();

            var reloaded = new MediaSourceCache(tempPath, capacity: 2);
            reloaded.Load();
            reloaded.SetBoth("third", "", "SoundCloud");

            Assert.True(reloaded.TryGet("oldest", out var preserved));
            Assert.Equal("Spotify", preserved);
            Assert.False(reloaded.TryGet("newest", out _));
            Assert.True(reloaded.TryGet("third", out _));
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void UpdateService_IsApprovedUpdate_AcceptsCaseInsensitiveInstallerName()
    {
        var update = new UpdateInfo
        {
            Version = "2.1.0",
            InstallerName = "v-notch-setup-selfcontained.exe",
            DownloadUrl = "https://github.com/w0otdev/V-Notch/releases/download/v2.1.0/v-notch-setup-selfcontained.exe",
            ManifestUrl = "https://github.com/w0otdev/V-Notch/releases/download/v2.1.0/v-notch-setup-selfcontained.exe.manifest.json",
            ManifestSignatureUrl = "https://github.com/w0otdev/V-Notch/releases/download/v2.1.0/v-notch-setup-selfcontained.exe.manifest.json.sig"
        };

        Assert.True(UpdateService.IsApprovedUpdate(update));
    }
}
