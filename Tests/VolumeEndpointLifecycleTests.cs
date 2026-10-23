using System.Runtime.InteropServices;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class VolumeEndpointLifecycleTests
{
    [Fact]
    public void DefaultDeviceChangeMovesVolumeAndMuteOperationsToNewEndpoint()
    {
        var speakers = new FakeEndpoint { Volume = 0.2f };
        var headphones = new FakeEndpoint { Volume = 0.8f, IsMuted = true };
        var source = new FakeSource { Current = speakers };
        using var service = new VolumeService(source);
        Assert.Equal(0.2f, service.GetVolume());

        source.Current = headphones;
        source.NotifyChange();
        Assert.Equal(0, speakers.DisposeCount); // Notification callback only invalidates.

        Assert.Equal(0.8f, service.GetVolume());
        Assert.Equal(1, speakers.DisposeCount);
        Assert.True(service.GetMute());
        Assert.True(service.SetVolume(2));
        service.ToggleMute();
        Assert.Equal(1, headphones.Volume);
        Assert.False(headphones.IsMuted);
        Assert.Equal(0.2f, speakers.Volume);
        Assert.Equal(2, source.OpenCount);
    }

    [Fact]
    public void DeviceMissingAfterUnplugRecoversWhenNewDefaultArrives()
    {
        var old = new FakeEndpoint();
        var source = new FakeSource { Current = old };
        using var service = new VolumeService(source);
        source.Current = null;
        source.NotifyChange();

        Assert.False(service.IsAvailable);
        Assert.False(service.SetVolume(0.3f));
        Assert.Equal(1, old.DisposeCount);

        var replacement = new FakeEndpoint { Volume = 0.7f };
        source.Current = replacement;
        source.NotifyChange();
        Assert.True(service.IsAvailable);
        Assert.Equal(0.7f, service.GetVolume());
    }

    [Fact]
    public void DeviceChangeDuringResolutionIsNotLost()
    {
        var first = new FakeEndpoint { Volume = 0.1f };
        var next = new FakeEndpoint { Volume = 0.9f };
        var source = new FakeSource { Current = first };
        source.OnOpen = () =>
        {
            source.Current = next;
            source.NotifyChange();
            source.OnOpen = null;
        };
        using var service = new VolumeService(source);

        Assert.Equal(0.9f, service.GetVolume());
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(2, source.OpenCount);
    }

    [Fact]
    public void DisposeUnsubscribesNotificationsAndCannotReopenEndpoint()
    {
        var endpoint = new FakeEndpoint();
        var source = new FakeSource { Current = endpoint };
        var service = new VolumeService(source);
        service.Dispose();
        service.Dispose();
        source.NotifyChange();

        Assert.Equal(1, endpoint.DisposeCount);
        Assert.Equal(1, source.DisposeCount);
        Assert.Equal(0, source.SubscriberCount);
        Assert.False(service.RefreshDefaultDevice());
        Assert.False(service.IsAvailable);
        Assert.False(service.SetVolume(0.3f));
        Assert.Equal(1, source.OpenCount);
    }

    private sealed class FakeSource : IVolumeEndpointSource
    {
        public event Action? DefaultDeviceChanged;
        public IVolumeEndpoint? Current { get; set; }
        public Action? OnOpen { get; set; }
        public int OpenCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int SubscriberCount => DefaultDeviceChanged?.GetInvocationList().Length ?? 0;

        public IVolumeEndpoint OpenDefault()
        {
            OpenCount++;
            var current = Current ?? throw new COMException("No default endpoint");
            OnOpen?.Invoke();
            return current;
        }

        public void NotifyChange() => DefaultDeviceChanged?.Invoke();
        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeEndpoint : IVolumeEndpoint
    {
        public float Volume { get; set; }
        public bool IsMuted { get; set; }
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }
}
