using System.Text.Json;
using VNotch.Models;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class TranslationAdvancedTests
{
    [Fact]
    public void SettingsRoundTripAndCloneKeepClampedValues()
    {
        var settings = new NotchSettings
        {
            TranslationGpuLayers = -5,
            TranslationThreads = 999,
            TranslationContextSize = 1,
            TranslationModelIdleMinutes = 0,
            TranslationCacheEntries = 0,
            TranslationTemperaturePercent = 42,
            TranslationTimeoutSeconds = 300
        };
        var restored = JsonSerializer.Deserialize<NotchSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(TranslationOptions.From(settings), TranslationOptions.From(restored.Clone()));
        Assert.Equal(0, restored.TranslationGpuLayers);
        Assert.Equal(64, restored.TranslationThreads);
        Assert.Equal(2048, restored.TranslationContextSize);
        Assert.Equal(0, restored.TranslationCacheEntries);
    }

    [Fact]
    public async Task CacheEvictsLeastRecentlyUsedAndConfigurationInvalidatesResults()
    {
        var engine = new CountingEngine();
        using var service = new LocalTranslationService(engine);
        var options = new TranslationOptions { CacheEntries = 2 };
        service.Configure(options);
        await service.TranslateAsync("a", "en", "vi", default);
        await service.TranslateAsync("b", "en", "vi", default);
        await service.TranslateAsync("a", "en", "vi", default);
        await service.TranslateAsync("c", "en", "vi", default);
        await service.TranslateAsync("a", "en", "vi", default);
        Assert.Equal(3, engine.Calls);
        await service.TranslateAsync("b", "en", "vi", default);
        Assert.Equal(4, engine.Calls);
        service.Configure(options with { TemperaturePercent = 50 });
        await service.TranslateAsync("b", "en", "vi", default);
        Assert.Equal(5, engine.Calls);
        Assert.Equal(50, engine.Options!.TemperaturePercent);
        service.Configure(options with { CacheEntries = 0 });
        await service.TranslateAsync("b", "en", "vi", default);
        await service.TranslateAsync("b", "en", "vi", default);
        Assert.Equal(7, engine.Calls);
    }

    private sealed class CountingEngine : ILocalTranslationEngine
    {
        internal int Calls;
        internal TranslationOptions? Options;
        public void Configure(TranslationOptions options) => Options = options;
        public Task<string> TranslateAsync(string text, string source, string target, CancellationToken ct)
        { Calls++; return Task.FromResult(text); }
        public void ReleaseIdleResources() { }
        public void Dispose() { }
    }
}
