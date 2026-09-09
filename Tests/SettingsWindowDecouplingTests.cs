using System;
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

public class SettingsWindowDecouplingTests
{
    private static void EnsureApplicationResources()
    {
        if (Application.Current == null)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources["SFProDisplay"] = new FontFamily("Segoe UI");
            app.Resources["SFProText"] = new FontFamily("Segoe UI");
            app.Resources["IconFont"] = new FontFamily("Segoe MDL2 Assets");
        }
        else
        {
            if (!Application.Current.Resources.Contains("SFProDisplay"))
                Application.Current.Resources["SFProDisplay"] = new FontFamily("Segoe UI");
            if (!Application.Current.Resources.Contains("SFProText"))
                Application.Current.Resources["SFProText"] = new FontFamily("Segoe UI");
            if (!Application.Current.Resources.Contains("IconFont"))
                Application.Current.Resources["IconFont"] = new FontFamily("Segoe MDL2 Assets");
        }
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

            // Snapshot of unchanged controls
            var snapshot = window.ReadSettingsFromUi();

            bool settingsChangedFired = false;
            window.SettingsChanged += (_, _) => settingsChangedFired = true;

            bool applied = window.ApplyPreview(snapshot);

            Assert.False(applied);
            Assert.False(settingsChangedFired, "ApplyPreview must not fire SettingsChanged when there are no changes.");
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

            window.WidthSlider.Value = 320;
            var snapshot = window.ReadSettingsFromUi();

            NotchSettings? emittedSettings = null;
            window.SettingsChanged += (_, s) => emittedSettings = s;

            bool applied = window.ApplyPreview(snapshot);

            Assert.True(applied);
            Assert.NotNull(emittedSettings);
            Assert.Equal(320, emittedSettings.Width);
        });
    }

    [Fact]
    public async Task SettingsWindow_SaveAsync_PersistsThroughAppService()
    {
        FakeSettingsAppService? fakeService = null;
        SettingsWindow? window = null;
        NotchSettings? snapshot = null;

        SharedStaTestRunner.Run(() =>
        {
            EnsureApplicationResources();
            fakeService = new FakeSettingsAppService();
            var initialSettings = new NotchSettings { Width = 230 };
            window = new SettingsWindow(initialSettings, fakeService);

            window.WidthSlider.Value = 380;
            snapshot = window.ReadSettingsFromUi();
        });

        Assert.NotNull(window);
        Assert.NotNull(fakeService);
        Assert.NotNull(snapshot);

        await window.SaveAsync(snapshot);

        Assert.Equal(1, fakeService.ApplyCount);
        Assert.NotNull(fakeService.AppliedSettings);
        Assert.Equal(380, fakeService.AppliedSettings.Width);
    }

    private sealed class FakeSettingsAppService : ISettingsApplicationService
    {
        public NotchSettings? ExportedSettings { get; private set; }
        public NotchSettings? AppliedSettings { get; private set; }
        public int ExportCount { get; private set; }
        public int ApplyCount { get; private set; }

        public NotchSettings Load() => new();

        public Task ApplyAsync(NotchSettings settings, CancellationToken ct = default)
        {
            ApplyCount++;
            AppliedSettings = settings;
            SettingsApplied?.Invoke(this, settings);
            return Task.CompletedTask;
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
