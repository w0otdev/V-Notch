using System.Diagnostics;
using System.IO;
using System.Text.Json;
using VNotch.Services.Translation;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

public sealed class LiteTranslationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task MissingModelFailsBeforeLoadingRuntimeAndDisposalWaitsForPreviousEngine()
    {
        var store = new TranslationModelStore(root: Path.Combine(Path.GetTempPath(), "vnotch-lite-" + Guid.NewGuid()),
            profile: TranslationModelProfile.Small100);
        var previous = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new LiteTranslationEngine(store, previous.Task);
        var loading = engine.PrepareAsync(default);
        Assert.False(loading.IsCompleted);
        previous.SetResult();
        var error = await Assert.ThrowsAsync<TranslationException>(() => loading);
        Assert.Equal("translation.modelMissing", error.MessageKey);
        engine.Dispose();
        await engine.Disposal.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Opt-in real model test. Ordinary CI does not download weights or need an account.
    [LiteModelFact]
    public async Task RealModelImportsVerifiesTranslatesCachesAndReleases()
    {
        string model = Environment.GetEnvironmentVariable("VNOTCH_LITE_MODEL_PATH")!;
        string root = Path.Combine(Path.GetTempPath(), "vnotch-lite-" + Guid.NewGuid());
        var store = new TranslationModelStore(root: root, profile: TranslationModelProfile.Small100);
        var engine = new LiteTranslationEngine(store);
        var service = new LocalTranslationService(engine);
        try
        {
            using var vocabulary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(model, "shared_vocabulary.json")));
            var targets = vocabulary.RootElement.EnumerateArray().Select(x => x.GetString()!)
                .Where(x => x.StartsWith("__") && x.EndsWith("__") && x.Trim('_').All(char.IsAsciiLetterLower) && x.Trim('_').Length is 2 or 3)
                .Select(x => x.Trim('_')).Order().ToArray();
            Assert.Equal(100, targets.Length);
            Assert.Equal(targets, TranslationLanguages.Codes.Order().ToArray());
            await store.ImportAsync(model, default);
            Assert.True(store.IsInstalled);
            await service.PrepareAsync(default);
            engine.Configure(new TranslationOptions { ModelIdleMinutes = 0 });
            engine.ReleaseIdleResources(); // Zero keeps the already warmed model resident.
            foreach (var (text, source, target) in new[]
            {
                ("Where is the nearest train station?", "en", "vi"),
                ("明日の会議は何時に始まりますか？", "ja", "fr"),
                ("请告诉我如何到达机场。", "zh", "de"),
                ("أين يمكنني شراء تذكرة القطار؟", "ar", "en"),
                ("Le restaurant est ouvert tous les jours.", "fr", "es")
            })
            {
                var clock = Stopwatch.StartNew();
                var result = await service.TranslateAsync(text, source, target, default);
                output.WriteLine($"{source}-{target}: {clock.Elapsed.TotalMilliseconds:F1} ms | {result.Text}");
                Assert.NotEmpty(result.Text);
                Assert.NotEqual(text, result.Text);
                Assert.True(result.InstantPresentation);
                clock.Restart();
                var cached = await service.TranslateAsync(text, source, target, default);
                output.WriteLine($"cached: {clock.Elapsed.TotalMilliseconds:F3} ms");
                Assert.True(cached.IsCached);
                Assert.Equal(result.Text, cached.Text);
            }
            // Batches must preserve repeated lines and their separators, not omit duplicates.
            const string line = "Where is the nearest train station?";
            string oneLine = await engine.TranslateAsync(line, "en", "vi", default);
            string repeated = await engine.TranslateAsync($"  {line}\r\n\r\n{line}\t", "en", "vi", default);
            Assert.Equal($"  {oneLine}\r\n\r\n{oneLine}\t", repeated);
            // The existing Humanizer option must affect the lightweight runtime too.
            engine.Configure(new TranslationOptions { EnableHumanizer = true });
            string natural = await engine.TranslateAsync("Save your changes.", "en", "vi", default);
            Assert.Contains("thay đổi", natural);
            engine.Configure(new TranslationOptions { EnableHumanizer = false });
            string fast = await engine.TranslateAsync("Save your changes.", "en", "vi", default);
            Assert.NotEqual(natural, fast);
            engine.Configure(new TranslationOptions { EnableHumanizer = true });
            Assert.Equal(natural, await engine.TranslateAsync("Save your changes.", "en", "vi", default));
            // Cancelling a multi-chunk request must leave the worker and handle reusable.
            using (var interrupted = new CancellationTokenSource(TimeSpan.FromMilliseconds(40)))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.TranslateAsync(
                    string.Join("\r\n", Enumerable.Repeat("Please check the documents and the train schedule before leaving for the airport.", 12)),
                    "en", "vi", interrupted.Token));
            Assert.NotEmpty(await engine.TranslateAsync("Good evening.", "en", "fr", default));
            service.SetEnabled(false);
            // Disabling background residency must not break an explicit engine request.
            Assert.NotEmpty(await engine.TranslateAsync("Good morning.", "en", "fr", default));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.TranslateAsync("Hello", "en", "fr", cancellation.Token));
        }
        finally
        {
            service.Dispose();
            await service.Disposal;
            // Remove only the model copies this test installed, never the source weights.
            await store.RemoveAsync(default);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}

internal sealed class LiteModelFactAttribute : FactAttribute
{
    public LiteModelFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VNOTCH_LITE_MODEL_PATH")))
            Skip = "Set VNOTCH_LITE_MODEL_PATH to the pinned SMaLL-100 asset directory to run real inference.";
    }
}
