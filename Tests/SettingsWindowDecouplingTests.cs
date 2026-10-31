using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using VNotch.Models;
using VNotch.Services;
using VNotch.Windows;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public class SettingsWindowDecouplingTests
{
    private static void EnsureApplicationResources()
    {
        BackgroundTestWindows.EnsureApplicationResources();
    }

    [Fact]
    public void NotchSettings_ValueEquals_IdenticalOrCloned_ReturnsTrue()
    {
        var s1 = new NotchSettings { Width = 300, Language = "en", Opacity = 0.85 };
        var s2 = s1.Clone();

        Assert.True(s1.ValueEquals(s2));
        Assert.True(s2.ValueEquals(s1));
    }

    [Theory]
    [InlineData(1)] // Int difference
    [InlineData(2)] // Double difference
    [InlineData(3)] // String difference
    [InlineData(4)] // Bool difference
    [InlineData(5)] // LiquidGlass difference
    public void NotchSettings_ValueEquals_DifferentValues_ReturnsFalse(int diffType)
    {
        var s1 = new NotchSettings();
        var s2 = s1.Clone();

        switch (diffType)
        {
            case 1:
                s2.Width += 50;
                break;
            case 2:
                s2.Opacity = 0.5;
                break;
            case 3:
                s2.Language = "vi";
                break;
            case 4:
                s2.EnableBlurEffects = !s1.EnableBlurEffects;
                break;
            case 5:
                s2.LiquidGlass.BlurAmount += 0.5;
                break;
        }

        Assert.False(s1.ValueEquals(s2));
    }

    [Fact]
    public void SettingsWindow_ReadSettingsFromUi_ProducesSnapshotWithoutSideEffects()
    {
        SharedStaTestRunner.Run(() =>
        {
            EnsureApplicationResources();
            var fakeService = new FakeSettingsAppService();
            var initialSettings = new NotchSettings { Width = 230, Height = 34 };
            var window = new SettingsWindow(initialSettings, fakeService);
            try
            {
                bool settingsChangedFired = false;
                window.SettingsChanged += (_, _) => settingsChangedFired = true;

                // Change UI sliders
                window.WidthSlider.Value = 350;
                window.HeightSlider.Value = 48;

                // Reading settings from UI should create snapshot without side effects
                var snapshot = window.ReadSettingsFromUi();

                Assert.Equal(350, snapshot.Width);
                Assert.Equal(48, snapshot.Height);
                Assert.False(settingsChangedFired, "ReadSettingsFromUi must not fire SettingsChanged.");
                Assert.Equal(0, fakeService.ExportCount);
                Assert.Equal(0, fakeService.ApplyCount);
            }
            finally { window.Close(); }
            if (Application.Current != null) Assert.DoesNotContain(window, Application.Current.Windows.OfType<SettingsWindow>());
        });
    }

    [Fact]
    public void InteractionSoundToggleLoadsAndParticipatesInPreviewAndCancel()
    {
        SharedStaTestRunner.Run(() =>
        {
            EnsureApplicationResources();
            var initial = new NotchSettings { EnableInteractionSounds = false, InteractionSoundVolume = .35 };
            var window = new SettingsWindow(initial, new FakeSettingsAppService());
            try
            {
                Assert.False(window.InteractionSoundsCheck.IsChecked);
                Assert.Equal(35, window.InteractionSoundVolumeSlider.Value);
                Assert.False(window.InteractionSoundVolumeSlider.IsEnabled);
                window.InteractionSoundsCheck.IsChecked = true;
                Assert.True(window.InteractionSoundVolumeSlider.IsEnabled);
                window.InteractionSoundVolumeSlider.Value = 70;
                var snapshot = window.ReadSettingsFromUi();
                Assert.True(snapshot.EnableInteractionSounds);
                Assert.Equal(.7, snapshot.InteractionSoundVolume);
                Assert.False(initial.EnableInteractionSounds);
                NotchSettings? received = null;
                window.SettingsChanged += (_, settings) => received = settings;
                window.ApplyPreview(snapshot);
                Assert.True(received!.EnableInteractionSounds);
                typeof(SettingsWindow).GetMethod("RevertLivePreviewIfNeeded", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                Assert.False(received.EnableInteractionSounds);
                Assert.Equal(.35, received.InteractionSoundVolume);
            }
            finally { window.Close(); }
            if (Application.Current != null) Assert.DoesNotContain(window, Application.Current.Windows.OfType<SettingsWindow>());
        });
    }

    [Fact]
    public void SettingsWindow_ApplyPreview_WhenValuesUnchanged_DoesNotFireSettingsChanged()
    {
        SharedStaTestRunner.Run(() =>
        {
            EnsureApplicationResources();
            var fakeService = new FakeSettingsAppService();
            var initialSettings = new NotchSettings { Width = 230, Height = 34 };
            var window = new SettingsWindow(initialSettings, fakeService);
            try
            {
                // Snapshot of unchanged controls
                var snapshot = window.ReadSettingsFromUi();

                bool settingsChangedFired = false;
                window.SettingsChanged += (_, _) => settingsChangedFired = true;

                bool applied = window.ApplyPreview(snapshot);

                Assert.False(applied);
                Assert.False(settingsChangedFired, "ApplyPreview must not fire SettingsChanged when there are no changes.");
            }
            finally { window.Close(); }
            if (Application.Current != null) Assert.DoesNotContain(window, Application.Current.Windows.OfType<SettingsWindow>());
        });
    }

    [Fact]
    public void SettingsWindow_ApplyPreview_WhenValuesChanged_AppliesAndFiresSettingsChanged()
    {
        SharedStaTestRunner.Run(() =>
        {
            EnsureApplicationResources();
            var fakeService = new FakeSettingsAppService();
            var initialSettings = new NotchSettings { Width = 230, Height = 34 };
            var window = new SettingsWindow(initialSettings, fakeService);
            try
            {
                window.WidthSlider.Value = 320;
                var snapshot = window.ReadSettingsFromUi();

                NotchSettings? emittedSettings = null;
                window.SettingsChanged += (_, s) => emittedSettings = s;

                bool applied = window.ApplyPreview(snapshot);

                Assert.True(applied);
                Assert.NotNull(emittedSettings);
                Assert.Equal(320, emittedSettings.Width);
            }
            finally { window.Close(); }
            if (Application.Current != null) Assert.DoesNotContain(window, Application.Current.Windows.OfType<SettingsWindow>());
        });
    }

    [Fact]
    public Task SettingsWindow_SaveAsync_PersistsThroughAppService()
    {
        SharedStaTestRunner.RunAsync(async () =>
        {
            EnsureApplicationResources();
            var fakeService = new FakeSettingsAppService();
            var initialSettings = new NotchSettings { Width = 230 };
            var window = new SettingsWindow(initialSettings, fakeService);
            try
            {
                window.WidthSlider.Value = 380;
                var snapshot = window.ReadSettingsFromUi();

                await window.SaveAsync(snapshot);

                Assert.Equal(1, fakeService.ApplyCount);
                Assert.NotNull(fakeService.AppliedSettings);
                Assert.Equal(380, fakeService.AppliedSettings.Width);
            }
            finally { window.Close(); }
            if (Application.Current != null) Assert.DoesNotContain(window, Application.Current.Windows.OfType<SettingsWindow>());
        });
        return Task.CompletedTask;
    }

    [Fact]
    public void CanvasOptIn_DeclineAcceptAndRevokeUpdateBothSettingsControls()
    {
        SharedStaTestRunner.Run(() =>
        {
            EnsureApplicationResources();
            var window = new SettingsWindow(new NotchSettings(), new FakeSettingsAppService());
            try
            {
                var policy = new NetworkPrivacy();
                policy.Apply(window.ReadSettingsFromUi());
                window.SettingsChanged += (_, settings) => policy.Apply(settings);
                int prompts = 0;
                Assert.False(window.SetSpotifyCanvasOptIn(true, () => { prompts++; return false; }));
                Assert.False(window.EnableSpotifyCanvasCheck.IsChecked);
                Assert.False(NetworkPrivacy.Allows(window.ReadSettingsFromUi(), NetworkFeature.Canvas));
                Assert.True(window.SetSpotifyCanvasOptIn(true, () => { prompts++; return true; }));
                var enabled = window.ReadSettingsFromUi();
                Assert.True(enabled.EnableSpotifyCanvas);
                Assert.True(enabled.AllowOnlineCanvas);
                Assert.True(NetworkPrivacy.Allows(enabled, NetworkFeature.Canvas));
                Assert.True(policy.IsAllowed(NetworkFeature.Canvas));
                var permission = policy.Acquire(NetworkFeature.Canvas);
                Assert.True(window.SetSpotifyCanvasOptIn(true, () => throw new Exception("Already accepted")));
                Assert.False(window.SetSpotifyCanvasOptIn(false, () => throw new Exception("Revocation must not prompt")));
                var revoked = window.ReadSettingsFromUi();
                Assert.False(revoked.EnableSpotifyCanvas);
                Assert.False(revoked.AllowOnlineCanvas);
                Assert.Equal(0, revoked.SpotifyCanvasConsentVersion);
                Assert.True(permission.IsCancellationRequested);
                Assert.False(policy.IsAllowed(NetworkFeature.Canvas));
                Assert.False(window.SetSpotifyCanvasOptIn(true, () => { prompts++; return false; }));
                Assert.Equal(3, prompts);
            }
            finally { window.Close(); }
            if (Application.Current != null) Assert.DoesNotContain(window, Application.Current.Windows.OfType<SettingsWindow>());
        });
    }

    [Fact]
    public void SavingKeepsTheWindowOpenUntilPersistenceCompletesAndCoalescesRepeatedClicks() => SharedStaTestRunner.RunAsync(async ct =>
    {
        EnsureApplicationResources();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeSettingsAppService { CompleteApply = () => complete.Task };
        var window = new SettingsWindow(new NotchSettings(), service) { Left = 0, Top = 0 };
        try
        {
            var pending = window.ApplyAndCloseAsync(ex => throw new Xunit.Sdk.XunitException(ex.ToString()));
            Assert.False(pending.IsCompleted);
            Assert.False((bool)typeof(SettingsWindow).GetField("_isClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
            Assert.False(await window.ApplyAndCloseAsync());
            Assert.Equal(1, service.ApplyCount);
            complete.SetResult();
            Assert.True(await pending);
            Assert.True((bool)typeof(SettingsWindow).GetField("_isClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
        }
        finally { complete.TrySetResult(); window.Close(); }
        if (Application.Current != null) Assert.DoesNotContain(window, Application.Current.Windows.OfType<SettingsWindow>());
        ct.ThrowIfCancellationRequested();
    });

    [Fact]
    public void FeatureSwitchesDimAndDisableTheirChildrenWithoutChangingValues() => SharedStaTestRunner.RunAsync(async ct =>
    {
        EnsureApplicationResources();
        var window = new SettingsWindow(new NotchSettings { EnableLiveTranslation = false, EnableSpotlight = false }, new FakeSettingsAppService());
        try
        {
            BackgroundTestWindows.ProtectInput(window);
            window.Show();
            await WpfFrameWaiter.UntilAsync(() => window.TranslationOptions.Opacity == 0.45,
                "disabled feature group loaded", ct);
            Assert.False(window.TranslationOptions.IsEnabled);
            Assert.Equal(0.45, window.TranslationOptions.Opacity);
            Assert.Equal(1, window.CurrencyOptions.Opacity); // The parent supplies dimming once.
            Assert.False(window.TranslationSourceCombo.IsEnabled);
            Assert.False(window.TranslationTargetCombo.IsEnabled);
            Assert.False(window.SpotlightOptions.IsEnabled);
            Assert.Equal(0.45, window.SpotlightOptions.Opacity);
            Assert.False(window.SpotlightAiProviderCombo.IsEnabled);
            Assert.True(window.EnableTranslationCheck.IsEnabled);
            Assert.True(window.EnableSpotlightCheck.IsEnabled);
            var language = window.TranslationTargetCombo.SelectedValue;
            window.EnableTranslationCheck.IsChecked = true;
            window.EnableSpotlightCheck.IsChecked = true;
            await WpfFrameWaiter.NextAsync(ct);
            Assert.True(window.TranslationOptions.IsEnabled);
            Assert.Equal(1, window.TranslationOptions.Opacity);
            Assert.True(window.TranslationTargetCombo.IsEnabled);
            Assert.True(window.SpotlightAiProviderCombo.IsEnabled);
            Assert.Equal(language, window.TranslationTargetCombo.SelectedValue);
            window.CurrencyConversionCheck.IsChecked = false;
            await WpfFrameWaiter.NextAsync(ct);
            Assert.False(window.TranslationCurrencyCombo.IsEnabled);
            Assert.Equal(0.45, window.CurrencyOptions.Opacity);
            window.CurrencyConversionCheck.IsChecked = true;
            await WpfFrameWaiter.NextAsync(ct);
            Assert.True(window.TranslationCurrencyCombo.IsEnabled);
            Assert.Equal(1, window.CurrencyOptions.Opacity);
            window.TrayTextCheck.IsChecked = window.TrayImagesCheck.IsChecked = window.TrayFilesCheck.IsChecked = false;
            Assert.False(window.TrayDelaySlider.IsEnabled);
            Assert.Equal(0.4, window.TrayDelayRow.Opacity);
            window.TrayFilesCheck.IsChecked = true;
            Assert.True(window.TrayDelaySlider.IsEnabled);
            Assert.Equal(1, window.TrayDelayRow.Opacity);
        }
        finally { window.Close(); }
        if (Application.Current != null) Assert.DoesNotContain(window, Application.Current.Windows.OfType<SettingsWindow>());
    });

    [Fact]
    public void ApplyButtonSavesWithoutClosingAndCancelKeepsTheAppliedBaseline() => SharedStaTestRunner.RunAsync(async ct =>
    {
        EnsureApplicationResources();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeSettingsAppService { CompleteApply = () => complete.Task };
        var window = new SettingsWindow(new NotchSettings(), service) { Left = 0, Top = 0 };
        try
        {
            window.WidthSlider.Value = 320;
            window.ApplyButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(1, service.ApplyCount);
            window.ApplyButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(1, service.ApplyCount);
            complete.SetResult();
            await WpfFrameWaiter.UntilAsync(() => !(bool)typeof(SettingsWindow).GetField("_isSaving", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!,
                "Apply finishes saving", ct);
            Assert.Equal(320, service.AppliedSettings!.Width);
            Assert.False((bool)typeof(SettingsWindow).GetField("_isClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
            window.WidthSlider.Value = 400;
            window.ApplyPreview(window.ReadSettingsFromUi());
            NotchSettings? reverted = null;
            window.SettingsChanged += (_, settings) => reverted = settings;
            typeof(SettingsWindow).GetMethod("RevertLivePreviewIfNeeded", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            Assert.Equal(320, reverted!.Width);
        }
        finally { complete.TrySetResult(); window.Close(); }
        if (Application.Current != null) Assert.DoesNotContain(window, Application.Current.Windows.OfType<SettingsWindow>());
    });

    [Fact]
    public void SaveButtonStillSavesAndCloses() => SharedStaTestRunner.Run(() =>
    {
        EnsureApplicationResources();
        var service = new FakeSettingsAppService();
        var window = new SettingsWindow(new NotchSettings(), service) { Left = 0, Top = 0 };
        try
        {
            window.WidthSlider.Value = 360;
            window.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(360, service.AppliedSettings!.Width);
            Assert.True((bool)typeof(SettingsWindow).GetField("_isClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
        }
        finally { window.Close(); }
        if (Application.Current != null) Assert.DoesNotContain(window, Application.Current.Windows.OfType<SettingsWindow>());
    });

    [Fact]
    public void FailedSaveKeepsTheWindowOpenAndAllowsRetry() => SharedStaTestRunner.RunAsync(async ct =>
    {
        EnsureApplicationResources();
        bool fail = true;
        var failure = new System.IO.IOException("Fixture disk full");
        var service = new FakeSettingsAppService { CompleteApply = () => fail ? Task.FromException(failure) : Task.CompletedTask };
        var window = new SettingsWindow(new NotchSettings(), service) { Left = 0, Top = 0 };
        try
        {
            Exception? reported = null;
            Assert.False(await window.ApplyAndCloseAsync(ex => reported = ex));
            Assert.Same(failure, reported);
            Assert.False((bool)typeof(SettingsWindow).GetField("_isClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
            Assert.Null(service.AppliedSettings);
            fail = false;
            Assert.True(await window.ApplyAndCloseAsync(ex => throw new Xunit.Sdk.XunitException(ex.ToString())));
            Assert.Equal(2, service.ApplyCount);
        }
        finally { window.Close(); }
        if (Application.Current != null) Assert.DoesNotContain(window, Application.Current.Windows.OfType<SettingsWindow>());
        ct.ThrowIfCancellationRequested();
    });

    private sealed class FakeSettingsAppService : ISettingsApplicationService
    {
        public NotchSettings? ExportedSettings { get; private set; }
        public NotchSettings? AppliedSettings { get; private set; }
        public int ExportCount { get; private set; }
        public int ApplyCount { get; private set; }
        public Func<Task>? CompleteApply { get; init; }

        public NotchSettings Load() => new();

        public async Task ApplyAsync(NotchSettings settings, CancellationToken ct = default)
        {
            ApplyCount++;
            if (CompleteApply != null) await CompleteApply();
            AppliedSettings = settings;
            SettingsApplied?.Invoke(this, settings);
        }

        public Task<(NotchSettings Settings, bool RequiresRestart)> ImportAsync(string filePath, NotchSettings? currentSettings = null, CancellationToken ct = default)
            => Task.FromResult((new NotchSettings(), false));

        public void Export(string filePath, NotchSettings settings)
        {
            ExportCount++;
            ExportedSettings = settings;
        }

        public bool IsAutoStartEnabled() => false;

        public event EventHandler<NotchSettings>? SettingsApplied;
    }
}
