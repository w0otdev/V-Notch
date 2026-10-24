using System.Runtime.InteropServices;
using VNotch.Models;

namespace VNotch.Services.Translation;

internal sealed partial record TranslationOptions
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnTranslationOptions
    {
        public int ModelIdleMinutes;
        public int CacheMinutes;
        public int CacheEntries;
        public int GpuLayers;
        public int Threads;
        public int BatchThreads;
        public int ContextSize;
        public int BatchSize;
        public int OutputTokens;
        public int TemperaturePercent;
        public int TopPPercent;
        public int TopK;
        public int TimeoutSeconds;
        public byte EnableHumanizer;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_transoptions_can_reuse_cached(
        in VnTranslationOptions current,
        in VnTranslationOptions previous,
        out byte outCanReuse);

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
    internal bool EnableHumanizer { get; init; } = true;

    private VnTranslationOptions ToNative() => new()
    {
        ModelIdleMinutes = ModelIdleMinutes,
        CacheMinutes = CacheMinutes,
        CacheEntries = CacheEntries,
        GpuLayers = GpuLayers,
        Threads = Threads,
        BatchThreads = BatchThreads,
        ContextSize = ContextSize,
        BatchSize = BatchSize,
        OutputTokens = OutputTokens,
        TemperaturePercent = TemperaturePercent,
        TopPPercent = TopPPercent,
        TopK = TopK,
        TimeoutSeconds = TimeoutSeconds,
        EnableHumanizer = EnableHumanizer ? (byte)1 : (byte)0
    };

    // Scheduling and hardware tuning do not change a completed translation.
    // All other settings (including newly added ones) invalidate results by default.
    internal bool CanReuseCachedResults(TranslationOptions previous)
    {
        var cur = ToNative();
        var prev = previous.ToNative();
        return vn_transoptions_can_reuse_cached(in cur, in prev, out byte canReuse) == 0 && canReuse != 0;
    }

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
        EnableHumanizer = settings.EnableTranslationHumanizer,
    };
}
