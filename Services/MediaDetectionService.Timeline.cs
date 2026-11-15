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
    private async Task ApplyTimelinePropertiesAsync(MediaInfo info, GlobalSystemMediaTransportControlsSession? session, bool forceRefresh)
    {
        bool isNewTrack = _isNewTrackThisUpdate ||
            (!string.IsNullOrEmpty(info.CurrentTrack) &&
             !string.IsNullOrEmpty(_lastTrackName) && info.CurrentTrack != _lastTrackName);
        bool isBrowserTimelineTrack = IsBrowserSourceApp(info.SourceAppId) ||
            info.Platform is MediaPlatform.YouTube or MediaPlatform.SoundCloud or MediaPlatform.Browser;

        try
        {
            var timeline = session?.GetTimelineProperties();
            if (timeline != null)
            {

                bool isInitialOrBigChange = forceRefresh ||
                                           _lastTrackSignature == "" ||
                                           (DateTime.UtcNow - _lastMetadataChangeTime).TotalSeconds < 4.0;

                if (isInitialOrBigChange)
                {

                    await Task.Delay(120, _bgCts?.Token ?? CancellationToken.None);
                    timeline = session?.GetTimelineProperties() ?? timeline;
                }

                TimeSpan duration = timeline.EndTime - timeline.StartTime;
                if (duration <= TimeSpan.Zero) duration = timeline.MaxSeekTime;
                bool isLiveTimeline = false;
                if (isBrowserTimelineTrack && MediaTimelinePolicy.HasFiniteDuration(duration))
                {
                    string identity = $"{info.SourceAppId}|{info.SessionInstanceKey}|{info.CurrentTrack}";
                    TimeSpan endpoint = timeline.EndTime > timeline.StartTime ? timeline.EndTime : timeline.MaxSeekTime;
                    isLiveTimeline = _timelineClassifier.Observe(identity, endpoint,
                        timeline.MinSeekTime - timeline.StartTime, DateTimeOffset.UtcNow, info.IsPlaying);
                }
                else
                {
                    _timelineClassifier.Reset();
                }

                var solved = TimelinePositionSolver.Solve(new TimelineSolveInputs
                {
                    StartTime = timeline.StartTime,
                    EndTime = timeline.EndTime,
                    MaxSeekTime = timeline.MaxSeekTime,
                    Position = timeline.Position,
                    LastUpdatedUtc = timeline.LastUpdatedTime.ToUniversalTime(),
                    IsInitialOrBigChange = isInitialOrBigChange,
                    IsNewTrack = isNewTrack,
                    IsBrowserTimelineTrack = isBrowserTimelineTrack,
                    IsLiveTimeline = isLiveTimeline,
                    IsPlaying = info.IsPlaying,
                    PlaybackRate = info.PlaybackRate,
                    NowUtc = DateTimeOffset.UtcNow,
                });

                info.Position = solved.Position;
                info.Duration = solved.Duration;
                info.LastUpdated = solved.LastUpdatedUtc;
                info.IsIndeterminate = solved.IsIndeterminate;
                ApplyTimelineAvailability(info, isBrowserTimelineTrack, isNewTrack, isLiveTimeline, duration, DateTime.UtcNow);
            }
            else
            {
                _timelineClassifier.Reset();
                info.IsIndeterminate = true;
                ApplyTimelineAvailability(info, isBrowserTimelineTrack, isNewTrack, false, TimeSpan.Zero, DateTime.UtcNow);
            }
        }
        catch
        {
            _timelineClassifier.Reset();
            info.IsIndeterminate = true;
            ApplyTimelineAvailability(info, isBrowserTimelineTrack, isNewTrack, false, TimeSpan.Zero, DateTime.UtcNow);
        }
    }

    internal void ApplyTimelineAvailability(MediaInfo info, bool isBrowserTimelineTrack, bool isNewTrack,
        bool isLiveTimeline, TimeSpan reportedDuration, DateTime nowUtc)
    {
        info.IsTimelinePending = false;
        if (!isBrowserTimelineTrack || reportedDuration > TimeSpan.Zero || isLiveTimeline)
            return;

        // Browsers clear their timeline while loading another video. A zero
        // duration supplies no evidence that the new video is a live broadcast.
        info.IsIndeterminate = false;
        if (_lastPublishedInfo is { } previous &&
            (info.SourceAppId != previous.SourceAppId || info.SessionInstanceKey != previous.SessionInstanceKey))
        {
            _lastMetadataChangeTime = nowUtc;
        }

        if (_lastPublishedInfo is { } last &&
            !string.IsNullOrEmpty(info.CurrentTrack) &&
            info.CurrentTrack == last.CurrentTrack &&
            info.SourceAppId == last.SourceAppId &&
            info.SessionInstanceKey == last.SessionInstanceKey)
        {
            if (last.IsIndeterminate)
            {
                info.IsIndeterminate = true;
                return;
            }

            if (last.HasTimeline)
            {
                info.Duration = last.Duration;
                return;
            }
        }

        info.IsTimelinePending = isNewTrack || _lastTrackSignature == "" ||
            (nowUtc - _lastMetadataChangeTime).TotalSeconds < 4.0;
    }

}
