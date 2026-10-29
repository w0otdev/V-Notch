using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class TranslationSameLanguageTests
{
    private const string ScreenshotText = "Mng cho em hỏi cấu hình như này có thể build AI local về máy được không ạ? Em tính vọc vạch 1 tí vì cũng hơi tò mò về mảng này.";

    [Fact]
    public async Task ScreenshotSelectionKeepsEveryWordAndNumberWithoutLoadingTheModel()
    {
        using var engine = new Engine();
        using var service = new LocalTranslationService(engine);
        var result = await service.TranslateAsync(ScreenshotText, "auto", "vi", default);
        Assert.Equal(ScreenshotText, result.Text);
        Assert.Equal("vi", result.SourceLanguage);
        Assert.True(result.LiteralsPreserved);
        Assert.False(result.InputTruncated);
        Assert.Equal(0, engine.Preparations + engine.Fits + engine.Calls);
    }

    [Theory]
    [InlineData("Hello", "en")]
    [InlineData("This branch", "en")]
    [InlineData("Champions", "en")]
    [InlineData("商品発送", "zh")]
    public async Task ShortAutoSelectionsStillReachTheModel(string text, string target)
    {
        using var engine = new Engine();
        using var service = new LocalTranslationService(engine);
        await service.TranslateAsync(text, "auto", target, default);
        Assert.Equal(1, engine.Calls);
    }

    [Theory]
    [InlineData("vi")]
    [InlineData("en")]
    public async Task MixedLanguageSentencesStillReachTheModel(string target)
    {
        using var engine = new Engine();
        using var service = new LocalTranslationService(engine);
        await service.TranslateAsync(ScreenshotText + "\nPlease send invoice 42 tomorrow afternoon.", "auto", target, default);
        Assert.True(engine.Calls > 0);
    }

    [Fact]
    public async Task ChangingTargetAfterPassthroughStillRunsInferenceAndFlagsUnverifiedNumbers()
    {
        using var engine = new Engine { Output = "Can my computer build a local AI? I'm curious about this field." };
        using var service = new LocalTranslationService(engine);
        const string text = ScreenshotText + " Máy có 16 GB RAM.";
        Assert.Equal(text, (await service.TranslateAsync(text, "auto", "vi", default)).Text);
        var result = await service.TranslateAsync(text, "auto", "en", default);
        Assert.Equal(engine.Output, result.Text);
        Assert.False(result.LiteralsPreserved);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task ScreenshotSelectionCanTranslateALittleNaturallyIntoEnglish()
    {
        const string output = "Hi, could you tell me if I can build an AI locally on my machine using this configuration? I'm just curious about this field and want to experiment a little.";
        using var engine = new Engine { Output = output };
        using var service = new LocalTranslationService(engine);
        var result = await service.TranslateAsync(ScreenshotText, "auto", "en", default);
        Assert.Equal(output, result.Text);
        Assert.True(result.LiteralsPreserved);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task PassthroughHonorsCancellation()
    {
        using var engine = new Engine();
        using var service = new LocalTranslationService(engine);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TranslateAsync(ScreenshotText, "auto", "vi", cancellation.Token));
    }

    [Fact]
    public async Task LongSameLanguageSelectionRetainsTruncationBadgeWithoutTokenizing()
    {
        using var engine = new Engine();
        using var service = new LocalTranslationService(engine);
        string text = string.Join(' ', Enumerable.Repeat(ScreenshotText, 30));
        var result = await service.TranslateAsync(text, "auto", "vi", default);
        Assert.Equal(TranslationInput.Prepare(text), result.Text);
        Assert.True(result.InputTruncated);
        Assert.Equal(0, engine.Fits + engine.Calls);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("vi")]
    public void PopupShowsSameLanguageTextEvenWithoutAnInstalledModel(string source) => SharedStaTestRunner.RunAsync(async ct =>
    {
        string missingRoot = Path.Combine(Path.GetTempPath(), "VNotch-same-language-" + Guid.NewGuid());
        var store = new TranslationModelStore(missingRoot);
        using var engine = new Engine();
        using var controller = new LiveTranslationController(Dispatcher.CurrentDispatcher, () => { }, store, engine);
        controller.ApplySettings(new NotchSettings { TranslationSourceLanguage = source, TranslationTargetLanguage = "vi", EnableCurrencyConversion = false });
        var window = (TranslationWindow)typeof(LiveTranslationController).GetMethod("EnsureWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(controller, null)!;
        BackgroundTestWindows.ProtectInput(window);
        window.Opacity = 0;
        window.ShowActivated = false;
        typeof(LiveTranslationController).GetField("_text", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(controller, ScreenshotText);
        window.ShowClipboard(ScreenshotText);
        window.TranslateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WpfFrameWaiter.UntilAsync(() => window.CopyButton.IsEnabled, "same-language result without a model", ct);
        Assert.Equal(ScreenshotText, window.OutputText);
        Assert.Equal(0, engine.Preparations + engine.Fits + engine.Calls);
        Assert.Empty(window.StatusText.Text);
    });

    private sealed class Engine : ILocalTranslationEngine
    {
        internal int Preparations, Fits, Calls;
        internal string? Output;
        public Task PrepareAsync(CancellationToken cancellationToken) { Preparations++; return Task.CompletedTask; }
        public Task<string> FitInputAsync(string text, CancellationToken cancellationToken) { Fits++; return Task.FromResult(text); }
        public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken) { Calls++; return Task.FromResult(Output ?? text); }
        public void ReleaseIdleResources() { }
        public void Dispose() { }
    }
}
