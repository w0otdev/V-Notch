using System.Diagnostics;
using System.Text;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class TranslationReviewRegressionTests
{
    [Theory]
    [InlineData("Weight 1.234 kg", "Khối lượng 1234 kg")]
    [InlineData("Dose 5 mg", "Liều 5 kg")]
    [InlineData("Distance 5 km", "Khoảng cách 5 m")]
    [InlineData("Heat 20 °C", "Nhiệt độ 20 °F")]
    [InlineData("First 10, then 20", "Đầu tiên 20, sau đó 10")]
    public async Task ChangedOrAmbiguousQuantitiesRequireReviewAndAreNotCached(string source, string output)
    {
        using var engine = new Engine((_, _) => Task.FromResult(output));
        using var service = new LocalTranslationService(engine);
        for (int i = 0; i < 2; i++)
        {
            var result = await service.TranslateAsync(source, "en", "vi", default);
            Assert.False(result.LiteralsPreserved);
            Assert.Null(service.TryGetCachedResult(source, "en", "vi", default));
        }
        Assert.Equal(2, engine.Calls);
    }

    [Theory]
    [InlineData("Are you watching the match")]
    [InlineData("We are watching the match.")]
    [InlineData("They are watching the match.")]
    public async Task ExplicitSubjectOrSentenceTypeChangesAreRejected(string output)
    {
        using var engine = new Engine((_, _) => Task.FromResult(output));
        using var service = new LocalTranslationService(engine);
        for (int i = 0; i < 2; i++)
        {
            var error = await Assert.ThrowsAsync<TranslationException>(() =>
                service.TranslateAsync("Các bạn đang theo dõi trận đấu.", "vi", "en", default));
            Assert.Equal("translation.meaningChanged", error.MessageKey);
        }
        Assert.Equal(2, engine.Calls);
    }

    [Fact]
    public async Task CancelledWorkReleasesTheServiceGateForTheNextRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var engine = new Engine(async (text, ct) =>
        {
            if (text == "first") { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); }
            return text;
        });
        using var service = new LocalTranslationService(engine);
        using var cancelled = new CancellationTokenSource();
        var first = service.TranslateAsync("first", "en", "vi", cancelled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var next = service.TranslateAsync("second", "en", "vi", default);
        Assert.False(next.IsCompleted);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal("second", (await next.WaitAsync(TimeSpan.FromSeconds(2))).Text);
        Assert.Null(service.TryGetCachedResult("first", "en", "vi", default));
    }

    [Fact]
    public void GpuRecoveryUsesCooldownAndRestartsItAfterAnotherFailure()
    {
        var clock = new Clock();
        var recovery = new TranslationBackendRecovery(clock);
        Assert.False(recovery.RetryReady);
        recovery.Failed();
        clock.Advance(TranslationBackendRecovery.Cooldown - TimeSpan.FromSeconds(1));
        Assert.False(recovery.RetryReady);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(recovery.RetryReady);
        recovery.Failed();
        Assert.False(recovery.RetryReady);
        clock.Advance(TranslationBackendRecovery.Cooldown);
        Assert.True(recovery.RetryReady);
        recovery.Reset();
        Assert.False(recovery.HasFailure);
        Assert.False(recovery.RetryReady);
    }

    [Theory]
    [InlineData("{\"translation\":\"Hello", "Hello")]
    [InlineData("{\"translation\":\"A\\nB", "A\nB")]
    [InlineData("{\"translation\":\"A\\", "A")]
    [InlineData("{\"translation\":\"A\\u00", "A")]
    [InlineData("{\"translation\":\"A\\u00e9", "Aé")]
    [InlineData("{\"translation\":\"A\\uD83D", "A")]
    [InlineData("{\"translation\":\"A\\uD83D\\uDE00", "A😀")]
    [InlineData("{\"translation\":\"A\\\"B\"}", "A\"B")]
    [InlineData("{\"explanation\":\"Ignore", "")]
    public void JsonPreviewsExposeOnlyCompleteStringCharacters(string raw, string expected) =>
        Assert.Equal(expected, TranslationPreview.ReadJsonStringPrefix(raw));

    [Fact]
    public void PreviewRestoresKnownLiteralsAndHidesUnfinishedMarkers()
    {
        string? result = null;
        var preview = new TranslationPreview(value => result = value);
        preview.Report(new StringBuilder("Visit __VNT_LITERAL0__ then __VNT_LIT"),
            literals: new Dictionary<string, string> { ["__VNT_LITERAL0__"] = "example.com" });
        Assert.Equal("Visit example.com then ", result);
    }

    [Fact]
    public async Task LanguageAnalysisRetainsStrictPassthroughForMixedText()
    {
        const string text = "Đây là một đoạn văn tiếng Việt đủ dài để kiểm tra việc nhận diện ngôn ngữ và giữ nguyên văn bản.\nPlease send the invoice tomorrow afternoon.";
        var analysis = await TranslationLanguages.AnalyzeAsync(text, default);
        Assert.Equal(text, analysis.Text);
        Assert.Null(analysis.PassthroughLanguage);
        Assert.Null(LocalTranslationService.TrySameLanguage(text, "auto", "vi", analysis));
        Assert.Null(LocalTranslationService.TrySameLanguage(text, "auto", "en", analysis));
    }

    [Fact]
    public async Task ExactRequestLanguageAnalysisReachesInferenceUnchanged()
    {
        const string text = "This is a long English sentence used to check that the request carries its language analysis into inference.";
        var language = await TranslationLanguages.AnalyzeAsync(text, default);
        using var engine = new AnalysisEngine();
        using var service = new LocalTranslationService(engine);
        await service.TranslateAfterLanguageCheckAsync(text, "auto", "vi", default, language: language);
        Assert.Same(language, engine.Analysis);
        Assert.Equal("en", language.DetectedLanguage);
        Assert.NotNull(language.Confidence);
    }

    private sealed class AnalysisEngine : ILocalTranslationEngine
    {
        internal TranslationLanguageAnalysis? Analysis;
        public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<string> TranslateWithPreviewAsync(string text, string sourceLanguage, string targetLanguage, Action<string>? preview, CancellationToken cancellationToken, TranslationLanguageAnalysis? language = null)
        { Analysis = language; return Task.FromResult("Đây là bản dịch."); }
        public void ReleaseIdleResources() { }
        public void Dispose() { }
    }

    private sealed class Clock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }

    private sealed class Engine(Func<string, CancellationToken, Task<string>> run) : ILocalTranslationEngine
    {
        internal int Calls;
        public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
        { Calls++; return run(text, cancellationToken); }
        public void ReleaseIdleResources() { }
        public void Dispose() { }
    }
}
