using System.Windows;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SpotlightIntroTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpensDirectlyToCompactSearchRegardlessOfIntroHistory(bool hasSeenIntro) => SharedStaTestRunner.Run(() =>
    {
        bool reducedMotion = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(true);
            var settings = new NotchSettings { HasSeenSpotlightIntro = hasSeenIntro };
            using var fixture = new SpotlightWindowFixture(settings);
            var window = fixture.Window;
            window.ShowSpotlight();
            AssertCompactSearch(window);
            window.HideSpotlight();
            settings.HasSeenSpotlightIntro = !hasSeenIntro;
            settings.Language = "vi";
            window.ApplySettings(settings);
            window.ShowSpotlight();
            AssertCompactSearch(window);
        }
        finally { AnimationConfig.SetReduceMotion(reducedMotion); }
    });

    private static void AssertCompactSearch(SpotlightWindow window)
    {
        window.UpdateLayout();
        Assert.Null(window.FindName("SpotlightIntroPanel"));
        Assert.Equal(Visibility.Collapsed, window.ContentRegion.Visibility);
        Assert.Empty(window.SearchBox.Text);
        Assert.True(window.SearchBox.IsEnabled);
        Assert.InRange(window.Shell.ActualHeight, 64, 66);
    }
}
