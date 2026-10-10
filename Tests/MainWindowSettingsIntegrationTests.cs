using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowSettingsIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SettingsPreviewUpdatesTheOwnerAndCancelRestoresItsOriginalConfiguration(bool island) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            s => { s.EnableLocalOnlyMode = true; s.DisableMouseLeaveAutoClose = true; },
            services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        window.Show();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var field = typeof(MainWindow).GetField("_settings", flags)!;
        var original = ((NotchSettings)field.GetValue(window)!).Clone();
        Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            var settings = Application.Current.Windows.OfType<SettingsWindow>().LastOrDefault();
            if (settings == null) return;
            timer.Stop();
            BackgroundTestWindows.ProtectInput(settings);
            try
            {
                var changed = original.Clone();
                changed.Width = 340;
                changed.Height = 48;
                changed.CornerRadius = 14;
                changed.EnableDynamicIslandMode = island;
                changed.DynamicIslandWidth = 260;
                changed.DynamicIslandHeight = 48;
                changed.EnableBlurEffects = !original.EnableBlurEffects;
                changed.ExpandedWidget = "none";
                changed.ClockPageStyle = "digital";
                changed.HideCamera = true;
                changed.Language = "vi";
                changed.HoverExpandDelay = 500;
                changed.HoverCollapseDelay = 800;
                changed.EnableIdleAutoHide = true;
                changed.IdleAutoHideDelay = 10000;
                changed.IgnoreYouTubeAutoSubtitles = !original.IgnoreYouTubeAutoSubtitles;
                changed.SubtitlePriority = "en,vi";
                changed.ManualCity = "Test city";
                changed.ShowBatteryIndicator = false;
                Assert.True(settings.ApplyPreview(changed));
                var applied = (NotchSettings)field.GetValue(window)!;
                Assert.Equal(340, applied.Width);
                Assert.Equal(island, applied.EnableDynamicIslandMode);
                Assert.Equal("vi", applied.Language);
                Assert.Equal(Visibility.Visible, window.ClockViewDigitalClock.Visibility);
                Assert.False(applied.ShowBatteryIndicator);
                typeof(SettingsWindow).GetMethod("RevertLivePreviewIfNeeded", flags)!.Invoke(settings, null);
                Assert.True(original.ValueEquals((NotchSettings)field.GetValue(window)!));
            }
            catch (Exception ex) { failure = ex; }
            finally { settings.Close(); }
        };
        timer.Start();
        try { typeof(MainWindow).GetMethod("OpenAppSettings", flags)!.Invoke(window, [false]); }
        finally { timer.Stop(); }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    });
}
