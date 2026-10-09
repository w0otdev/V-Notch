using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using VNotch.Models;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class LiveTranslationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DismissAnimationCannotHideANewerSelection(bool reduceMotion) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = VNotch.Services.AnimationConfig.ReduceMotion;
        VNotch.Services.AnimationConfig.SetReduceMotion(reduceMotion);
        var window = new TranslationWindow("auto", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        int dismissals = 0;
        window.Dismissed += () => dismissals++;
        try
        {
            window.ShowSelection(new(1, "first", new Rect(10, 10, 100, 20), IntPtr.Zero, false), true);
            Assert.Equal(Visibility.Visible, window.LoadingIndicator.Visibility);
            if (reduceMotion) Assert.False(window.LoadingIndicator.IsAnimating);
            window.Dismiss();
            Assert.False(window.LoadingIndicator.IsAnimating);
            Assert.Equal(1, dismissals);
            if (reduceMotion) Assert.False(window.IsVisible);
            window.ShowSelection(new(2, "second", new Rect(10, 10, 100, 20), IntPtr.Zero, false), true);
            await WpfFrameWaiter.UntilAsync(() => !window.IsPresentationAnimating, "new selection entrance settles", ct);
            Assert.True(window.IsVisible);
            Assert.Equal("second", window.OriginalText.Text);
            Assert.True(window.PopupSurface.IsHitTestVisible);
            Assert.Equal(1, window.PopupSurface.Opacity, 3);
            window.ShowResult(new("Thứ hai", "en", "vi", true));
            Assert.Equal(Visibility.Collapsed, window.LoadingIndicator.Visibility);
            Assert.False(window.LoadingIndicator.IsAnimating);
            window.Dismiss();
            await WpfFrameWaiter.UntilAsync(() => !window.IsVisible, "dismiss finishes", ct);
            Assert.False(window.IsVisible);
            Assert.Empty(window.OriginalText.Text);
        }
        finally { window.Close(); VNotch.Services.AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData("This branch")]
    [InlineData("Hello")]
    [InlineData("商品発送")]
    [InlineData("a")]
    public async Task AutoSourceSendsShortSelectionsToTheModel(string input)
    {
        using var engine = new FakeEngine((text, source, target, _) =>
        {
            Assert.Equal(input, text);
            Assert.Equal("auto", source);
            Assert.Equal("vi", target);
            return Task.FromResult("Bản dịch");
        });
        using var service = new LocalTranslationService(engine);
        Assert.Equal("Bản dịch", (await service.TranslateAsync(input, "auto", "vi", default)).Text);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task AutoSourceDoesNotSkipInferenceBasedOnADetectorGuess()
    {
        using var engine = new FakeEngine((_, source, _, _) =>
        { Assert.Equal("auto", source); return Task.FromResult("Kết quả"); });
        using var service = new LocalTranslationService(engine);
        var result = await service.TranslateAsync("Đây là một đoạn tiếng Việt đủ dài để bộ nhận diện tìm thấy.", "auto", "vi", default);
        Assert.Equal("Kết quả", result.Text);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public void CompleteOutputIsRecognizedBeforeTheModelEndToken()
    {
        Assert.False(QwenTranslationEngine.TryReadOutput("{\"translation\":\"a } bracket", out _));
        Assert.True(QwenTranslationEngine.TryReadOutput("{\"translation\":\"a } bracket\"}   ", out string text));
        Assert.Equal("a } bracket", text);
        Assert.False(QwenTranslationEngine.TryReadOutput("{\"translation\":\"ok\",\"extra\":1}", out _));
    }

    [Fact]
    public void AutoSourceHeaderAndCancellationDoNotKeepAStaleLanguagePair() => SharedStaTestRunner.Run(() =>
    {
        var window = new TranslationWindow("auto", "vi");
        try
        {
            window.ShowResult(new("Nhánh này", "auto", "vi", true));
            Assert.StartsWith(VNotch.Services.Loc.Get("translation.autoDetect"), window.LanguageSummary.Text);
            window.SourceLanguageCombo.SelectedValue = "en";
            Assert.StartsWith(TranslationLanguages.All.Single(x => x.Code == "en").Name, window.LanguageSummary.Text);
            Assert.Empty(window.ResultText.Text);
            window.SetStatus("translation.working");
            window.ShowActivity(TranslationStage.LoadingModel, 3);
            Assert.Equal(VNotch.Services.Loc.Get("translation.loadingElapsed", 3), window.StatusText.Text);
            Assert.Equal(Visibility.Visible, window.CancelButton.Visibility);
            bool cancelled = false;
            window.CancelRequested += () => cancelled = true;
            window.CancelButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.True(cancelled);
            window.SetStatus("translation.cancelled");
            Assert.True(window.TranslateButton.IsEnabled);
            Assert.Equal(Visibility.Collapsed, window.CancelButton.Visibility);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ShortHanOnlyTextDoesNotSilentlyBecomeChinese()
    {
        Assert.Null(TranslationLanguages.Detect("商品発送"));
        Assert.Equal("zh", TranslationLanguages.Detect("您好！这批货总价为2380元，明天下午三点发货。"));
    }

    [Fact]
    public void ProtectedWeekdaysRestoreTheirMeaningInTheTargetLanguage()
    {
        var days = TranslationDateIntegrity.Protect("预计周五寄出，周日送达。", "zh", "vi");
        string friday = days.Values.Single(p => p.Value.Equals("Thứ Sáu", StringComparison.OrdinalIgnoreCase)).Key;
        string sunday = days.Values.Single(p => p.Value.Equals("Chủ Nhật", StringComparison.OrdinalIgnoreCase)).Key;
        string rendered = days.Restore($"Gửi vào {friday}, đến vào {sunday}.");
        Assert.Contains("Thứ Sáu", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Chủ Nhật", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.True(TranslationDateIntegrity.PreservesWeekdays("预计周五寄出，周日送达。", "zh", rendered, "vi"));
        Assert.Throws<TranslationException>(() => days.Restore("Gửi vào thứ Năm, đến vào chủ nhật."));
    }

    [Theory]
    [InlineData("预计周五寄出，周日送达。", "zh", "Gửi vào thứ Sáu, đến vào Chủ Nhật.", "vi", true)]
    [InlineData("预计周五寄出，周日送达。", "zh", "Gửi vào thứ Năm, đến vào Chủ Nhật.", "vi", false)]
    [InlineData("発送は金曜日、到着は日曜日です。", "ja", "Gửi vào thứ Sáu, đến vào Chủ Nhật.", "vi", true)]
    [InlineData("Ship on Friday.", "en", "Gửi hàng ngày mai.", "vi", false)]
    public void ChangedWeekdaysDisableReplacement(string source, string sourceLanguage, string result, string targetLanguage, bool preserved)
        => Assert.Equal(preserved, TranslationDateIntegrity.PreservesWeekdays(source, sourceLanguage, result, targetLanguage));

    [Fact]
    public void ProtectedLiteralsRoundTripWithoutInventingCurrency()
    {
        const string original = "__VNT_LITERAL0__: AB-42, 2380元, ¥2,380. https://example.com/item?id=42";
        var protectedText = TranslationTextIntegrity.Protect(original);
        Assert.Equal(original, protectedText.Restore(protectedText.Text));
        Assert.DoesNotContain("2380元", protectedText.Text);
        Assert.True(TranslationTextIntegrity.Verify(original, protectedText.Restore(protectedText.Text)));
        Assert.False(TranslationTextIntegrity.Verify("2380元", "2380 VND"));
        Assert.Throws<TranslationException>(() => protectedText.Restore("Missing literals"));
        Assert.Equal("Lúc 3 giờ", TranslationTextIntegrity.Protect("At 3 hours").Restore("Lúc 3 giờ"));
    }

    [Fact]
    public void TranslationOutputUnwrapsOnlyTheKnownModelEnvelope()
    {
        Assert.Equal("Bản dịch", QwenTranslationEngine.ReadOutput("{\"translation\":\"Bản dịch\"}"));
        Assert.Throws<TranslationException>(() => QwenTranslationEngine.ReadOutput("{\"translation\":\"data\",\"extra\":\"unexpected\"}"));
        Assert.Throws<TranslationException>(() => QwenTranslationEngine.ReadOutput("{\"translation\":\"incomplete"));
    }

    [Fact]
    public void AllSelectableLanguagesHaveAnUnambiguousPromptName()
    {
        foreach (string code in TranslationLanguages.Codes)
            Assert.False(string.IsNullOrWhiteSpace(TranslationLanguages.EnglishName(code)));
    }

    [Fact]
    public void PopupClearsOldResultsWhenLanguagesChange() => SharedStaTestRunner.Run(() =>
    {
        var window = new TranslationWindow("en", "vi");
        try
        {
            window.ShowResult(new("Bản dịch", "en", "vi", true));
            Assert.True(window.CopyButton.IsEnabled);
            Assert.False(window.ReplaceButton.IsEnabled); // no editable selection
            bool changed = false;
            window.LanguagesChanged += (source, target) => changed = source == "en" && target == "fr";
            window.TargetLanguageCombo.SelectedValue = "fr";
            Assert.True(changed);
            Assert.Empty(window.ResultText.Text);
            Assert.False(window.CopyButton.IsEnabled);
            Assert.True(window.ResultText.IsReadOnly);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void SettingsRemainOptInAndSurviveCloningAndSerialization()
    {
        var defaults = JsonSerializer.Deserialize<NotchSettings>("{}")!;
        Assert.False(defaults.EnableLiveTranslation);
        Assert.False(defaults.AutoLiveTranslation);
        var settings = new NotchSettings { EnableLiveTranslation = true, AutoLiveTranslation = true, TranslationSourceLanguage = "ja", TranslationTargetLanguage = "en" };
        var copy = JsonSerializer.Deserialize<NotchSettings>(JsonSerializer.Serialize(settings.Clone()))!;
        Assert.True(copy.EnableLiveTranslation);
        Assert.True(copy.AutoLiveTranslation);
        Assert.Equal("ja", copy.TranslationSourceLanguage);
        Assert.Equal("en", copy.TranslationTargetLanguage);
    }

    [Fact]
    public async Task CacheIncludesBothLanguagesAndReusesOnlyCompletedResults()
    {
        using var engine = new FakeEngine((text, _, _, _) => Task.FromResult(text));
        using var service = new LocalTranslationService(engine);
        await service.TranslateAsync("Invoice 42", "en", "vi", default);
        await service.TranslateAsync("Invoice 42", "en", "vi", default);
        Assert.Equal(1, engine.Calls);
        await service.TranslateAsync("Invoice 42", "en", "ja", default);
        await service.TranslateAsync("Invoice 42", "fr", "vi", default);
        Assert.Equal(3, engine.Calls);
    }

    [Fact]
    public async Task CancellationReachesEngineAndDoesNotPoisonNextRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var engine = new FakeEngine(async (text, _, _, token) =>
        {
            if (text == "cancel") { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); }
            return text;
        });
        using var service = new LocalTranslationService(engine);
        using var cancellation = new CancellationTokenSource();
        var first = service.TranslateAsync("cancel", "en", "vi", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal("next", (await service.TranslateAsync("next", "en", "vi", default)).Text);
    }

    [Fact]
    public async Task WaitingRequestCanBeCancelledWithoutStartingInference()
    {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var engine = new FakeEngine((_, _, _, _) => { entered.TrySetResult(); return release.Task; });
        using var service = new LocalTranslationService(engine);
        var first = service.TranslateAsync("first", "en", "vi", default);
        await entered.Task;
        using var token = new CancellationTokenSource();
        var queued = service.TranslateAsync("queued", "en", "vi", token.Token);
        token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(1, engine.Calls);
        release.SetResult("first");
        await first;
    }

    [Fact]
    public async Task SameLanguagePreservesTextWithoutInference()
    {
        using var engine = new FakeEngine((_, _, _, _) => throw new InvalidOperationException());
        using var service = new LocalTranslationService(engine);
        var result = await service.TranslateAsync("Thông tin: https://example.com/42 — ¥2,380", "vi", "vi", default);
        Assert.True(result.LiteralsPreserved);
        Assert.Equal(0, engine.Calls);
    }

    [Theory]
    [InlineData("Total ¥2,380. https://example.com/42", "Tổng ¥2,380. https://example.com/42", true)]
    [InlineData("Total ¥2,380", "Tổng $2,380", false)]
    [InlineData("Order AB-42", "Đơn AB-43", false)]
    [InlineData("https://example.com/42", "https://example.com/43", false)]
    [InlineData("Call 15:00", "Gọi 15:30", false)]
    [InlineData("Balance -$42", "Số dư $42", false)]
    [InlineData("Delta −5%", "Độ lệch 5%", false)]
    public void LiteralChangesAreReported(string source, string result, bool safe)
        => Assert.Equal(safe, TranslationTextIntegrity.Verify(source, result));

    [Fact]
    public async Task HashValidationRejectsSameSizeTampering()
    {
        string path = Path.GetTempFileName();
        try
        {
            byte[] content = [1, 2, 3];
            var asset = new TranslationModelStore.Asset("test", "test", content.Length, Convert.ToHexString(SHA256.HashData(content)));
            await File.WriteAllBytesAsync(path, content);
            await TranslationModelStore.VerifyAssetAsync(path, asset, default);
            await File.WriteAllBytesAsync(path, [1, 2, 4]);
            await Assert.ThrowsAsync<TranslationException>(() => TranslationModelStore.VerifyAssetAsync(path, asset, default));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("https://huggingface.co/model", true)]
    [InlineData("https://cas-bridge.xethub.hf.co/model", true)]
    [InlineData("https://huggingface.co.evil.example/model", false)]
    [InlineData("http://huggingface.co/model", false)]
    [InlineData("https://user@huggingface.co/model", false)]
    [InlineData("https://huggingface.co:8443/model", false)]
    public void ModelRedirectsStayWithinPinnedProvider(string uri, bool allowed)
        => Assert.Equal(allowed, TranslationModelStore.IsAllowedDownloadUri(new Uri(uri)));

    [Fact]
    public void PopupClampsOnNegativeMonitorCoordinatesAndSmallWorkAreas()
    {
        var work = new Rect(-1920, 40, 1920, 1040);
        var placement = TranslationWindow.ClampPlacement(new Rect(-10, 1050, 100, 30), work, 500, 440);
        Assert.True(work.Contains(new Rect(placement, new Size(500, 440))));
    }

    private sealed class FakeEngine(Func<string, string, string, CancellationToken, Task<string>> translate) : ILocalTranslationEngine
    {
        internal int Calls;
        public Task<string> TranslateAsync(string text, string source, string target, CancellationToken token)
        { Interlocked.Increment(ref Calls); return translate(text, source, target, token); }
        public void ReleaseIdleResources() { }
        public void Dispose() { }
    }
}

