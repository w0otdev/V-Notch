using VNotch.Models;

namespace VNotch.Services.Translation;

internal sealed record TranslationOptions
{
    internal int ModelIdleMinutes { get; init; } = 5;
    internal int CacheMinutes { get; init; } = 30;
    internal int CacheEntries { get; init; } = 128;
    internal int GpuLayers { get; init; } = 36;
    internal int Threads { get; init; } = 0;
    internal int BatchThreads { get; init; } = 0;
    internal int ContextSize { get; init; } = 2048;
    internal int BatchSize { get; init; } = 512;
    internal int OutputTokens { get; init; } = 768;
    internal int TemperaturePercent { get; init; } = 20;
    internal int TopPPercent { get; init; } = 80;
    internal int TopK { get; init; } = 20;
    internal int TimeoutSeconds { get; init; } = 90;
    internal static TranslationOptions From(NotchSettings settings) => new()
    {
        ModelIdleMinutes = settings.TranslationModelIdleMinutes,
        CacheMinutes = settings.TranslationCacheMinutes,
        CacheEntries = settings.TranslationCacheEntries,
        GpuLayers = settings.TranslationGpuLayers,
        Threads = settings.TranslationThreads,
        BatchThreads = settings.TranslationBatchThreads,
        ContextSize = settings.TranslationContextSize,
        BatchSize = settings.TranslationBatchSize,
        OutputTokens = settings.TranslationOutputTokens,
        TemperaturePercent = settings.TranslationTemperaturePercent,
        TopPPercent = settings.TranslationTopPPercent,
        TopK = settings.TranslationTopK,
        TimeoutSeconds = settings.TranslationTimeoutSeconds,
    };
}
