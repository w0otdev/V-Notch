using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Runtime.InteropServices;
using VNotch.Controllers;
using VNotch.Models;
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

    [Fact]
    public void MediaSessionSelectionPolicy_SelectNewlyPlayingSession_MatchesDistinctWrappersWhenEquivalenceProvided()
    {
        var first = new SessionWrapper("chrome.exe#1");
        var second = new SessionWrapper("chrome.exe#2");
        var osCurrentWrapper = new SessionWrapper("chrome.exe#2");

        Assert.NotSame(second, osCurrentWrapper);
        Assert.Null(MediaSessionSelectionPolicy.SelectNewlyPlayingSession(new[] { first, second }, osCurrentWrapper));

        var selected = MediaSessionSelectionPolicy.SelectNewlyPlayingSession(
            new[] { first, second },
            osCurrentWrapper,
            static (a, b) => string.Equals(a.Key, b.Key, StringComparison.Ordinal));

        Assert.Same(second, selected);
        Assert.Null(MediaSessionSelectionPolicy.SelectNewlyPlayingSession(
            new[] { first, second },
            null,
            static (a, b) => string.Equals(a.Key, b.Key, StringComparison.Ordinal)));
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(nint thisPtr, in Guid riid, out nint ppvObject);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint AddRefReleaseDelegate(nint thisPtr);

    private static readonly Guid IUnknownGuid = new("00000000-0000-0000-C000-000000000046");

    private static readonly QueryInterfaceDelegate NativeQi = static (nint thisPtr, in Guid riid, out nint ppvObject) =>
    {
        if (riid == IUnknownGuid)
        {
            ppvObject = thisPtr;
            return 0;
        }
        ppvObject = 0;
        return unchecked((int)0x80004002);
    };

    private static readonly AddRefReleaseDelegate NativeAddRefRelease = static _ => 1;

    [Fact]
    public async Task MediaTransportControlService_PreviousTrackAsync_NavigatesBrowserBackForDistinctSessionWrappersAndRcws()
    {
        nint vtbl = Marshal.AllocHGlobal(IntPtr.Size * 3);
        nint unk = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Marshal.WriteIntPtr(vtbl, 0, Marshal.GetFunctionPointerForDelegate(NativeQi));
            Marshal.WriteIntPtr(vtbl, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(NativeAddRefRelease));
            Marshal.WriteIntPtr(vtbl, IntPtr.Size * 2, Marshal.GetFunctionPointerForDelegate(NativeAddRefRelease));
            Marshal.WriteIntPtr(unk, 0, vtbl);

            object rcw1 = Marshal.GetUniqueObjectForIUnknown(unk);
            object rcw2 = Marshal.GetUniqueObjectForIUnknown(unk);
            try
            {
                Assert.NotSame(rcw1, rcw2);
                Assert.True(Marshal.IsComObject(rcw1));
                Assert.True(Marshal.IsComObject(rcw2));

                var firstWrapper = new TestTransportSession(rcw1, "chrome.exe", "Browser Video");
                var secondWrapper = new TestTransportSession(rcw2, "chrome.exe", "Browser Video");
                int calls = 0;
                var keys = new List<byte>();
                MediaInfo? navigatedInfo = null;

                var service = new MediaTransportControlService(
                    () => calls++ == 0 ? firstWrapper : secondWrapper,
                    keys.Add,
                    _ => true,
                    info => { navigatedInfo = info; return true; });

                await service.PreviousTrackAsync();

                Assert.NotNull(navigatedInfo);
                Assert.Equal("Browser Video", navigatedInfo!.CurrentTrack);
                Assert.Equal("chrome.exe", navigatedInfo.SourceAppId);
                Assert.Empty(keys);
            }
            finally
            {
                if (Marshal.IsComObject(rcw1)) Marshal.ReleaseComObject(rcw1);
                if (Marshal.IsComObject(rcw2)) Marshal.ReleaseComObject(rcw2);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(unk);
            Marshal.FreeHGlobal(vtbl);
        }
    }

    [Fact]
    public void MediaWindowActivator_IsMediaBrowserWindow_MatchesResolvedBrowserNameWithoutAllocatingProcess()
    {
        int processLookups = 0;
        bool matched = MediaWindowActivator.IsMediaBrowserWindowCore(
            new IntPtr(0x1234),
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            processId: 4242,
            resolveProcessName: static _ => "chrome",
            isChromiumWindow: static _ => false,
            getProcessById: _ =>
            {
                processLookups++;
                throw new InvalidOperationException("Process.GetProcessById should not be called when resolvedName matches.");
            });

        Assert.True(matched);
        Assert.Equal(0, processLookups);

        bool nonBrowser = MediaWindowActivator.IsMediaBrowserWindowCore(
            new IntPtr(0x1234),
            "Spotify.exe",
            processId: 4242,
            resolveProcessName: static _ => "Spotify",
            isChromiumWindow: static _ => false,
            getProcessById: _ =>
            {
                processLookups++;
                throw new InvalidOperationException("Process.GetProcessById should not be called for non-browser windows.");
            });

        Assert.False(nonBrowser);
        Assert.Equal(0, processLookups);
    }

    private sealed record SessionWrapper(string Key);

    private sealed class TestTransportSession(object identity, string sourceAppId, string title) : IMediaTransportSession
    {
        public object Identity => identity;
        public string SourceAppUserModelId => sourceAppId;
        public MediaTransportControls? GetControls() => new(IsNextEnabled: false, IsPreviousEnabled: false);
        public MediaTransportTimeline? GetTimelineProperties() => new(TimeSpan.Zero, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(1));
        public Task<bool> TryTogglePlayPauseAsync() => Task.FromResult(false);
        public Task<bool> TrySkipNextAsync() => Task.FromResult(false);
        public Task<bool> TrySkipPreviousAsync() => Task.FromResult(false);
        public Task<bool> TryChangePlaybackPositionAsync(long ticks) => Task.FromResult(false);
        public Task<string?> GetTitleAsync() => Task.FromResult<string?>(title);
    }
}
