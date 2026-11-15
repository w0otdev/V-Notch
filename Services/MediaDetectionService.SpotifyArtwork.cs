using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Models;
using Windows.Media.Control;

namespace VNotch.Services;

public sealed partial class MediaDetectionService : IMediaDetectionService, IAsyncDisposable
{
    private async Task FetchSpotifyThumbnailAsync(MediaInfo info, CancellationToken token, string trackDuringFetch, string artistDuringFetch, string sourceAppDuringFetch, string sessionKeyDuringFetch, int thumbGenAtStart, int fetchGeneration, CancellationTokenSource? ownedCts = null)
    {
        int lifecycleGenAtStart = Volatile.Read(ref _lifecycleGeneration);
        bool IsCurrentSpotifyFetch() =>
            !_disposed &&
            _state is not (ServiceLifecycleState.Stopping or ServiceLifecycleState.Disposed) &&
            lifecycleGenAtStart == Volatile.Read(ref _lifecycleGeneration) &&
            !token.IsCancellationRequested &&
            fetchGeneration == Volatile.Read(ref _soundCloudFetchGeneration) &&
            Volatile.Read(ref _thumbnailFetchGeneration) == thumbGenAtStart &&
            IsStillSamePublishedTrack(trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch);

        try
        {
            if (!IsCurrentSpotifyFetch())
                return;

            string? artworkUrl = await _metadataLookup.TryGetSpotifyArtworkUrlAsync(
                trackDuringFetch, artistDuringFetch, info.Duration, null, token);
            if (string.IsNullOrEmpty(artworkUrl) || !IsCurrentSpotifyFetch())
                return;

            BitmapSource? frameBitmap = await DownloadImageAsync(artworkUrl, token);
            if (frameBitmap == null ||
                IsLikelySoundCloudPlaceholderThumbnail(frameBitmap) ||
                !IsCurrentSpotifyFetch())
            {
                return;
            }

            string spotifySource = MediaPlatform.Spotify.ToDisplayString();
            frameBitmap = CropToSquare(frameBitmap, spotifySource) ?? frameBitmap;

            MediaInfo? publishedSnapshot = null;
            await _updateLock.WaitAsync(token);
            try
            {
                if (!IsCurrentSpotifyFetch() ||
                    info.Platform != MediaPlatform.Spotify ||
                    (_lastPublishedInfo != null && _lastPublishedInfo.Platform != MediaPlatform.Spotify))
                {
                    return;
                }

                _timelineSimulator.RecoveredThumbnail = frameBitmap;
                CachedThumbnail = frameBitmap;
                _cachedThumbnailSource = spotifySource;
                info.Thumbnail = frameBitmap;

                var target = _lastPublishedInfo ?? info;
                target.Thumbnail = frameBitmap;

                _lastPublishedInfo = target.Clone();
                string sig = _lastPublishedInfo.GetSignature();
                _lastPublishedSignature = sig;
                _lastTrackSignature = sig;
                _lastPublishedTrackIdentity = BuildTrackIdentity(_lastPublishedInfo.CurrentTrack, _lastPublishedInfo.CurrentArtist);
                _lastPublishedTrackOnlyIdentity = BuildTrackIdentity(_lastPublishedInfo.CurrentTrack, "");

                publishedSnapshot = _lastPublishedInfo.Clone();
                publishedSnapshot.IsThumbnailOnlyUpdate = true;
            }
            finally
            {
                _updateLock.Release();
            }

            if (publishedSnapshot != null)
            {
                await FireMediaChangedAsync(publishedSnapshot);
            }
        }
        catch (OperationCanceledException) { /* Cancelled */ }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-SPOTIFY-FETCH", ex.ToString());
        }
        finally
        {
            if (fetchGeneration == Volatile.Read(ref _soundCloudFetchGeneration))
            {
                Interlocked.Exchange(ref _soundCloudFetchInFlight, 0);
            }
            ReleaseThumbnailCts(ownedCts);
        }
    }

    private static string GetSpotifyWindowTitle()
    {
        Span<char> buf = stackalloc char[512];
        return vn_media_detection_get_spotify_window_title(buf, buf.Length, out int len) == 0 && len > 0
            ? new string(buf[..len])
            : string.Empty;
    }

}
