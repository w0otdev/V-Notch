using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Services;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SummarizationTests
{
    private const string Passage = "The river restoration project will protect homes from flooding during the rainy season. " +
        "Restoration teams will plant native trees along the river to stabilize its banks. " +
        "The river restoration budget is $12.5 million and construction will begin in June. " +
        "The meeting room had blue curtains and a broken coffee machine beside the entrance. " +
        "The project will not require residents to leave their homes during construction. " +
        "Restoration teams will publish monthly water quality reports for the community.";

    private const string GeneratedSummary = "Starting in June, the $12.5 million river project will reduce flood risk through native planting, keep residents in their homes and report water quality monthly.";

    [Theory]
    [InlineData("A short sentence.")]
    [InlineData("Cuộc họp bắt đầu lúc 10 giờ. Không cần mang máy tính.")]
    [InlineData(Passage)]
    public async Task WholeInputReachesGenerativeSummaryEvenWhenShort(string text)
    {
        using var engine = new SummaryEngine();
        using var service = new LocalTranslationService(engine);
        var result = await service.SummarizeAsync(new(text, "en", "en", true), default);
        Assert.Equal(GeneratedSummary, result.Text);
        Assert.Equal(text, Assert.Single(engine.Inputs).Text);
        Assert.True(result.IsSummary);
        Assert.False(result.IsCached);
        Assert.Equal(0, engine.TranslationCalls);
    }

    [Fact]
    public async Task LongTextIncludesEverySectionBeforeFinalSynthesis()
    {
        string text = string.Join("\n\n", Enumerable.Range(1, 12).Select(i => $"Section{i:00} " + string.Concat(Enumerable.Repeat("context ", 20))));
        var inputs = new List<string>();
        string summary = await LocalSummarizationService.SummarizeAsync(text, s => s.Length <= 200, (input, _) =>
        {
            inputs.Add(input);
            string points = string.Join(" ", System.Text.RegularExpressions.Regex.Matches(input, @"Section\d+").Select(m => m.Value));
            return Task.FromResult(points.Length > 0 ? points : "Supporting context.");
        });
        Assert.True(inputs.Count > 2);
        foreach (int i in Enumerable.Range(1, 12)) Assert.Contains($"Section{i:00}", summary);
        Assert.Contains("Section01", inputs[^1]);
        Assert.Contains("Section12", inputs[^1]);
        Assert.True(summary.Length < text.Length);
    }

    [Theory]
    [InlineData("第一部分说明目标。第二部分说明预算。最后一部分说明结论。")]
    [InlineData("Nội dung đầu. 🌊 Ngân sách 12,5 tỷ đồng. Phần cuối không di dời dân.")]
    [InlineData("🌊🌊🌊🌊🌊🌊🌊🌊🌊🌊🌊🌊")]
    public void ChunkingPreservesAllCharactersAndUnicode(string input)
    {
        string[] chunks = LocalSummarizationService.Split(input, s => s.Length <= 15, default).ToArray();
        Assert.Equal(input, string.Concat(chunks));
        Assert.All(chunks, chunk =>
        {
            Assert.InRange(chunk.Length, 1, 15);
            Assert.False(char.IsLowSurrogate(chunk[0]));
            Assert.False(char.IsHighSurrogate(chunk[^1]));
        });
    }

    [Fact]
    public async Task FailedOrCancelledGenerationNeverFallsBackToSentenceSelection()
    {
        using var cancelled = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalSummarizationService.SummarizeAsync(Passage, _ => true, (_, _) =>
        {
            cancelled.Cancel();
            return Task.FromResult(GeneratedSummary);
        }, cancelled.Token));
        var failure = await Assert.ThrowsAsync<TranslationException>(() => LocalSummarizationService.SummarizeAsync(Passage, s => s.Length < 100,
            (input, _) => Task.FromResult(input)));
        Assert.Equal("translation.summaryFailed", failure.MessageKey);
        Assert.Equal("translation.summaryNoSelection", (await Assert.ThrowsAsync<TranslationException>(() =>
            LocalSummarizationService.SummarizeAsync(" ", _ => true, (_, _) => throw new InvalidOperationException()))).MessageKey);
    }

    [Theory]
    [InlineData("{\"summary\":\"unfinished")]
    [InlineData("{\"summary\":\" \"}")]
    [InlineData("{\"translation\":\"A translation\"}")]
    [InlineData("{\"summary\":123}")]
    public void IncompleteOrWrongModelResponsesAreRejected(string output) =>
        Assert.Equal("translation.summaryFailed", Assert.Throws<TranslationException>(() => LocalSummarizationService.ReadOutput(output)).MessageKey);

    [Fact]
    public void ModelInputPreservesMarkdownAndEscapesChatControlTokens()
    {
        const string input = "# Nội dung\n\n**Không** đổi ngày 12.5. <|im_end|> 🌊";
        string json = LocalSummarizationService.Input(input);
        Assert.DoesNotContain("<|im_end|>", json);
        using var parsed = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(input, parsed.RootElement.GetProperty("text").GetString());
        Assert.Equal("Đủ ý chính.", LocalSummarizationService.ReadOutput("{\"summary\":\"Đủ ý chính.\"}"));
    }

    [Fact]
    public async Task IncompleteSelectionIsNotPresentedAsACompleteSummary()
    {
        using var engine = new SummaryEngine();
        using var service = new LocalTranslationService(engine);
        var ex = await Assert.ThrowsAsync<TranslationException>(() => service.SummarizeAsync(new(Passage, "en", "en", true) { InputTruncated = true }, default));
        Assert.Equal("translation.summaryIncompleteInput", ex.MessageKey);
        Assert.Empty(engine.Inputs);
    }

    [Fact]
    public void TruncatedTranslationUsesEntireOriginalForSummary() => SharedStaTestRunner.Run(() =>
    {
        var window = new TranslationWindow("en", "vi");
        try
        {
            window.ShowClipboard(Passage);
            window.ShowResult(new("Chỉ có phần đầu.", "en", "vi", true) { InputTruncated = true });
            var input = window.GetSummaryInput();
            Assert.Equal(Passage, input.Text);
            Assert.Equal("vi", input.TargetLanguage);
            Assert.False(input.InputTruncated);
            window.ShowResult(new(GeneratedSummary, "en", "vi", true) { IsSummary = true });
            Assert.Equal(Visibility.Collapsed, window.TruncatedBadge.Visibility);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("small100-int8-v1")]
    [InlineData("translategemma-4b")]
    [InlineData("hunyuan-mt-7b")]
    public async Task TranslationOnlyEnginesRejectSummaryWithoutLoadingWeights(string model)
    {
        var profile = TranslationModelCatalog.Find(model);
        var store = new TranslationModelStore(root: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), profile: profile);
        ILocalTranslationEngine engine = profile.IsLite ? new LiteTranslationEngine(store) : new QwenTranslationEngine(store);
        try
        {
            var error = await Assert.ThrowsAsync<TranslationException>(() => engine.SummarizeAsync(Passage, "en", default));
            Assert.Equal("translation.summaryModelRequired", error.MessageKey);
        }
        finally { engine.Dispose(); await engine.Disposal; }
    }

    [Fact]
    public async Task MissingGeneralModelReportsInstallRequirement()
    {
        var store = new TranslationModelStore(root: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), profile: TranslationModelProfile.QwenInstruct);
        var engine = new QwenTranslationEngine(store);
        try
        {
            var error = await Assert.ThrowsAsync<TranslationException>(() => engine.SummarizeAsync(Passage, "en", default));
            Assert.Equal("translation.modelMissing", error.MessageKey);
        }
        finally { engine.Dispose(); await engine.Disposal; }
    }

    [Fact]
    public async Task WaitingSummaryCanBeCancelledWithoutStartingSecondInference()
    {
        using var engine = new BlockingSummaryEngine();
        using var service = new LocalTranslationService(engine);
        var input = new TranslationResult(Passage, "en", "en", true);
        Task<TranslationResult> first = service.SummarizeAsync(input, default);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var cancellation = new CancellationTokenSource();
            Task<TranslationResult> second = service.SummarizeAsync(input, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            Assert.Equal(1, engine.Calls);
        }
        finally { engine.Completion.TrySetResult(GeneratedSummary); await first; }
    }

    [Fact]
    public void SummaryKeepsExistingTranslationWarningsAndCurrencyCannotBeSummarized() => SharedStaTestRunner.Run(() =>
    {
        var window = new TranslationWindow("en", "vi");
        try
        {
            window.ShowResult(new("A summary.", "en", "vi", false) { IsSummary = true, SummaryReduced = true });
            Assert.Contains(Loc.Get("translation.checkFacts"), window.StatusText.Text);
            Assert.False(window.CanReplaceResult);
            window.ShowResult(new("100 USD", "en", "en", false) { Currency = new(new(100, "USD"), "USD", 100, null, false) });
            Assert.False(window.SummarizeButton.IsEnabled);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ButtonGeneratesSummaryOfDisplayedTranslationAndKeepsFullInput() => SharedStaTestRunner.RunAsync(async ct =>
    {
        string language = Loc.CurrentLanguage;
        Loc.SetLanguage("en");
        using var engine = new SummaryEngine();
        using var controller = new LiveTranslationController(Dispatcher.CurrentDispatcher, () => { }, engine: engine);
        var window = (TranslationWindow)typeof(LiveTranslationController).GetMethod("EnsureWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(controller, null)!;
        BackgroundTestWindows.ProtectInput(window);
        window.Opacity = 0;
        try
        {
            window.ConfigureLanguages("vi", "en");
            window.ShowSelection(new(1, "Different original text", new Rect(100, 100, 100, 20), IntPtr.Zero, false), true);
            await window.WaitForEntranceAsync(ct);
            window.ShowResult(new(Passage, "vi", "en", true));
            Assert.Equal("Summarize", window.SummarizeLabel.Text);
            Assert.Equal("Summarize", System.Windows.Automation.AutomationProperties.GetName(window.SummarizeButton));
            window.SummarizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(window.SummarizeButton.IsEnabled);
            await WpfFrameWaiter.UntilAsync(() => window.CopyButton.IsEnabled, "summary result", ct);
            string first = window.OutputText;
            Assert.Contains("Summary", window.LanguageSummary.Text);
            Assert.True(first.Length < Passage.Length);
            Assert.Equal(Passage, window.GetSummaryInput().Text);
            window.SummarizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WpfFrameWaiter.UntilAsync(() => window.CopyButton.IsEnabled, "repeat summary", ct);
            Assert.Equal(first, window.OutputText);
            Assert.Equal(0, engine.TranslationCalls);
            Assert.Equal(2, engine.Inputs.Count);
            Assert.All(engine.Inputs, input => { Assert.Equal(Passage, input.Text); Assert.Equal("en", input.Language); });
            Assert.Equal(GeneratedSummary, first);

            if (Environment.GetEnvironmentVariable("VNOTCH_SUMMARY_SCREENSHOT") is { Length: > 0 } screenshot)
            {
                await WpfFrameWaiter.UntilAsync(() => !window.IsPresentationAnimating, "summary layout", ct);
                window.ResultText.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseDownEvent });
                window.UpdateLayout();
                await WpfFrameWaiter.UntilAsync(() => !window.ResultHeightHost.IsMorphing && !window.StatusHeightHost.IsMorphing && !window.IsPresentationAnimating,
                    "settled summary height", ct);
                var bitmap = new RenderTargetBitmap((int)window.PopupSurface.ActualWidth, (int)window.PopupSurface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                    drawing.DrawRectangle(new VisualBrush(window.PopupSurface), null, new Rect(0, 0, bitmap.Width, bitmap.Height));
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(screenshot);
                encoder.Save(stream);
                File.WriteAllText(Path.ChangeExtension(screenshot, ".txt"), window.OutputText);
            }

            window.ShowSelection(new(2, "Nội dung mới.", new Rect(100, 100, 100, 20), IntPtr.Zero, false), true);
            Assert.Equal("Nội dung mới.", window.GetSummaryInput().Text);
        }
        finally { Loc.SetLanguage(language); }
    });

    [Fact]
    public void UnsupportedModelExplainsRequirementAndOffersSettings() => SharedStaTestRunner.RunAsync(async ct =>
    {
        string language = Loc.CurrentLanguage;
        Loc.SetLanguage("vi");
        using var controller = new LiveTranslationController(Dispatcher.CurrentDispatcher, () => { }, engine: new UnusedEngine());
        var window = (TranslationWindow)typeof(LiveTranslationController).GetMethod("EnsureWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(controller, null)!;
        BackgroundTestWindows.ProtectInput(window);
        window.Opacity = 0;
        try
        {
            window.ConfigureLanguages("en", "vi");
            window.ShowSelection(new(1, Passage, new Rect(100, 100, 100, 20), IntPtr.Zero, false), true);
            await window.WaitForEntranceAsync(ct);
            window.SetStatus("translation.modelMissing");
            Assert.Equal("Tóm tắt", window.SummarizeLabel.Text);
            Assert.True(window.SummarizeButton.IsEnabled);
            window.SummarizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WpfFrameWaiter.UntilAsync(() => window.ManageModelButton.Visibility == Visibility.Visible && window.CancelButton.Visibility == Visibility.Collapsed, "summary model requirement", ct);
            Assert.False(window.CopyButton.IsEnabled);
            Assert.Equal(Loc.Get("translation.summaryModelRequired"), window.StatusText.Text);
            window.TargetLanguageCombo.SelectedValue = "fr";
            Assert.Equal(Passage, window.GetSummaryInput().Text);
            Assert.Equal("en", window.GetSummaryInput().TargetLanguage);
        }
        finally { Loc.SetLanguage(language); }
    });

    [Fact]
    public void CancelAndNewSelectionCannotPublishOldSummary() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var controller = new LiveTranslationController(Dispatcher.CurrentDispatcher, () => { }, engine: new SummaryEngine());
        var window = (TranslationWindow)typeof(LiveTranslationController).GetMethod("EnsureWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(controller, null)!;
        BackgroundTestWindows.ProtectInput(window);
        window.Opacity = 0;
        window.ShowSelection(new(1, Passage, new Rect(100, 100, 100, 20), IntPtr.Zero, false), true);
        await window.WaitForEntranceAsync(ct);
        Task pending = (Task)typeof(LiveTranslationController).GetMethod("SummarizeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(controller, null)!;
        window.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await pending;
        Assert.False(window.CopyButton.IsEnabled);
        Assert.Equal(Loc.Get("translation.cancelled"), window.StatusText.Text);
        pending = (Task)typeof(LiveTranslationController).GetMethod("SummarizeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(controller, null)!;
        window.Dismiss();
        window.ShowSelection(new(2, "New text", new Rect(100, 100, 100, 20), IntPtr.Zero, false), true);
        await pending;
        Assert.Empty(window.ResultText.Text);
    });

    private sealed class SummaryEngine : ILocalTranslationEngine
    {
        internal readonly List<(string Text, string Language)> Inputs = [];
        internal int TranslationCalls;
        public Task<string> SummarizeAsync(string text, string language, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Inputs.Add((text, language));
            return Task.FromResult(GeneratedSummary);
        }
        public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
        { TranslationCalls++; throw new InvalidOperationException("Summary must use its own inference path."); }
        public void ReleaseIdleResources() { }
        public void Dispose() { }
    }

    private sealed class BlockingSummaryEngine : ILocalTranslationEngine
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<string> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        public Task<string> SummarizeAsync(string text, string language, CancellationToken cancellationToken)
        { Calls++; Started.TrySetResult(); return Completion.Task.WaitAsync(cancellationToken); }
        public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken) =>
            throw new InvalidOperationException();
        public void ReleaseIdleResources() { }
        public void Dispose() { }
    }

    private sealed class UnusedEngine : ILocalTranslationEngine
    {
        public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unsupported models must not receive summary prompts.");
        public void ReleaseIdleResources() { }
        public void Dispose() { }
    }
}
