using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class TranslationStartupTests
{
    [Fact]
    public void RepeatedManualRequestsAndEmptyKeyUpSnapshotsKeepTheExistingPopupAndResult() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new ModelFixture();
        using var engine = new PreparedEngine();
        using var controller = new LiveTranslationController(Dispatcher.CurrentDispatcher, () => { }, fixture.Store, engine);
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
        var foreground = VNotch.Services.Win32Interop.GetForegroundWindow();
        var selection = new TranslationSelection(1, "hello", new System.Windows.Rect(100, 100, 100, 20), foreground, false);
        var present = typeof(LiveTranslationController).GetMethod("PresentSelection", BindingFlags.NonPublic | BindingFlags.Instance)!;
        present.Invoke(controller, [selection, true, null]);
        await WpfFrameWaiter.UntilAsync(() => window.ResultText.Text == "vi result", "manual translation settles", ct);
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
    public void TranslationStartsOnlyAfterTheEntranceFinishes(bool reduced) => SharedStaTestRunner.RunAsync(async ct =>
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
        bool startedDuringAnimation = false;
        engine.OnTranslate = () => startedDuringAnimation = window.IsPresentationAnimating;
        try
        {
            typeof(LiveTranslationController).GetField("_text", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(controller, "hello");
            window.ShowSelection(new(1, "hello", new System.Windows.Rect(100, 100, 100, 20), IntPtr.Zero, false), true);
            window.TranslateButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.True(window.IsPresentationAnimating);
            Assert.Equal(0, engine.Calls);
            Assert.False(engine.Preparing.Task.IsCompleted);
            await WpfFrameWaiter.UntilAsync(() => window.ResultText.Text == "vi result", "translation after entrance", ct);
            Assert.False(startedDuringAnimation);
            Assert.False(window.IsPresentationAnimating);
            Assert.Equal(1, engine.Calls);
        }
        finally { VNotch.Services.AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData("cancel")]
    [InlineData("dismiss")]
    [InlineData("close")]
    public void CancellingDuringEntranceDoesNotStartModelWork(string action) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new ModelFixture();
        using var engine = new PreparedEngine();
        using var controller = new LiveTranslationController(Dispatcher.CurrentDispatcher, () => { }, fixture.Store, engine);
        var window = (TranslationWindow)typeof(LiveTranslationController).GetMethod("EnsureWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(controller, null)!;
        BackgroundTestWindows.ProtectInput(window);
        window.Opacity = 0;
        window.ShowActivated = false;
        typeof(LiveTranslationController).GetField("_text", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(controller, "hello");
        window.ShowSelection(new(1, "hello", new System.Windows.Rect(100, 100, 100, 20), IntPtr.Zero, false), true);
        window.TranslateButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert.True(window.IsPresentationAnimating);
        if (action == "cancel") window.CancelButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        else if (action == "dismiss") window.Dismiss();
        else window.Close();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (action != "close") await WpfFrameWaiter.UntilAsync(() => !window.IsPresentationAnimating, "cancelled entrance settles", ct);
        Assert.Equal(0, engine.Calls);
        Assert.False(engine.Preparing.Task.IsCompleted);
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
        cancellation.Cancel();
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
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.VerifyAsync(cancellation.Token));
    }

    private sealed class PreparedEngine : ILocalTranslationEngine
    {
        internal bool BlockPreparation;
        internal Action? OnTranslate;
        internal bool BlockFirstTranslation;
        internal bool FirstCancelled;
        internal TaskCompletionSource Translating = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        internal string LastSource = "";
        internal TaskCompletionSource Preparing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task PrepareAsync(CancellationToken ct)
        {
            Preparing.TrySetResult();
            if (BlockPreparation) await Prepared.Task.WaitAsync(ct);
        }
        public async Task<string> TranslateAsync(string text, string source, string target, CancellationToken ct)
        {
            OnTranslate?.Invoke();
            Calls++;
            LastSource = source;
            if (BlockFirstTranslation && Calls == 1)
            {
                Translating.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { FirstCancelled = true; throw; }
            }
            return target + " result";
        }
        public void ReleaseIdleResources() { }
        public void Dispose() { }
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
