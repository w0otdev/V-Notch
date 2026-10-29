using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class TranslationCacheTests
{
    [Fact]
    public async Task CachedTranslationDoesNotWaitForUnrelatedInference()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var engine = new Engine(async (text, _) =>
        {
            if (text == "slow") { entered.TrySetResult(); await release.Task; }
            return text;
        });
        using var service = new LocalTranslationService(engine);
        var original = await service.TranslateAsync("cached", "en", "vi", default);
        var slow = service.TranslateAsync("slow", "en", "vi", default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var cached = await service.TranslateAsync("cached", "en", "vi", default)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(original, cached);
            Assert.False(slow.IsCompleted);
            Assert.Equal(2, engine.Calls);
        }
        finally { release.TrySetResult(); await slow; }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task RuntimeTuningReusesResultsAndReachesTheNextInference(int setting)
    {
        using var engine = new Engine((text, _) => Task.FromResult(text));
        using var service = new LocalTranslationService(engine);
        var options = new TranslationOptions();
        service.Configure(options);
        await service.TranslateAsync("cached", "en", "vi", default);
        var changed = setting switch
        {
            0 => options with { Threads = 2 },
            1 => options with { BatchThreads = 2 },
            2 => options with { GpuLayers = 0 },
            3 => options with { BatchSize = 128 },
            4 => options with { TimeoutSeconds = 120 },
            _ => options with { ModelIdleMinutes = 10 }
        };
        service.Configure(changed);
        await service.TranslateAsync("cached", "en", "vi", default);
        Assert.Equal(1, engine.Calls);
        await service.TranslateAsync("fresh", "en", "vi", default);
        Assert.Equal(changed, engine.Options);
        Assert.Equal(2, engine.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task OutputAndRetentionChangesInvalidateResults(int setting)
    {
        using var engine = new Engine((text, _) => Task.FromResult(text));
        using var service = new LocalTranslationService(engine);
        var options = new TranslationOptions();
        service.Configure(options);
        await service.TranslateAsync("cached", "en", "vi", default);
        service.Configure(setting switch
        {
            0 => options with { TemperaturePercent = 0 },
            1 => options with { TopPPercent = 50 },
            2 => options with { TopK = 5 },
            3 => options with { OutputTokens = 128 },
            4 => options with { ContextSize = 4096 },
            5 => options with { EnableHumanizer = false },
            6 => options with { CacheEntries = 1 },
            _ => options with { CacheMinutes = 1 }
        });
        await service.TranslateAsync("cached", "en", "vi", default);
        Assert.Equal(2, engine.Calls);
    }

    [Theory]
    [InlineData("en", "vi", "Ship on Friday.", "Gửi hàng thứ Năm.", "Gửi hàng thứ Sáu.")]
    [InlineData("zh", "en", "周五发货。", "Ship on Thursday.", "Ship on Friday.")]
    [InlineData("ja", "vi", "金曜日に発送します。", "Gửi hàng thứ Năm.", "Gửi hàng thứ Sáu.")]
    public async Task ChangedWeekdaysAreReviewableButNeverCached(string source, string target, string text, string wrong, string correct)
    {
        int attempts = 0;
        using var engine = new Engine((_, _) => Task.FromResult(++attempts == 1 ? wrong : correct));
        using var service = new LocalTranslationService(engine);
        var first = await service.TranslateAsync(text, source, target, default);
        Assert.False(first.WeekdaysPreserved);
        var second = await service.TranslateAsync(text, source, target, default);
        Assert.Equal(correct, second.Text);
        Assert.True(second.WeekdaysPreserved);
        await service.TranslateAsync(text, source, target, default);
        Assert.Equal(2, engine.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationChangeCannotExposeAnOldCachedResultDuringInference(bool disableCache)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var engine = new Engine(async (text, _) =>
        {
            if (text == "slow") { entered.TrySetResult(); await release.Task; }
            return text;
        });
        using var service = new LocalTranslationService(engine);
        await service.TranslateAsync("cached", "en", "vi", default);
        var slow = service.TranslateAsync("slow", "en", "vi", default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        service.Configure(disableCache ? new() { CacheEntries = 0 } : new() { EnableHumanizer = false });
        var changed = service.TranslateAsync("cached", "en", "vi", default);
        try { Assert.False(changed.IsCompleted); }
        finally { release.TrySetResult(); await slow; }
        await changed.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(3, engine.Calls);
    }

    [Fact]
    public async Task CacheLookupValidatesInputAndPreservesTruncationWithoutModelWork()
    {
        using var engine = new Engine((text, _) => Task.FromResult(text));
        using var service = new LocalTranslationService(engine);
        Assert.Null(service.TryGetCachedResult("missing", "en", "vi", default));
        Assert.Equal(0, engine.Calls);
        await service.TranslateAsync("cached", "en", "vi", default);
        var result = service.TryGetCachedResult("ca\0ched", "en", "vi", default);
        Assert.NotNull(result);
        Assert.Equal("cached", result.Text);
        Assert.True(result.InputTruncated);
        Assert.Equal("translation.languageUnsupported", Assert.Throws<TranslationException>(() =>
            service.TryGetCachedResult("cached", "en", "unsupported", default)).MessageKey);
        Assert.Equal("translation.currencyAmbiguous", Assert.Throws<TranslationException>(() =>
            service.TryGetCachedResult("EUR 80 USD", "en", "vi", default)).MessageKey);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        Assert.ThrowsAny<OperationCanceledException>(() => service.TryGetCachedResult("cached", "en", "vi", cancellation.Token));
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task ConcurrentCacheHitsKeepTheLruConsistentWhileInferenceIsBusy()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var engine = new Engine(async (text, _) =>
        {
            if (text == "slow") { entered.TrySetResult(); await release.Task; }
            return text;
        });
        using var service = new LocalTranslationService(engine);
        service.Configure(new() { CacheEntries = 4 });
        string[] texts = ["one", "two", "three", "four"];
        foreach (string text in texts) await service.TranslateAsync(text, "en", "vi", default);
        var slow = service.TranslateAsync("slow", "en", "vi", default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var hits = Enumerable.Range(0, 32).Select(i => Task.Run(async () =>
            {
                string text = texts[i % texts.Length];
                Assert.Equal(text, (await service.TranslateAsync(text, "en", "vi", default)).Text);
            }));
            await Task.WhenAll(hits).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(slow.IsCompleted);
            Assert.Equal(5, engine.Calls);
        }
        finally { release.TrySetResult(); await slow; }
        await service.TranslateAsync("slow", "en", "vi", default);
        Assert.Equal(5, engine.Calls);
    }

    [Fact]
    public async Task CacheHitStillHonorsCancellationAndDisposal()
    {
        using var engine = new Engine((text, _) => Task.FromResult(text));
        var service = new LocalTranslationService(engine);
        try
        {
            await service.TranslateAsync("cached", "en", "vi", default);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.TranslateAsync("cached", "en", "vi", cancellation.Token));
            service.Dispose();
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                service.TranslateAsync("cached", "en", "vi", default));
            Assert.Equal(1, engine.Calls);
        }
        finally { service.Dispose(); }
    }

    private sealed class Engine(Func<string, CancellationToken, Task<string>> translate) : ILocalTranslationEngine
    {
        internal int Calls;
        internal TranslationOptions? Options;
        public void Configure(TranslationOptions options) => Options = options;
        public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return translate(text, cancellationToken); }
        public void ReleaseIdleResources() { }
        public void Dispose() { }
    }
}
