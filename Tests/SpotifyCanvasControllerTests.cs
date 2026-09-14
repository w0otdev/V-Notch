using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Presenters;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotifyCanvasControllerTests
{
    private const string CanvasTestUrl = "https://canvaz.scdn.co/upload/video/test.mp4";

    private sealed class FakeSpotifyCanvasPresenter : ISpotifyCanvasPresenter
    {
        public List<(Uri? uri, long version, bool autoPlay)> SetSourceCalls { get; } = new();
        public List<(bool clearSource, bool restoreFallback)> HideCalls { get; } = new();
        public List<bool> SetPlaybackStateCalls { get; } = new();
        public List<SpotifyCanvasPresentationOptions> ApplyPresentationCalls { get; } = new();
        public int ReleaseCount { get; private set; }
        public int FadeInBackgroundIfReadyCount { get; private set; }
        public int UpdateCropCount { get; private set; }
        public int HideBlurFallbackCount { get; private set; }
        public int RestoreBlurFallbackCount { get; private set; }
        public int DisposeCount { get; private set; }

        public bool IsMediaOpen { get; set; }
        public bool IsCanvasVisiblyShowing { get; set; }
        public Uri? CurrentSource { get; set; }
        public long CurrentSourceVersion { get; set; }

        public event EventHandler? MediaOpened;
        public event EventHandler? MediaEnded;
        public event EventHandler<string?>? MediaFailed;
        public event EventHandler? Unloaded;

        public void SetPlaybackState(bool isPlaying) => SetPlaybackStateCalls.Add(isPlaying);

        public void SetSource(Uri? uri, long sourceVersion, bool autoPlay)
        {
            CurrentSource = uri;
            CurrentSourceVersion = sourceVersion;
            IsMediaOpen = uri != null;
            SetSourceCalls.Add((uri, sourceVersion, autoPlay));
        }

        public void ApplyPresentation(SpotifyCanvasPresentationOptions options) =>
            ApplyPresentationCalls.Add(options);

        public void Hide(bool clearSource, bool restoreFallback = true)
        {
            IsMediaOpen = false;
            HideCalls.Add((clearSource, restoreFallback));
            if (clearSource)
            {
                Release();
            }
        }

        public void Release()
        {
            CurrentSource = null;
            IsMediaOpen = false;
            ReleaseCount++;
        }

        public void FadeInBackgroundIfReady() => FadeInBackgroundIfReadyCount++;
        public void UpdateCrop() => UpdateCropCount++;
        public void HideBlurFallback() => HideBlurFallbackCount++;
        public void RestoreBlurFallback() => RestoreBlurFallbackCount++;
        public void Dispose() => DisposeCount++;

        public void TriggerMediaOpened() => MediaOpened?.Invoke(this, EventArgs.Empty);
        public void TriggerMediaEnded() => MediaEnded?.Invoke(this, EventArgs.Empty);
        public void TriggerMediaFailed(string error) => MediaFailed?.Invoke(this, error);
        public void TriggerUnloaded() => Unloaded?.Invoke(this, EventArgs.Empty);
    }

    private static SpotifyCanvasService CreateStubService(Func<HttpRequestMessage, HttpResponseMessage> handlerFunc)
    {
        var handler = new TestHttpMessageHandler(handlerFunc);
        return new SpotifyCanvasService(new HttpClient(handler));
    }

    private static SpotifyCanvasService CreateAsyncStubService(Func<HttpRequestMessage, Task<HttpResponseMessage>> handlerFunc)
    {
        var handler = new AsyncTestHttpMessageHandler(handlerFunc);
        return new SpotifyCanvasService(new HttpClient(handler));
    }

    private sealed class TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(reply(request));
        }
    }

    private sealed class AsyncTestHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return reply(request);
        }
    }

    [Fact]
    public void UpdateTrack_WhenNotSpotify_HidesBackgroundAndResetsState()
    {
        var presenter = new FakeSpotifyCanvasPresenter();
        using var service = CreateStubService(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var controller = new SpotifyCanvasController(service, presenter, action => action());

        controller.UpdateTrack("Title", "Artist", TimeSpan.FromMinutes(3), MediaPlatform.YouTube, isPlaying: true);

        Assert.Equal(string.Empty, controller.CurrentTrackKey);
        Assert.Null(controller.CurrentUri);
        Assert.Contains(presenter.HideCalls, c => c.clearSource);
    }

    [Fact]
    public void UpdateTrack_WhenSurfaceNotVisible_DefersFetchUntilVisible()
    {
        var presenter = new FakeSpotifyCanvasPresenter();
        var tcs = new TaskCompletionSource<HttpResponseMessage>();
        using var service = CreateAsyncStubService(_ => tcs.Task);
        using var controller = new SpotifyCanvasController(service, presenter, action => action());

        controller.UpdateSettings(enabled: true, spDc: "dummy_sp_dc_cookie", brightness: 0.7, localOnlyMode: false);
        controller.SetSurfaceVisibility(false);
        controller.UpdateTrack("Track A", "Artist A", TimeSpan.FromMinutes(3), MediaPlatform.Spotify, isPlaying: true);

        Assert.Equal("Track A|Artist A", controller.CurrentTrackKey);
        Assert.False(controller.HasPendingFetch);
        Assert.False(controller.IsLookupCompleted);

        // When surface becomes visible later, fetch is kicked off
        controller.SetSurfaceVisibility(true);
        Assert.True(controller.HasPendingFetch);
    }

    [Fact]
    public void UpdatePlaybackState_UpdatesPlaybackOnPresenter()
    {
        var presenter = new FakeSpotifyCanvasPresenter();
        using var service = CreateStubService(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var controller = new SpotifyCanvasController(service, presenter, action => action());

        controller.UpdateTrack("Track", "Artist", TimeSpan.FromMinutes(3), MediaPlatform.Spotify, isPlaying: false);
        controller.UpdatePlaybackState(true);

        Assert.Contains(true, presenter.SetPlaybackStateCalls);
    }

    [Fact]
    public void SetSurfaceVisibility_WhenHidden_HidesPresenterAndCancelsFetch()
    {
        var presenter = new FakeSpotifyCanvasPresenter();
        var tcs = new TaskCompletionSource<HttpResponseMessage>();
        using var service = CreateAsyncStubService(_ => tcs.Task);
        using var controller = new SpotifyCanvasController(service, presenter, action => action());

        controller.UpdateSettings(enabled: true, spDc: "dummy_sp_dc_cookie", brightness: 0.7, localOnlyMode: false);
        controller.SetSurfaceVisibility(true);
        controller.UpdateTrack("Track", "Artist", TimeSpan.FromMinutes(3), MediaPlatform.Spotify, isPlaying: true);
        Assert.True(controller.HasPendingFetch);

        controller.SetSurfaceVisibility(false);

        Assert.False(controller.HasPendingFetch);
        Assert.Contains(presenter.HideCalls, c => c.clearSource && !c.restoreFallback);
    }

    [Fact]
    public void UpdateSettings_WhenDisabled_HidesAndResets()
    {
        var presenter = new FakeSpotifyCanvasPresenter();
        using var service = CreateStubService(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var controller = new SpotifyCanvasController(service, presenter, action => action());

        controller.SetSurfaceVisibility(true);
        controller.UpdateTrack("Track", "Artist", TimeSpan.FromMinutes(3), MediaPlatform.Spotify, isPlaying: true);

        controller.UpdateSettings(enabled: false, spDc: null, brightness: 0.8, localOnlyMode: false);

        Assert.Equal(string.Empty, controller.CurrentTrackKey);
        Assert.False(controller.HasPendingFetch);
        Assert.Contains(presenter.HideCalls, c => c.clearSource);
    }

    [Fact]
    public void UpdateTrack_SequentialRequests_IncrementsRequestId()
    {
        var presenter = new FakeSpotifyCanvasPresenter();
        using var service = CreateStubService(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var controller = new SpotifyCanvasController(service, presenter, action => action());

        controller.SetSurfaceVisibility(true);
        controller.UpdateTrack("Track A", "Artist A", TimeSpan.FromMinutes(3), MediaPlatform.Spotify, isPlaying: true);
        long firstRequestId = controller.CurrentRequestId;

        controller.UpdateTrack("Track B", "Artist B", TimeSpan.FromMinutes(3), MediaPlatform.Spotify, isPlaying: true);
        long secondRequestId = controller.CurrentRequestId;

        controller.UpdateTrack("Track A", "Artist A", TimeSpan.FromMinutes(3), MediaPlatform.Spotify, isPlaying: true);
        long thirdRequestId = controller.CurrentRequestId;

        Assert.True(firstRequestId < secondRequestId);
        Assert.True(secondRequestId < thirdRequestId);
    }

    [Fact]
    public void Dispose_WhenMultipleTasksInFlight_DisposesSafelyWithoutException()
    {
        var presenter = new FakeSpotifyCanvasPresenter();
        using var service = CreateStubService(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var controller = new SpotifyCanvasController(service, presenter, action => action());

        controller.SetSurfaceVisibility(true);
        controller.UpdateTrack("Track 1", "Artist 1", TimeSpan.FromMinutes(3), MediaPlatform.Spotify, isPlaying: true);
        controller.UpdateTrack("Track 2", "Artist 2", TimeSpan.FromMinutes(3), MediaPlatform.Spotify, isPlaying: true);
        controller.UpdateTrack("Track 3", "Artist 3", TimeSpan.FromMinutes(3), MediaPlatform.Spotify, isPlaying: true);

        // Dispose while multiple requests were triggered
        var exception = Record.Exception(() => controller.Dispose());
        Assert.Null(exception);
    }
}
