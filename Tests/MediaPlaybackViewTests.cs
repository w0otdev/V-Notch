using System.Windows.Automation;
using System.Windows;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Tests.Fakes;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MediaPlaybackViewTests
{
    [Fact]
    public void HidingCancelsRewindAndItsAnimationClock() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var view = new MediaPlaybackView();
        var host = new Window { Content = view, Width = 400, Height = 200, ShowInTaskbar = false };
        try
        {
            host.Show();
            await Task.Delay(50, ct);
            view.CurrentTimeText.Text = "1:00";
            view.AnimateTrackChangeRewindToZero(0.5, TimeSpan.FromMinutes(2));
            Assert.True(view.ProgressBarScale.HasAnimatedProperties);
            view.Visibility = Visibility.Collapsed;
            Assert.False(view.ProgressBarScale.HasAnimatedProperties);
            Assert.Equal(0, view.DisplayRatio);
            view.Visibility = Visibility.Visible;
            await Task.Delay(500, ct);
            Assert.False(view.ProgressBarScale.HasAnimatedProperties);
        }
        finally { host.Close(); }
    });

    [Fact]
    public void TransportWorksWithoutMainWindowOrTheApplicationServiceLocator() => SharedStaTestRunner.RunAsync(async ct =>
    {
        Loc.SetLanguage("en");
        var media = new FakeMediaDetectionService();
        var viewModel = new MediaViewModel(media, _ => Task.CompletedTask);
        using var view = new MediaPlaybackView();
        view.Attach(viewModel, media);
        view.PlayPauseButton.InvokeAction();
        Assert.Equal(1, media.PlayPauseCount);
        Assert.Equal(Loc.Get("media.play"), AutomationProperties.GetName(view.PlayPauseButton));
        await Task.Delay(300, ct);
        view.NextButton.InvokeAction();
        Assert.Equal(1, media.NextTrackCount);
        await Task.Delay(300, ct);
        view.PrevButton.InvokeAction();
        Assert.Equal(1, media.PreviousTrackCount);
        view.Dispose();
        await Task.Delay(300, ct);
        view.PlayPauseButton.InvokeAction();
        Assert.Equal(1, media.PlayPauseCount);
    });
}
