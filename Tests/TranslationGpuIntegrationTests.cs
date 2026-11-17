using System.Diagnostics;
using LLama.Native;
using VNotch.Services.Translation;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

// Run in a fresh test host: llama.cpp's backend cannot be replaced after initialization.
public sealed class TranslationGpuIntegrationTests(ITestOutputHelper output)
{
    [TranslationGpuFact]
    public async Task InstalledTranslateGemmaUsesVulkanAndTranslatesConsecutiveRequests()
    {
        var store = new TranslationModelStore(profile: TranslationModelCatalog.Find("translategemma-4b"));
        Assert.True(store.IsInstalled);
        Assert.True(await TranslationNativeRuntime.IsOnDemandVulkanValidAsync(default));
        var engine = new QwenTranslationEngine(store);
        var service = new LocalTranslationService(engine);
        service.Configure(new TranslationOptions { Threads = 4, BatchThreads = 4, BatchSize = 256, ModelIdleMinutes = 0 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            var timer = Stopwatch.StartNew();
            await service.PrepareAsync(timeout.Token);
            output.WriteLine($"Prepare: {timer.Elapsed.TotalMilliseconds:F1} ms; GPU backend: {NativeApi.llama_supports_gpu_offload()}");
            foreach (string text in new[] { "Where is the nearest train station?", "Please save your changes before closing the window.", "The meeting starts tomorrow morning." })
            {
                timer.Restart();
                double? firstPreview = null;
                var result = await service.TranslateAfterLanguageCheckAsync(text, "en", "vi", timeout.Token,
                    preview => { if (preview.Length > 0) firstPreview ??= timer.Elapsed.TotalMilliseconds; });
                output.WriteLine($"Translate: {timer.Elapsed.TotalMilliseconds:F1} ms; preview: {firstPreview:F1} ms | {result.Text}");
                Assert.NotEmpty(result.Text);
                Assert.NotEqual(text, result.Text);
                Assert.False(result.IsCached);
            }
            Assert.True(NativeApi.llama_supports_gpu_offload(), "Vulkan assets are valid, but the CPU-only backend was loaded.");
            Assert.False(engine.IsCpuFallback);
            using var process = Process.GetCurrentProcess();
            Assert.Contains(process.Modules.Cast<ProcessModule>(), module => module.ModuleName == "ggml-vulkan.dll");
        }
        finally
        {
            service.Dispose();
            await service.Disposal;
        }
    }
}

internal sealed class TranslationGpuFactAttribute : FactAttribute
{
    public TranslationGpuFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VNOTCH_TRANSLATION_GPU_TEST") != "1")
            Skip = "Set VNOTCH_TRANSLATION_GPU_TEST=1 with TranslateGemma 4B and the pinned Vulkan runtime installed on a GPU machine.";
    }
}
