using System;

namespace VNotch.Presenters;

public sealed record SpotifyCanvasPresentationOptions(
    double Brightness,
    bool CanFadeIn,
    bool BlurFallbackEnabled,
    bool IsLyricsActive
);

public interface ISpotifyCanvasPresenter : IDisposable
{
    event EventHandler? MediaOpened;
    event EventHandler? MediaEnded;
    event EventHandler<string?>? MediaFailed;
    event EventHandler? Unloaded;

    bool IsMediaOpen { get; }
    Uri? CurrentSource { get; }
    long CurrentSourceVersion { get; }

    void SetSource(Uri? uri, long sourceVersion, bool autoPlay);
    void SetPlaybackState(bool isPlaying);
    void ApplyPresentation(SpotifyCanvasPresentationOptions options);
    void Hide(bool clearSource, bool restoreFallback = true);
    void Release();
    void FadeInBackgroundIfReady();
    void UpdateCrop();
    void HideBlurFallback();
    void RestoreBlurFallback();
}
