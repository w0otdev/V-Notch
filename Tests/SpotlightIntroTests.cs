using System.Windows;
using System.Windows.Controls;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SpotlightIntroTests
{
    [Fact]
    public void IntroRemainsUntilAcknowledgedAndDoesNotReturnOnStaleSettings() => SharedStaTestRunner.Run(() =>
    {
        Loc.SetLanguage("en");
        var settings = new NotchSettings { HasSeenSpotlightIntro = false };
        using var fixture = new SpotlightWindowFixture(settings);
        var window = fixture.Window;
        window.ShowSpotlight();
        var panel = Assert.IsType<Border>(window.FindName("SpotlightIntroPanel"));
        Assert.Equal(Visibility.Visible, panel.Visibility);
        Assert.Equal(Loc.Get("intro.body"), Assert.IsType<TextBlock>(window.FindName("SpotlightIntroBody")).Text);
        window.HideSpotlight();
        window.ShowSpotlight();
        Assert.Equal(Visibility.Visible, panel.Visibility);
        Assert.IsType<Button>(window.FindName("SpotlightIntroClose")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(Visibility.Collapsed, panel.Visibility);
        window.ApplySettings(settings); // A previously-open settings editor still holds the old flag.
        window.HideSpotlight();
        window.ShowSpotlight();
        Assert.Equal(Visibility.Collapsed, panel.Visibility);
    });

    [Fact]
    public void PreviouslyAcknowledgedIntroStaysHidden() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new SpotlightWindowFixture(new NotchSettings { HasSeenSpotlightIntro = true });
        fixture.Window.ShowSpotlight();
        Assert.Equal(Visibility.Collapsed, Assert.IsType<Border>(fixture.Window.FindName("SpotlightIntroPanel")).Visibility);
    });

    [Fact]
    public void AcknowledgingIntroPreservesSettingsBaselineAndUncommittedPreview()
    {
        var baseline = new NotchSettings { Width = 230 };
        var session = new SettingsEditSession(baseline);
        var preview = baseline.Clone();
        preview.Width = 350;
        session.Preview(preview);
        session.CommitSpotlightIntroSeen();
        Assert.True(session.Baseline.HasSeenSpotlightIntro);
        Assert.True(session.Applied.HasSeenSpotlightIntro);
        Assert.Equal(230, session.Baseline.Width);
        Assert.Equal(350, session.Applied.Width);
        Assert.True(session.Revert().HasSeenSpotlightIntro);
    }
}
