using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using VNotch.Presenters;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SpotifyCanvasPresenterTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OpenedSourceFadesInAndRepeatedUpdatesPreserveVisibleFrame(bool alternate, bool playing) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var refs = CreateRefs(alternate);
        using var presenter = new SpotifyCanvasPresenter(refs, Dispatcher.CurrentDispatcher);
        presenter.ApplyPresentation(new(.4, true, true, true));
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(refs.BlurFallbackBackground!.Opacity - .55) < .001, "canvas fallback fade", ct);
        Assert.False(presenter.IsMediaOpen);
        Assert.Equal(.6, refs.BrightnessOverlay.Opacity, 3);
        var source = Source();
        presenter.SetSource(source, 1, playing);
        Assert.Equal(source, presenter.CurrentSource);
        Assert.Equal(1, presenter.CurrentSourceVersion);
        Assert.False(presenter.IsCanvasVisiblyShowing);
        int opened = 0;
        presenter.MediaOpened += (_, _) => opened++;
        refs.Video.RaiseEvent(new RoutedEventArgs(MediaElement.MediaOpenedEvent));
        refs.VideoAlt?.RaiseEvent(new RoutedEventArgs(MediaElement.MediaOpenedEvent));
        Assert.Equal(1, opened);
        await WpfFrameWaiter.UntilAsync(() => presenter.IsCanvasVisiblyShowing, "canvas fade in", ct);
        Assert.Equal(Visibility.Collapsed, refs.BlurFallbackBackground!.Visibility);
        presenter.SetSource(source, 2, !playing);
        Assert.Equal(2, presenter.CurrentSourceVersion);
        Assert.True(presenter.IsCanvasVisiblyShowing);
        presenter.SetPlaybackState(true);
        presenter.SetPlaybackState(false);
        presenter.ApplyPresentation(new(5, false, true, true));
        Assert.Equal(0, refs.BrightnessOverlay.Opacity);
        Assert.True(presenter.IsCanvasVisiblyShowing);
        presenter.ApplyPresentation(new(-1, true, true, true));
        Assert.Equal(.8, refs.BrightnessOverlay.Opacity, 3);
        presenter.Hide(clearSource: false);
        Assert.Equal(source, presenter.CurrentSource);
        Assert.False(presenter.IsMediaOpen);
        Assert.Equal(Visibility.Collapsed, refs.Background.Visibility);
        presenter.SetSource(null, 3, false);
        Assert.Null(presenter.CurrentSource);
        Assert.Null(refs.Video.Source);
        Assert.Null(refs.VideoAlt?.Source);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoopCompletionSwapsPlayersOrRewindsSinglePlayer(bool alternate) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var refs = CreateRefs(alternate);
        using var presenter = new SpotifyCanvasPresenter(refs, Dispatcher.CurrentDispatcher);
        presenter.ApplyPresentation(new(1, true, true, true));
        presenter.SetSource(Source(), 1, false);
        refs.Video.RaiseEvent(new RoutedEventArgs(MediaElement.MediaOpenedEvent));
        int ended = 0;
        presenter.MediaEnded += (_, _) => ended++;
        refs.Video.RaiseEvent(new RoutedEventArgs(MediaElement.MediaEndedEvent));
        Assert.Equal(1, ended);
        if (alternate)
        {
            await WpfFrameWaiter.UntilAsync(() => ReferenceEquals(Field<MediaElement>(presenter, "_activeVideo"), refs.VideoAlt) &&
                !Field<bool>(presenter, "_isSwapping"), "canvas loop crossfade", ct);
            Assert.Equal(1, refs.VideoAlt!.Opacity);
            Assert.Equal(0, refs.Video.Opacity);
            refs.Video.RaiseEvent(new RoutedEventArgs(MediaElement.MediaEndedEvent));
            Assert.Equal(1, ended);
        }
        else
        {
            Assert.Equal(TimeSpan.Zero, refs.Video.Position);
            Assert.False(Field<bool>(presenter, "_isSwapping"));
        }
        presenter.SetPlaybackState(true);
        Invoke(presenter, "OnLoopTimerTick", null, EventArgs.Empty);
        presenter.SetPlaybackState(false);
        presenter.Release();
        Assert.Null(presenter.CurrentSource);
        Assert.False(presenter.IsMediaOpen);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedActiveVideoFallsBackAndStandbyFailureKeepsActiveVideo(bool alternate) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var refs = CreateRefs(alternate);
        using var presenter = new SpotifyCanvasPresenter(refs, Dispatcher.CurrentDispatcher);
        presenter.ApplyPresentation(new(.5, true, true, true));
        presenter.SetSource(Source(), 1, false);
        refs.Video.RaiseEvent(new RoutedEventArgs(MediaElement.MediaOpenedEvent));
        await WpfFrameWaiter.UntilAsync(() => presenter.IsCanvasVisiblyShowing, "canvas open before failure", ct);
        var error = (ExceptionRoutedEventArgs)typeof(ExceptionRoutedEventArgs).GetConstructors(Private).Single()
            .Invoke([MediaElement.MediaFailedEvent, refs.Video, new InvalidOperationException("test decode failure")]);
        string? failure = null;
        presenter.MediaFailed += (_, message) => failure = message;
        if (alternate)
        {
            Invoke(presenter, "OnVideoMediaFailed", refs.VideoAlt, error);
            Assert.Null(refs.VideoAlt!.Source);
            Assert.True(presenter.IsMediaOpen);
            Assert.Null(failure);
        }
        Invoke(presenter, "OnVideoMediaFailed", refs.Video, error);
        Assert.Equal("test decode failure", failure);
        Assert.Null(presenter.CurrentSource);
        Assert.False(presenter.IsMediaOpen);
        Assert.Equal(Visibility.Visible, refs.BlurFallbackBackground!.Visibility);
        presenter.ApplyPresentation(new(1, false, false, true));
        Assert.Equal(Visibility.Collapsed, refs.BlurFallbackBackground.Visibility);
        presenter.ApplyPresentation(new(1, false, true, false));
        Assert.Equal(Visibility.Collapsed, refs.Background.Visibility);
    });

    [Fact]
    public void DisposalUnhooksEventsAndLateCallbacksCannotReopenCanvas() => SharedStaTestRunner.Run(() =>
    {
        var refs = CreateRefs(true);
        var presenter = new SpotifyCanvasPresenter(refs, Dispatcher.CurrentDispatcher);
        presenter.SetSource(Source(), 1, false);
        int unloaded = 0, opened = 0;
        presenter.Unloaded += (_, _) => unloaded++;
        presenter.MediaOpened += (_, _) => opened++;
        refs.Video.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.Equal(1, unloaded);
        Assert.Null(presenter.CurrentSource);
        presenter.SetSource(Source(), 2, false);
        presenter.Dispose();
        presenter.Dispose();
        refs.Video.RaiseEvent(new RoutedEventArgs(MediaElement.MediaOpenedEvent));
        Assert.Equal(0, opened);
        presenter.SetSource(Source(), 3, true);
        presenter.SetPlaybackState(true);
        presenter.ApplyPresentation(new(1, true, true, true));
        presenter.UpdateCrop();
        presenter.FadeInBackgroundIfReady();
        presenter.Hide(true);
        Assert.False(presenter.IsCanvasVisiblyShowing);
    });

    private static SpotifyCanvasViewRefs CreateRefs(bool alternate)
    {
        static MediaElement Video() => new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, IsMuted = true };
        var viewport = new Grid();
        var video = Video();
        viewport.Children.Add(video);
        MediaElement? second = alternate ? Video() : null;
        if (second != null) viewport.Children.Add(second);
        var background = new Border { Child = viewport, Visibility = Visibility.Collapsed };
        background.Measure(new Size(300, 150));
        background.Arrange(new Rect(0, 0, 300, 150));
        return new()
        {
            Background = background,
            Viewport = viewport,
            Video = video,
            VideoAlt = second,
            BrightnessOverlay = new Rectangle(),
            BlurFallbackBackground = new Border { Visibility = Visibility.Collapsed },
            BlurFallbackImage = new Image()
        };
    }
    private static Uri Source() => new(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "canvas-test.mp4"));
    private static T Field<T>(SpotifyCanvasPresenter presenter, string name) => (T)typeof(SpotifyCanvasPresenter).GetField(name, Private)!.GetValue(presenter)!;
    private static object? Invoke(SpotifyCanvasPresenter presenter, string name, params object?[] args) => typeof(SpotifyCanvasPresenter).GetMethod(name, Private)!.Invoke(presenter, args);
}
