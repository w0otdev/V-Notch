using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SettingsActionsAndBaselineTests
{
    private const BindingFlags PrivateFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    [Fact]
    public void Reset_WhenLanguageIsNonEnglish_ResetsAllControlsWithoutPersistingOrCorruptingBaseline_AndCancelRestoresBaseline() =>
        SharedStaTestRunner.Run(() =>
        {
            LogLevel prevLogLevel = RuntimeLog.MinimumLevel;
            bool prevInspection = WindowTitleScanner.IsBrowserUrlInspectionAllowed();
            try
            {
                var initial = new NotchSettings
                {
                    Language = "vi",
                    Width = 360,
                    Height = 44,
                    IncludePrereleaseUpdates = true,
                    EnableHelloGreeting = false,
                    EnableWeather = true,
                    ManualCity = "Hanoi",
                    EnableYouTubeApi = true,
                    YouTubeApiKey = string.Concat("AIzaSy", "TestKey123456789012345678901234"),
                    ProcessPriority = "High",
                    GpuPreference = 2,
                    EnableLiveTranslation = true,
                    AutoLiveTranslation = true,
                    TranslationExcludedApps = "notepad.exe",
                    TranslationTargetLanguage = "ja",
                    AutoCheckUpdates = false,
                };

                Loc.SetLanguage("vi");
                BackgroundTestWindows.EnsureApplicationResources();
                var service = new RecordingService();
                var window = new SettingsWindow(initial, service);
                BackgroundTestWindows.ProtectInput(window);

                try
                {
                    window.Show();
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                    NotchSettings? lastPreview = null;
                    window.SettingsChanged += (_, s) => lastPreview = s.Clone();

                    bool confirmed = false;
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                    timer.Tick += (_, _) =>
                    {
                        var dialog = Application.Current.Windows.OfType<VNotch.Windows.ConfirmationDialog>().LastOrDefault();
                        if (dialog == null) return;
                        timer.Stop();
                        BackgroundTestWindows.ProtectInput(dialog);
                        confirmed = true;
                        typeof(VNotch.Windows.ConfirmationDialog).GetMethod("ConfirmButton_Click", PrivateFlags)!
                            .Invoke(dialog, [dialog, new RoutedEventArgs()]);
                    };

                    timer.Start();
                    try
                    {
                        typeof(SettingsWindow).GetMethod("Reset_Click", PrivateFlags)!
                            .Invoke(window, [window, new RoutedEventArgs()]);
                    }
                    finally
                    {
                        timer.Stop();
                    }

                    Assert.True(confirmed);

                    var defaults = new NotchSettings();
                    var resetUi = window.ReadSettingsFromUi();

                    Assert.Equal(defaults.Language, resetUi.Language);
                    Assert.Equal(defaults.Width, resetUi.Width);
                    Assert.Equal(defaults.Height, resetUi.Height);
                    Assert.Equal(defaults.IncludePrereleaseUpdates, resetUi.IncludePrereleaseUpdates);
                    Assert.Equal(defaults.EnableHelloGreeting, resetUi.EnableHelloGreeting);
                    Assert.Equal(defaults.EnableWeather, resetUi.EnableWeather);
                    Assert.Equal(defaults.ManualCity, resetUi.ManualCity);
                    Assert.Equal(defaults.EnableYouTubeApi, resetUi.EnableYouTubeApi);
                    Assert.Equal(defaults.YouTubeApiKey, resetUi.YouTubeApiKey);
                    Assert.Equal(defaults.ProcessPriority, resetUi.ProcessPriority);
                    Assert.Equal(defaults.GpuPreference, resetUi.GpuPreference);
                    Assert.Equal(defaults.EnableLiveTranslation, resetUi.EnableLiveTranslation);
                    Assert.Equal(defaults.AutoLiveTranslation, resetUi.AutoLiveTranslation);
                    Assert.Equal(defaults.TranslationExcludedApps, resetUi.TranslationExcludedApps);
                    Assert.Equal(defaults.TranslationTargetLanguage, resetUi.TranslationTargetLanguage);

                    // Reset_Click must push live preview without persisting to disk or overwriting baseline
                    Assert.NotNull(lastPreview);
                    Assert.Equal(defaults.Width, lastPreview!.Width);
                    Assert.Null(service.Applied);

                    var editSession = (SettingsEditSession)typeof(SettingsWindow).GetField("_editSession", PrivateFlags)!.GetValue(window)!;
                    Assert.Equal("vi", editSession.Baseline.Language);
                    Assert.Equal(360, editSession.Baseline.Width);

                    // Cancelling after Reset must restore the original non-English baseline
                    typeof(SettingsWindow).GetMethod("RevertLivePreviewIfNeeded", PrivateFlags)!.Invoke(window, null);

                    Assert.Equal("vi", Loc.CurrentLanguage);
                    Assert.NotNull(lastPreview);
                    Assert.Equal("vi", lastPreview!.Language);
                    Assert.Equal(360, lastPreview.Width);
                    Assert.Equal("Hanoi", lastPreview.ManualCity);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Loc.SetLanguage("en");
                RuntimeLog.MinimumLevel = prevLogLevel;
                WindowTitleScanner.UpdateInspectionAllowed(prevInspection);
                ProcessPriorityPolicy.Apply(ProcessPriorityPolicy.Normal);
            }
        });

    [Fact]
    public void ApplyImportedSettingsAsync_CoercesOutOfRangeSliderValuesViaUiBeforePersistingAndCommittingBaseline() =>
        SharedStaTestRunner.RunAsync(async ct =>
        {
            Loc.SetLanguage("en");
            BackgroundTestWindows.EnsureApplicationResources();
            var service = new RecordingService();
            var window = new SettingsWindow(new NotchSettings { AutoCheckUpdates = false, EnableWeather = false }, service);
            BackgroundTestWindows.ProtectInput(window);

            try
            {
                var imported = new NotchSettings
                {
                    Width = 9999,
                    Height = 999,
                    CornerRadius = 999,
                    AutoCheckUpdates = false,
                    EnableWeather = false,
                };

                await window.ApplyImportedSettingsAsync(imported, ct);

                int expectedMaxWidth = (int)window.WidthSlider.Maximum;
                int expectedMaxHeight = (int)window.HeightSlider.Maximum;
                int expectedMaxRadius = (int)window.RadiusSlider.Maximum;

                var uiSnapshot = window.ReadSettingsFromUi();
                Assert.Equal(expectedMaxWidth, uiSnapshot.Width);
                Assert.Equal(expectedMaxHeight, uiSnapshot.Height);
                Assert.Equal(expectedMaxRadius, uiSnapshot.CornerRadius);

                Assert.NotNull(service.Applied);
                Assert.Equal(expectedMaxWidth, service.Applied!.Width);
                Assert.Equal(expectedMaxHeight, service.Applied.Height);
                Assert.Equal(expectedMaxRadius, service.Applied.CornerRadius);

                var editSession = (SettingsEditSession)typeof(SettingsWindow).GetField("_editSession", PrivateFlags)!.GetValue(window)!;
                Assert.Equal(expectedMaxWidth, editSession.Baseline.Width);
                Assert.Equal(expectedMaxHeight, editSession.Baseline.Height);
                Assert.Equal(expectedMaxRadius, editSession.Baseline.CornerRadius);
            }
            finally
            {
                window.Close();
                Loc.SetLanguage("en");
            }
        });

    [Fact]
    public void RevertLivePreviewIfNeeded_RestoresProcessPriorityUrlInspectionLogLevelAnimationFpsAndLanguage() =>
        SharedStaTestRunner.Run(() =>
        {
            LogLevel prevLogLevel = RuntimeLog.MinimumLevel;
            bool prevInspection = WindowTitleScanner.IsBrowserUrlInspectionAllowed();
            try
            {
                var baseline = new NotchSettings
                {
                    Language = "vi",
                    ProcessPriority = ProcessPriorityPolicy.Normal,
                    EnableBrowserUrlInspection = true,
                    EnableDiagnosticLogging = false,
                    AnimationFps = 120,
                    AutoAnimationFps = false,
                    AutoCheckUpdates = false,
                    EnableWeather = false,
                };

                Loc.SetLanguage("vi");
                WindowTitleScanner.UpdateInspectionAllowed(true);
                RuntimeLog.MinimumLevel = LogLevel.Info;
                AnimationConfig.Configure(120, false);
                ProcessPriorityPolicy.Apply(ProcessPriorityPolicy.Normal);

                BackgroundTestWindows.EnsureApplicationResources();
                var service = new RecordingService();
                var window = new SettingsWindow(baseline, service);
                BackgroundTestWindows.ProtectInput(window);

                try
                {
                    // Simulate unapplied live preview mutations across subsystems
                    Loc.SetLanguage("en");
                    ProcessPriorityPolicy.Apply(ProcessPriorityPolicy.High);
                    WindowTitleScanner.UpdateInspectionAllowed(false);
                    RuntimeLog.MinimumLevel = LogLevel.Debug;
                    AnimationConfig.Configure(60, false);

                    var mutated = baseline.Clone();
                    mutated.Language = "en";
                    mutated.ProcessPriority = ProcessPriorityPolicy.High;
                    mutated.EnableBrowserUrlInspection = false;
                    mutated.EnableDiagnosticLogging = true;
                    mutated.AnimationFps = 60;
                    window.ApplyPreview(mutated);

                    Assert.Equal("en", Loc.CurrentLanguage);
                    Assert.False(WindowTitleScanner.IsBrowserUrlInspectionAllowed());
                    Assert.Equal(LogLevel.Debug, RuntimeLog.MinimumLevel);
                    Assert.Equal(60, AnimationConfig.TargetFps);

                    typeof(SettingsWindow).GetMethod("RevertLivePreviewIfNeeded", PrivateFlags)!.Invoke(window, null);

                    Assert.Equal("vi", Loc.CurrentLanguage);
                    Assert.True(WindowTitleScanner.IsBrowserUrlInspectionAllowed());
                    Assert.Equal(LogLevel.Info, RuntimeLog.MinimumLevel);
                    Assert.Equal(120, AnimationConfig.TargetFps);
                    Assert.Equal(ProcessPriorityClass.Normal, Process.GetCurrentProcess().PriorityClass);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Loc.SetLanguage("en");
                RuntimeLog.MinimumLevel = prevLogLevel;
                WindowTitleScanner.UpdateInspectionAllowed(prevInspection);
                AnimationConfig.Configure(AnimationConfig.MaxFps, true);
                ProcessPriorityPolicy.Apply(ProcessPriorityPolicy.Normal);
            }
        });

    private sealed class RecordingService : ISettingsApplicationService
    {
        internal NotchSettings? Applied { get; private set; }
        public NotchSettings Load() => new();
        public Task ApplyAsync(NotchSettings settings, CancellationToken ct = default)
        {
            Applied = settings.Clone();
            SettingsApplied?.Invoke(this, Applied);
            return Task.CompletedTask;
        }
        public Task<(NotchSettings Settings, bool RequiresRestart)> ImportAsync(string filePath, NotchSettings? currentSettings = null, CancellationToken ct = default) =>
            Task.FromResult((new NotchSettings(), false));
        public void Export(string filePath, NotchSettings settings) { }
        public bool IsAutoStartEnabled() => false;
        public event EventHandler<NotchSettings>? SettingsApplied;
    }
}
