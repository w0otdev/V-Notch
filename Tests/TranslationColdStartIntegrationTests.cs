using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LLama.Native;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services.Translation;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class TranslationColdStartIntegrationTests(ITestOutputHelper output)
{
    // Run alone in a fresh host, with no other process holding the model on the GPU.
    [TranslationGpuFact]
    public void FirstPopupTranslationPreparesModelOnDemand() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var store = new TranslationModelStore(profile: TranslationModelCatalog.Find("translategemma-4b"));
        Assert.True(store.IsInstalled);
        var engine = new QwenTranslationEngine(store);
        var controller = new LiveTranslationController(Dispatcher.CurrentDispatcher, () => { }, store, engine);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        try
        {
            var startup = Stopwatch.StartNew();
            var settings = new NotchSettings
            {
                EnableLiveTranslation = true, AutoLiveTranslation = false,
                TranslationModelId = store.Profile.Id, TranslationSourceLanguage = "auto", TranslationTargetLanguage = "vi",
                TranslationModelIdleMinutes = 0, TranslationThreads = 4, TranslationBatchThreads = 4,
                TranslationBatchSize = 256, TranslationGpuLayers = 36, EnableCurrencyConversion = false
            };
            controller.ApplySettings(settings);
            output.WriteLine($"ApplySettings: {startup.Elapsed.TotalMilliseconds:F1} ms");
            var preparation = (Task?)typeof(LiveTranslationController).GetField("_preparation", flags)!.GetValue(controller);
            Assert.Null(preparation);
            Assert.Null(typeof(QwenTranslationEngine).GetField("_weights", flags)!.GetValue(engine));
            output.WriteLine($"Enabled without resident model: {startup.Elapsed.TotalMilliseconds:F1} ms");

            var window = (TranslationWindow)typeof(LiveTranslationController).GetMethod("EnsureWindow", flags)!.Invoke(controller, null)!;
            BackgroundTestWindows.ProtectInput(window);
            window.Opacity = 0;
            window.ShowActivated = false;
            foreach (string text in new[] { "Improved AI Subscription managing", "Please save your changes before closing the window." })
            {
                typeof(LiveTranslationController).GetField("_text", flags)!.SetValue(controller, text);
                window.ShowClipboard(text);
                var timer = Stopwatch.StartNew();
                _ = typeof(LiveTranslationController).GetMethod("TranslateAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(controller, null);
                await WpfFrameWaiter.UntilAsync(() => window.ResultText.Text.Length > 0, "first visible translation", ct);
                double firstPreviewMs = timer.Elapsed.TotalMilliseconds;
                await WpfFrameWaiter.UntilAsync(() => window.CopyButton.IsEnabled, "complete first popup translation", ct);
                output.WriteLine($"Popup: {timer.Elapsed.TotalMilliseconds:F1} ms; first text: {firstPreviewMs:F1} ms | {window.OutputText}");
                Assert.NotEmpty(window.OutputText);
                Assert.NotEqual(text, window.OutputText);
            }
            Assert.NotNull(typeof(LiveTranslationController).GetField("_preparation", flags)!.GetValue(controller));
            Assert.True(NativeApi.llama_supports_gpu_offload());
            Assert.False(engine.IsCpuFallback);
            Assert.True((bool)typeof(QwenTranslationEngine).GetField("_preparedForInference", flags)!.GetValue(engine)!);
            settings.EnableLiveTranslation = false;
            controller.ApplySettings(settings);
            await WpfFrameWaiter.UntilAsync(() => typeof(QwenTranslationEngine).GetField("_weights", flags)!.GetValue(engine) == null,
                "disabled translation releases resident weights", ct);
            Assert.False((bool)typeof(QwenTranslationEngine).GetField("_preparedForInference", flags)!.GetValue(engine)!);
        }
        finally
        {
            controller.Dispose();
            await engine.Disposal;
        }
    }, timeoutSeconds: 180);
}
