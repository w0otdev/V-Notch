using System.Diagnostics;
using System.Text.Json;
using VNotch.Services.Translation;

internal static class TranslationCacheBenchmark
{
    // Synthetic inference isolates pipeline latency from model/GPU speed.
    internal static async Task RunAsync()
    {
        const int busyMilliseconds = 500;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var engine = new Engine(async text =>
        {
            if (text == "busy") { entered.TrySetResult(); await release.Task; }
            return text;
        });
        using var service = new LocalTranslationService(engine);
        var options = new TranslationOptions();
        service.Configure(options);
        await service.TranslateAsync("cached", "en", "vi", default);
        var busy = service.TranslateAsync("busy", "en", "vi", default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var unblock = Task.Run(async () => { await Task.Delay(busyMilliseconds); release.TrySetResult(); });
        var timer = Stopwatch.StartNew();
        await service.TranslateAsync("cached", "en", "vi", default);
        double busyCacheMilliseconds = timer.Elapsed.TotalMilliseconds;
        bool returnedDuringInference = !release.Task.IsCompleted;
        await busy;
        await unblock;

        // Exercise the same cache across scripts and language pairs. The echo
        // engine does not evaluate linguistic quality and loads no model files.
        string[] sources = ["en", "vi", "zh", "ja", "ko", "th", "ar", "hi", "ru", "fr"];
        foreach (string source in sources)
            await service.TranslateAsync("sample", source, source == "en" ? "vi" : "en", default);
        int beforeHits = engine.Calls;
        var timings = new double[1000];
        for (int i = 0; i < timings.Length; i++)
        {
            string source = sources[i % sources.Length];
            timer.Restart();
            await service.TranslateAsync("sample", source, source == "en" ? "vi" : "en", default);
            timings[i] = timer.Elapsed.TotalMilliseconds;
        }
        int hitInferences = engine.Calls - beforeHits;

        int beforeTuning = engine.Calls;
        foreach (var changed in new[]
        {
            options with { Threads = 2 }, options with { BatchThreads = 2 },
            options with { GpuLayers = 0 }, options with { BatchSize = 128 },
            options with { TimeoutSeconds = 120 }, options with { ModelIdleMinutes = 10 }
        })
        {
            service.Configure(changed);
            await service.TranslateAsync("cached", "en", "vi", default);
        }
        Array.Sort(timings);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "synthetic-cache-pipeline", runtime = Environment.Version.ToString(),
            sources = sources.Length, hits = timings.Length, busyMilliseconds,
            busyCacheMilliseconds, returnedDuringInference,
            hitP50Milliseconds = timings[timings.Length / 2],
            hitP95Milliseconds = timings[(int)(timings.Length * 0.95)],
            hitInferences, runtimeTuningInferences = engine.Calls - beforeTuning
        }));
    }

    private sealed class Engine(Func<string, Task<string>> translate) : ILocalTranslationEngine
    {
        internal int Calls;
        public Task<string> TranslateAsync(string text, string source, string target, CancellationToken ct)
        { Calls++; return translate(text); }
        public void ReleaseIdleResources() { }
        public void Dispose() { }
    }
}
