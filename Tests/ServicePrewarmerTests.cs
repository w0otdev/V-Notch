using System.Collections.Concurrent;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

public sealed class ServicePrewarmerTests
{
    [Fact]
    public void MissingContainerIsRejectedBeforeSchedulingBackgroundWork() => Assert.Throws<ArgumentNullException>(() => ServicePrewarmer.Prewarm(null!));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingAndFailedServicesDoNotPreventRemainingWarmups(bool throws)
    {
        var provider = new RecordingProvider(throws);
        ServicePrewarmer.Prewarm(provider);
        Assert.True(provider.Requests.Count >= 20);
        await WaitForBackgroundWarmups(provider);
        Assert.True(provider.Requests.Count(type => type == typeof(PrivacyIndicatorService)) >= 2);
        Assert.Contains(typeof(IBatteryService), provider.Requests);
        Assert.Contains(typeof(IWindowTitleScanner), provider.Requests);
        Assert.Contains(typeof(AudioMixerService), provider.Requests);
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
        ServicePrewarmer.Prewarm(provider);
        await WaitForBackgroundWarmups(provider);
        Assert.Equal(enabled, artwork.SmartCropEnabled);
        Assert.Null(settings.LastSaved);
        Assert.Equal(enabled ? 2 : 1, provider.Requests.Count(type => type == typeof(VNotch.Services.Spotlight.SpotlightSearchService)));
    }

    private static async Task WaitForBackgroundWarmups(RecordingProvider provider)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (provider.Requests.Count(type => type == typeof(PrivacyIndicatorService)) < 2) await Task.Delay(10, timeout.Token);
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
