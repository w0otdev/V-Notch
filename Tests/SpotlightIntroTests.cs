using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SpotlightIntroTests
{
    [Theory]
    [InlineData("default", false, false)]
    [InlineData("default", true, false)]
    [InlineData("liquidglass", false, false)]
    [InlineData("liquidglass", true, false)]
    [InlineData("default", false, true)]
    [InlineData("liquidglass", false, true)]
    public void FirstOpenAndReopenHaveTheSameCompactHeight(string style, bool reduceMotion, bool preloadResults) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(reduceMotion);
            var settings = new NotchSettings { NotchStyle = style };
            using var fixture = new SpotlightWindowFixture(new NotchSettings(), saveChatHistory: true);
            var window = fixture.Window;
            window.ApplySettings(settings);
            if (preloadResults)
            {
                // A hidden window can receive results before its first entrance.
                var viewModel = (SpotlightViewModel)window.DataContext;
                viewModel.Results.Add(new SpotlightSearchItem("stale", SpotlightResultKind.Application, "Stale result", "", "stale.exe"));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                Assert.Equal(Visibility.Visible, window.ContentRegion.Visibility);
            }
            var entranceActive = typeof(SpotlightWindow).GetField("_entranceActive", BindingFlags.Instance | BindingFlags.NonPublic)!;
            window.ShowSpotlight();
            await WpfFrameWaiter.UntilAsync(() => !(bool)entranceActive.GetValue(window)!, "first Spotlight entrance", ct);
            AssertCompactSearch(window);
            double firstHeight = window.Shell.ActualHeight;

            window.HideSpotlight();
            await WpfFrameWaiter.UntilAsync(() => !window.IsSpotlightOpen, "Spotlight closed", ct);
            window.ShowSpotlight();
            await WpfFrameWaiter.UntilAsync(() => !(bool)entranceActive.GetValue(window)!, "Spotlight reopened", ct);
            AssertCompactSearch(window);
            Assert.Equal(firstHeight, window.Shell.ActualHeight, precision: 3);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

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
