using System.Collections.Concurrent;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

public sealed class ServicePrewarmerTests
{
    [Fact]
    public void MissingContainerIsRejectedBeforeSchedulingBackgroundWork() => Assert.Throws<ArgumentNullException>(() => { _ = ServicePrewarmer.Prewarm(null!); });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingAndFailedServicesDoNotPreventRemainingWarmups(bool throws)
    {
        var provider = new RecordingProvider(throws);
        var warmup = ServicePrewarmer.Prewarm(provider);
        Assert.Empty(provider.Requests);
        await warmup;
        Assert.DoesNotContain(typeof(PrivacyIndicatorService), provider.Requests);
        Assert.Contains(typeof(IBatteryService), provider.Requests);
        Assert.Contains(typeof(IWindowTitleScanner), provider.Requests);
        Assert.DoesNotContain(typeof(AudioMixerService), provider.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SettingsControlSmartCropAndSpotlightWarmupWithoutChangingUserSettings(bool enabled)
    {
        var artwork = new FakeMediaArtworkService();
        var settings = new FakeSettingsService(new() { EnableSmartCrop = enabled, EnableSpotlight = enabled, AutoCheckUpdates = false });
        var provider = new RecordingProvider(false, new Dictionary<Type, object>
        {
            [typeof(ISettingsService)] = settings,
            [typeof(IMediaArtworkService)] = artwork,
            [typeof(IBatteryService)] = new FakeBatteryService(),
            [typeof(IVolumeService)] = new FakeVolumeService()
        });
        await ServicePrewarmer.Prewarm(provider);
        Assert.Equal(enabled, artwork.SmartCropEnabled);
        Assert.Null(settings.LastSaved);
        Assert.Equal(enabled ? 1 : 0, provider.Requests.Count(type => type == typeof(VNotch.Services.Spotlight.SpotlightSearchService)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivacyWorkerRemainsOwnedByItsModuleDuringBackgroundWarmup(bool enabled)
    {
        int scans = 0;
        using var privacy = new PrivacyIndicatorService(TimeSpan.FromMilliseconds(20),
            _ => Array.Empty<CapabilityUsage>(), () => false,
            new ScreenRecordingProbe(() => { Interlocked.Increment(ref scans); return false; }));
        var provider = new RecordingProvider(false, new Dictionary<Type, object>
        {
            [typeof(ISettingsService)] = new FakeSettingsService(new() { EnablePrivacyIndicators = enabled, EnableSpotlight = false }),
            [typeof(PrivacyIndicatorService)] = privacy
        });
        await ServicePrewarmer.Prewarm(provider);
        Assert.Equal(0, Volatile.Read(ref scans));
        Assert.DoesNotContain(typeof(PrivacyIndicatorService), provider.Requests);
    }

    private sealed class RecordingProvider(bool throws, Dictionary<Type, object>? registrations = null) : IServiceProvider
    {
        internal ConcurrentQueue<Type> Requests { get; } = new();
        public object? GetService(Type serviceType)
        {
            Requests.Enqueue(serviceType);
            if (throws) throw new InvalidOperationException("Fixture resolution failure");
            return registrations?.GetValueOrDefault(serviceType);
        }
    }
}
