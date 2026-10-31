using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class TranslationStartupTests
{
    [Theory]
    [InlineData("auto", "VND")]
    [InlineData("JPY", "JPY")]
    public void SelectedPriceBypassesMissingModelAndRespectsCurrencyOverride(string choice, string target) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new ModelFixture();
        File.Delete(fixture.Path);
        using var engine = new PreparedEngine();
        var privacy = new VNotch.Services.NetworkPrivacy();
        privacy.Apply(new NotchSettings());
        using var client = new System.Net.Http.HttpClient(new CurrencyHandler(target));
        var rates = new CurrencyRateService(client, privacy, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fixture.Path)!, "rates"),
            () => new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
        using var controller = new LiveTranslationController(Dispatcher.CurrentDispatcher, () => { }, fixture.Store, engine, currencyRates: rates);
        controller.ApplySettings(new NotchSettings { TranslationTargetLanguage = "vi", TranslationTargetCurrency = choice });
        var window = (TranslationWindow)typeof(LiveTranslationController).GetMethod("EnsureWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(controller, null)!;
        BackgroundTestWindows.ProtectInput(window);
        window.Opacity = 0;
        window.ShowActivated = false;
        typeof(LiveTranslationController).GetField("_text", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(controller, "USD 10");
        window.ShowClipboard("USD 10");
        window.TranslateButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await WpfFrameWaiter.UntilAsync(() => window.ResultText.Text.EndsWith(target), "currency result without model", ct);
        Assert.Equal(0, engine.Calls);
        Assert.False(engine.Preparing.Task.IsCompleted);
        Assert.Equal("USD → " + target, window.LanguageSummary.Text);
        Assert.Contains("2026-10-11", window.StatusText.Text);
        Assert.Contains("Frankfurter", window.StatusText.Text);
        Assert.True(window.CopyButton.IsEnabled);
        Assert.False(window.ReplaceButton.IsEnabled);
    });

    [Theory]
    [InlineData(true, "vi")]
    [InlineData(false, "vi")]
    [InlineData(false, "am")]
    public void UnavailableOrUnmappedCurrencyFallsBackToLocalTranslation(bool localOnly, string target) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new ModelFixture();
        using var engine = new PreparedEngine { Echo = true };
        var privacy = new VNotch.Services.NetworkPrivacy();
        privacy.Apply(new NotchSettings { EnableLocalOnlyMode = localOnly });
        using var client = new System.Net.Http.HttpClient(new UnavailableCurrencyHandler());
        var rates = new CurrencyRateService(client, privacy, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fixture.Path)!, "rates"));
        using var controller = new LiveTranslationController(Dispatcher.CurrentDispatcher, () => { }, fixture.Store, engine, currencyRates: rates);
        controller.ApplySettings(new NotchSettings { TranslationSourceLanguage = "en", TranslationTargetLanguage = target });
        var window = (TranslationWindow)typeof(LiveTranslationController).GetMethod("EnsureWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(controller, null)!;
        BackgroundTestWindows.ProtectInput(window);
        window.Opacity = 0;
        window.ShowActivated = false;
        typeof(LiveTranslationController).GetField("_text", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(controller, "USD 10");
        window.ShowClipboard("USD 10");
        window.TranslateButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await WpfFrameWaiter.UntilAsync(() => window.ResultText.Text == "USD 10", "local fallback", ct);
        Assert.Equal(1, engine.Calls);
    });

    private sealed class UnavailableCurrencyHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new System.Net.Http.HttpRequestException("offline");
    }

    private sealed class CurrencyHandler(string target) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://api.frankfurter.dev/v2/rate/USD/" + target, request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent($"{{\"base\":\"USD\",\"quote\":\"{target}\",\"rate\":25000,\"date\":\"2026-10-11\"}}")
            });
        }
    }

    [Fact]
    public async Task ConsecutiveSpecialistInferencesDoNotDisposeContextHandle()
    {
        var profile = TranslationModelCatalog.Find("translategemma-4b");
        var store = new TranslationModelStore(profile: profile);
        Assert.Equal(TranslationPromptKind.TranslateGemma, profile.PromptKind);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(VNotch.Services.AppPaths.LocalAppDataRoot, "translation-models", "translategemma-4b")),
            store.Root);
        Assert.StartsWith(VNotch.Services.AppPaths.LocalAppDataRoot, store.Root, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            VNotch.Services.AppPaths.LegacyLocalAppDataRoot + Path.DirectorySeparatorChar,
            store.Root,
            StringComparison.OrdinalIgnoreCase);

        using var engine = new PreparedEngine { Echo = true, Profile = profile };
        using var service = new LocalTranslationService(engine);
        var options = new TranslationOptions { GpuLayers = 36, ContextSize = 2048 };
        service.Configure(options);
        Assert.False(QwenTranslationEngine.RequiresNewContext(options, options));

        string[] samples =
        [
            "*(Lưu ý: Hiện có một tiến trình V-Notch.exe bản cũ đang chạy nền trên máy, bạn hãy tắt và build/chạy lại app để áp dụng code mới).*",
            "Lưu ý: V-Notch.exe đang chạy nền.",
            "Please check `SelectionTextReader.cs` and `TranslationSelectionService.cs`.",
            "See https://github.com/example/repo for details.",
            "**Important**: Restart `V-Notch.exe` to apply the fix."
        ];
        foreach (string s in samples)
        {
            var res = await service.TranslateAsync(s, "auto", "vi", default);
            Assert.False(string.IsNullOrWhiteSpace(res.Text));
            Assert.True(res.LiteralsPreserved);
        }

        Assert.True(engine.Calls >= 3);
        Assert.Equal(1, engine.ContextHandlesCreated);
        Assert.Equal(0, engine.ContextHandlesDisposed);
        Assert.False(engine.Disposed);
    }

    [Fact]
    public void CachedResultReachesPopupWhileModelPreparationIsStillRunning() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new ModelFixture();
        using var engine = new PreparedEngine { BlockPreparation = true };
        using var controller = new LiveTranslationController(Dispatcher.CurrentDispatcher, () => { }, fixture.Store, engine);
        controller.ApplySettings(new NotchSettings { TranslationSourceLanguage = "en", TranslationTargetLanguage = "vi", EnableCurrencyConversion = false });
        var service = (LocalTranslationService)typeof(LiveTranslationController).GetField("_service", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(controller)!;
        await service.TranslateAsync("hello", "en", "vi", ct);
        var preparation = controller.PrepareModelAsync();
        await engine.Preparing.Task.WaitAsync(ct);
        try
        {
            var window = (TranslationWindow)typeof(LiveTranslationController).GetMethod("EnsureWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(controller, null)!;
            BackgroundTestWindows.ProtectInput(window);
            window.Opacity = 0;
            window.ShowActivated = false;
            typeof(LiveTranslationController).GetField("_text", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(controller, "hello");
            window.ShowClipboard("hello");
            window.TranslateButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await WpfFrameWaiter.UntilAsync(() => window.OutputText == "vi result" && window.CopyButton.IsEnabled, "cached result during model preparation", ct);
            Assert.False(preparation.IsCompleted);
            Assert.Equal(1, engine.Calls);
        }
        finally { engine.Prepared.TrySetResult(); await preparation.WaitAsync(ct); }
    });

    [Fact]
    public void StartupPreparesInstalledModelOffDispatcherWithTranslationDisabled() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new ModelFixture();
        using var engine = new PreparedEngine { BlockPreparation = true };
        using var controller = new LiveTranslationController(Dispatcher.CurrentDispatcher, () => { }, fixture.Store, engine);
        controller.ApplySettings(new NotchSettings { EnableLiveTranslation = false });
        int uiThread = Environment.CurrentManagedThreadId;
        var preparation = controller.PrepareModelAsync();
        Assert.Same(preparation, controller.PrepareModelAsync());
        await engine.Preparing.Task.WaitAsync(ct);
        Assert.NotEqual(uiThread, engine.PreparationThread);
        Assert.False(preparation.IsCompleted);
        Assert.Equal(0, engine.Calls);
        engine.Prepared.TrySetResult();
        await preparation.WaitAsync(ct);
    });

    [Fact]
    public void StartupSkipsMissingOrDisposedModel() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new ModelFixture();
        using var engine = new PreparedEngine();
        using var controller = new LiveTranslationController(Dispatcher.CurrentDispatcher, () => { }, fixture.Store, engine);
        File.Delete(fixture.Path);
        await controller.PrepareModelAsync().WaitAsync(ct);
        Assert.False(engine.Preparing.Task.IsCompleted);
        controller.Dispose();
        await controller.PrepareModelAsync().WaitAsync(ct);
        Assert.False(engine.Preparing.Task.IsCompleted);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingSelectionOffersEmptyManualInputOnlyWhileRequestIsCurrent(bool focusChanged) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new ModelFixture();
        using var engine = new PreparedEngine();
        var foreground = new IntPtr(42);
        using var controller = new LiveTranslationController(Dispatcher.CurrentDispatcher, () => { }, fixture.Store, engine, () => foreground);
        controller.ApplySettings(new NotchSettings());
        typeof(LiveTranslationController).GetField("_selectionService", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(controller, new TranslationSelectionService());
        ((NotchSettings)typeof(LiveTranslationController).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(controller)!).EnableLiveTranslation = true;
        var window = (TranslationWindow)typeof(LiveTranslationController).GetMethod("EnsureWindow", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(controller, null)!;
        BackgroundTestWindows.ProtectInput(window);
        window.Opacity = 0;
        window.ShowActivated = false;
        typeof(LiveTranslationController).GetMethod("ManualSelectionUnavailable", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(controller, [foreground, 0L]);
        if (focusChanged) foreground = new IntPtr(43);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, ct);
        Assert.Equal(!focusChanged, window.IsVisible);
        Assert.Empty(window.OriginalText.Text);
        Assert.Empty(window.ResultText.Text);
        Assert.Equal(0, engine.Calls);
        if (!focusChanged) Assert.Equal(VNotch.Services.Loc.Get("translation.noSelection"), window.StatusText.Text);
    });

    [Fact]
    public void RepeatedManualRequestsAndEmptyKeyUpSnapshotsKeepTheExistingPopupAndResult() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new ModelFixture();
        using var engine = new PreparedEngine();
        var foreground = new IntPtr(42);
        using var controller = new LiveTranslationController(Dispatcher.CurrentDispatcher, () => { }, fixture.Store, engine, () => foreground);
        controller.ApplySettings(new NotchSettings { TranslationSourceLanguage = "en", TranslationTargetLanguage = "vi" });
        // Inject an unwired watcher so real desktop selection events cannot steer this fixture.
        typeof(LiveTranslationController).GetField("_selectionService", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(controller, new TranslationSelectionService());
        ((NotchSettings)typeof(LiveTranslationController).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(controller)!).EnableLiveTranslation = true;
        var window = (TranslationWindow)typeof(LiveTranslationController).GetMethod("EnsureWindow", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(controller, null)!;
        BackgroundTestWindows.ProtectInput(window);
        window.Opacity = 0;
        window.ShowActivated = false;
        var selection = new TranslationSelection(1, "hello", new System.Windows.Rect(100, 100, 100, 20), foreground, false);
        var present = typeof(LiveTranslationController).GetMethod("PresentSelection", BindingFlags.NonPublic | BindingFlags.Instance)!;
        present.Invoke(controller, [selection, true, null]);
        await WpfFrameWaiter.UntilAsync(() => window.ResultText.Text == "vi result" && !window.IsPresentationAnimating, "manual translation settles", ct);
        for (int i = 2; i < 14; i++)
        {
            present.Invoke(controller, [selection with { Id = i }, true, null]);
            present.Invoke(controller, [selection with { Id = i }, false, null]);
            present.Invoke(controller, [null, false, null]);
        }
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, ct);
        Assert.True(window.IsVisible);
        Assert.False(window.IsPresentationAnimating);
        Assert.Equal("hello", window.OriginalText.Text);
        Assert.Equal("vi result", window.ResultText.Text);
        Assert.Equal(1, engine.Calls);

        window.Dismiss();
        await WpfFrameWaiter.UntilAsync(() => !window.IsVisible, "explicit dismissal finishes", ct);
        present.Invoke(controller, [selection, true, null]);
        await WpfFrameWaiter.UntilAsync(() => window.IsVisible && window.ResultText.Text == "vi result", "a new deliberate request reopens", ct);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangingLanguagesRetranslatesWithAutoTranslationDisabled(bool throughSettings) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new ModelFixture();
        using var engine = new PreparedEngine();
        using var controller = new LiveTranslationController(Dispatcher.CurrentDispatcher, () => { }, fixture.Store, engine);
        var settings = new NotchSettings { TranslationSourceLanguage = "en", TranslationTargetLanguage = "vi", AutoLiveTranslation = false };
        controller.ApplySettings(settings);
        var window = (TranslationWindow)typeof(LiveTranslationController).GetMethod("EnsureWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(controller, null)!;
        BackgroundTestWindows.ProtectInput(window);
        window.Opacity = 0;
        window.ShowActivated = false;
        typeof(LiveTranslationController).GetField("_text", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(controller, "hello");
        window.ShowClipboard("hello");
        if (throughSettings)
        {
            settings.TranslationTargetLanguage = "fr";
            controller.ApplySettings(settings);
        }
        else window.TargetLanguageCombo.SelectedValue = "fr";
        await WpfFrameWaiter.UntilAsync(() => window.ResultText.Text == "fr result", "new language result", ct);
        Assert.True(window.IsVisible);
        Assert.Equal(1, engine.Calls);
        window.SourceLanguageCombo.SelectedValue = "de";
        await WpfFrameWaiter.UntilAsync(() => engine.Calls == 2 && window.ResultText.Text == "fr result", "source language result", ct);
        Assert.Equal("de", engine.LastSource);
    });

    [Fact]
    public void RapidLanguageChangesCancelTheOldTranslationAndPresentOnlyTheNewestPair() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new ModelFixture();
        using var engine = new PreparedEngine { BlockFirstTranslation = true };
        using var controller = new LiveTranslationController(Dispatcher.CurrentDispatcher, () => { }, fixture.Store, engine);
        controller.ApplySettings(new NotchSettings { TranslationSourceLanguage = "en", TranslationTargetLanguage = "vi" });
        var window = (TranslationWindow)typeof(LiveTranslationController).GetMethod("EnsureWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(controller, null)!;
        BackgroundTestWindows.ProtectInput(window);
        window.Opacity = 0;
        window.ShowActivated = false;
        typeof(LiveTranslationController).GetField("_text", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(controller, "hello");
        window.ShowClipboard("hello");
        window.TargetLanguageCombo.SelectedValue = "fr";
        await engine.Translating.Task.WaitAsync(ct);
        window.TargetLanguageCombo.SelectedValue = "de";
        await WpfFrameWaiter.UntilAsync(() => window.ResultText.Text == "de result", "latest language result", ct);
        Assert.True(engine.FirstCancelled);
        Assert.Equal(2, engine.Calls);
        Assert.Equal("de", window.TargetLanguage);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TranslationRunsOffDispatcherWithoutWaitingForEntrance(bool reduced) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = VNotch.Services.AnimationConfig.ReduceMotion;
        VNotch.Services.AnimationConfig.SetReduceMotion(reduced);
        using var fixture = new ModelFixture();
        using var engine = new PreparedEngine();
        using var controller = new LiveTranslationController(Dispatcher.CurrentDispatcher, () => { }, fixture.Store, engine);
        controller.ApplySettings(new NotchSettings { TranslationSourceLanguage = "en", TranslationTargetLanguage = "vi" });
        var window = (TranslationWindow)typeof(LiveTranslationController).GetMethod("EnsureWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(controller, null)!;
        BackgroundTestWindows.ProtectInput(window);
        window.Opacity = 0;
        window.ShowActivated = false;
        int uiThread = Environment.CurrentManagedThreadId, inferenceThread = uiThread;
        engine.OnTranslate = () => inferenceThread = Environment.CurrentManagedThreadId;
        try
        {
            typeof(LiveTranslationController).GetField("_text", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(controller, "hello");
            window.ShowSelection(new(1, "hello", new System.Windows.Rect(100, 100, 100, 20), IntPtr.Zero, false), true);
            // Hold the entrance gate without pumping WPF. Translation must still
            // begin on a worker thread, independently of render/animation progress.
            var entrance = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            typeof(TranslationWindow).GetField("_entranceCompletion", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, entrance);
            window.TranslateButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.True(engine.Translating.Task.Wait(TimeSpan.FromSeconds(3)));
            Assert.False(entrance.Task.IsCompleted);
            Assert.NotEqual(uiThread, inferenceThread);
            entrance.TrySetResult();
            await WpfFrameWaiter.UntilAsync(() => window.ResultText.Text == "vi result", "translation overlaps entrance", ct);
            Assert.False(window.IsPresentationAnimating);
            Assert.Equal(1, engine.Calls);
        }
        finally { VNotch.Services.AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData("cancel")]
    [InlineData("dismiss")]
    [InlineData("close")]
    public void CancellingDuringEntranceCancelsModelWorkAndSuppressesResult(string action) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new ModelFixture();
        using var engine = new PreparedEngine { BlockFirstTranslation = true };
        using var controller = new LiveTranslationController(Dispatcher.CurrentDispatcher, () => { }, fixture.Store, engine);
        var window = (TranslationWindow)typeof(LiveTranslationController).GetMethod("EnsureWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(controller, null)!;
        BackgroundTestWindows.ProtectInput(window);
        window.Opacity = 0;
        window.ShowActivated = false;
        typeof(LiveTranslationController).GetField("_text", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(controller, "hello");
        window.ShowSelection(new(1, "hello", new System.Windows.Rect(100, 100, 100, 20), IntPtr.Zero, false), true);
        window.TranslateButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert.True(window.IsPresentationAnimating);
        Assert.True(engine.Translating.Task.Wait(TimeSpan.FromSeconds(3)));
        if (action == "cancel") window.CancelButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        else if (action == "dismiss") window.Dismiss();
        else window.Close();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (action != "close") await WpfFrameWaiter.UntilAsync(() => !window.IsPresentationAnimating, "cancelled entrance settles", ct);
        await engine.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
        Assert.True(engine.FirstCancelled);
        Assert.Equal(1, engine.Calls);
        Assert.Empty(window.ResultText.Text);
        Assert.True(engine.Preparing.Task.IsCompleted);
    });

    [Fact]
    public void DismissingDuringColdStartKeepsPreparationAndSuppressesTheOldResult() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new ModelFixture();
        using var engine = new PreparedEngine { BlockPreparation = true };
        using var controller = new LiveTranslationController(Dispatcher.CurrentDispatcher, () => { }, fixture.Store, engine);
        var window = (TranslationWindow)typeof(LiveTranslationController).GetMethod("EnsureWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(controller, null)!;
        BackgroundTestWindows.ProtectInput(window);
        window.Opacity = 0;
        typeof(LiveTranslationController).GetField("_text", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(controller, "hello");
        window.ShowSelection(new(1, "hello", new System.Windows.Rect(100, 100, 100, 20), IntPtr.Zero, false), true);
        window.TranslateButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await engine.Preparing.Task.WaitAsync(ct);
        var preparation = controller.PrepareModelAsync();
        window.Dismiss();
        Assert.False(preparation.IsCompleted);
        engine.Prepared.TrySetResult();
        await preparation.WaitAsync(ct);
        await WpfFrameWaiter.UntilAsync(() => !window.IsVisible, "cold-start popup dismissed", ct);
        Assert.Equal(0, engine.Calls);
        Assert.Empty(window.ResultText.Text);
    });

    [Fact]
    public async Task PreparationAndTranslationShareOneGateAndCancellationDoesNotStopPreparation()
    {
        using var engine = new PreparedEngine { BlockPreparation = true };
        using var service = new LocalTranslationService(engine);
        var preparation = service.PrepareAsync(default);
        await engine.Preparing.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var cancellation = new CancellationTokenSource();
        var waiting = service.TranslateAsync("hello", "en", "fr", cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, engine.Calls);
        Assert.False(preparation.IsCompleted);
        engine.Prepared.SetResult();
        await preparation;
        Assert.Equal("fr result", (await service.TranslateAsync("hello", "en", "fr", default)).Text);
    }

    [Fact]
    public async Task VerifiedUnchangedModelDoesNotReadItsContentsAgainButChangedModelIsRejected()
    {
        using var fixture = new ModelFixture();
        await fixture.Store.VerifyAsync(default);
        // Metadata remains readable, but a second full hash would fail to open this handle.
        using (var exclusive = new FileStream(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.None))
            await fixture.Store.VerifyAsync(default);
        await File.WriteAllBytesAsync(fixture.Path, [1, 2, 4]);
        File.SetLastWriteTimeUtc(fixture.Path, DateTime.UtcNow.AddSeconds(2));
        await Assert.ThrowsAsync<TranslationException>(() => fixture.Store.VerifyAsync(default));
    }

    [Fact]
    public async Task CachedVerificationStillHonorsCancellation()
    {
        using var fixture = new ModelFixture();
        await fixture.Store.VerifyAsync(default);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.VerifyAsync(cancellation.Token));
    }

    [Fact]
    public void ChangingCurrencyCancelsOldRateRequestAndOnlyPresentsNewestPair() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new ModelFixture();
        File.Delete(fixture.Path);
        using var engine = new PreparedEngine();
        using var handler = new ChangingCurrencyHandler();
        using var client = new System.Net.Http.HttpClient(handler);
        var privacy = new VNotch.Services.NetworkPrivacy();
        privacy.Apply(new NotchSettings());
        var rates = new CurrencyRateService(client, privacy, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fixture.Path)!, "rates"),
            () => new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
        using var controller = new LiveTranslationController(Dispatcher.CurrentDispatcher, () => { }, fixture.Store, engine, currencyRates: rates);
        controller.ApplySettings(new NotchSettings { TranslationTargetCurrency = "VND" });
        var window = (TranslationWindow)typeof(LiveTranslationController).GetMethod("EnsureWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(controller, null)!;
        BackgroundTestWindows.ProtectInput(window);
        window.Opacity = 0;
        typeof(LiveTranslationController).GetField("_text", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(controller, "USD 50");
        window.ShowClipboard("USD 50");
        window.TranslateButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await handler.Started.Task.WaitAsync(ct);
        window.SourceCurrencyCombo.SelectedValue = "SGD";
        window.TargetCurrencyCombo.SelectedValue = "EUR";
        await WpfFrameWaiter.UntilAsync(() => window.ResultText.Text.EndsWith("EUR"), "newest currency pair", ct);
        Assert.Equal("SGD → EUR", window.LanguageSummary.Text);
        Assert.Equal("500", window.OutputText);
        Assert.True(handler.FirstCancelled);
        Assert.Equal(0, engine.Calls);
    });

    private sealed class ChangingCurrencyHandler : System.Net.Http.HttpMessageHandler
    {
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool FirstCancelled;
        private int _calls;
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { FirstCancelled = true; throw; }
            }
            var parts = request.RequestUri!.AbsolutePath.Split('/');
            return new(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent($"{{\"base\":\"{parts[^2]}\",\"quote\":\"{parts[^1]}\",\"rate\":10,\"date\":\"2026-10-11\"}}")
            };
        }
    }

    private sealed class PreparedEngine : ILocalTranslationEngine
    {
        internal bool Echo;
        internal TranslationModelProfile? Profile;
        internal TranslationOptions Options = new();
        internal int ContextHandlesCreated;
        internal int ContextHandlesDisposed;
        internal bool Disposed;
        internal bool BlockPreparation;
        internal int PreparationThread;
        internal Action? OnTranslate;
        internal bool BlockFirstTranslation;
        internal bool FirstCancelled;
        internal TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Translating = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        internal string LastSource = "";
        internal TaskCompletionSource Preparing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Configure(TranslationOptions options)
        {
            if (ContextHandlesCreated > 0 && (Options.GpuLayers != options.GpuLayers || QwenTranslationEngine.RequiresNewContext(Options, options)))
            {
                ContextHandlesDisposed++;
                ContextHandlesCreated++;
            }
            Options = options;
        }
        public async Task PrepareAsync(CancellationToken cancellationToken)
        {
            PreparationThread = Environment.CurrentManagedThreadId;
            Preparing.TrySetResult();
            if (BlockPreparation) await Prepared.Task.WaitAsync(cancellationToken);
        }
        public async Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (ContextHandlesCreated == 0) ContextHandlesCreated = 1;
            OnTranslate?.Invoke();
            Calls++;
            LastSource = sourceLanguage;
            Translating.TrySetResult();
            if (BlockFirstTranslation && Calls == 1)
            {
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { FirstCancelled = true; Cancelled.TrySetResult(); throw; }
            }
            if (Profile is { PromptKind: not TranslationPromptKind.General })
            {
                string prompt = TranslationModelPrompts.Build(Profile, text, sourceLanguage, targetLanguage, Options.EnableHumanizer);
                Assert.False(string.IsNullOrWhiteSpace(prompt));
                return QwenTranslationEngine.TrimStopSequence((Echo ? text : targetLanguage + " result") + "<end_of_turn>");
            }
            return Echo ? text : targetLanguage + " result";
        }
        public void ReleaseIdleResources() { }
        public void Dispose() => Disposed = true;
    }

    private sealed class ModelFixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "translation-startup-" + Guid.NewGuid());
        internal string Path => System.IO.Path.Combine(_root, "model.gguf");
        internal TranslationModelStore Store { get; }
        internal ModelFixture()
        {
            Directory.CreateDirectory(_root);
            byte[] bytes = [1, 2, 3];
            File.WriteAllBytes(Path, bytes);
            File.WriteAllText(System.IO.Path.Combine(_root, "verified.txt"), "test");
            var profile = new TranslationModelProfile("test", "test", "test", [new("model.gguf", "model.gguf", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)))]);
            Store = new TranslationModelStore(_root, profile: profile);
        }
        public void Dispose() => Directory.Delete(_root, true);
    }
}
