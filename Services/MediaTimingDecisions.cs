using System;
using VNotch.Models;

namespace VNotch.Services;

public readonly record struct NewTrackDebounceParams(
    string CurrentTrack,
    string CurrentArtist,
    bool IsPlaying,
    bool ForceRefresh,
    string LastPublishedTrackIdentity,
    string PendingKey,
    DateTime PendingSince,
    DateTime NowUtc,
    double DebounceMs = 600);

public readonly record struct SoundCloudPreserveParams(
    string MediaSource,
    string CurrentTrack,
    string CurrentArtist,
    string SourceAppId,
    string SessionInstanceKey,
    string LastSource,
    string LastPublishedSessionInstanceKey,
    DateTime LastMetadataChangeTime,
    DateTime Now,
    bool HasSessionOverride,
    string SessionOverride,
    double FreshnessSeconds = 3.0);

internal static class MediaTimingDecisions
{
    private static bool IsLongHoldSource(string lastSource)
        => lastSource == MediaPlatform.YouTube.ToDisplayString()
        || lastSource == MediaPlatform.Browser.ToDisplayString();

    public static (bool hold, DateTime emptyStart, string stableSignature) EvaluateEmptyMetadataHold(
        string currentTrack,
        bool isAnyMediaPlaying,
        string currentSignature,
        string lastSource,
        DateTime emptyStart,
        string stableSignature,
        DateTime now)
    {
        if (string.IsNullOrEmpty(currentTrack))
        {
            if (isAnyMediaPlaying)
            {
                if (emptyStart == DateTime.MinValue)
                {
                    emptyStart = now;
                }

                double holdSeconds = IsLongHoldSource(lastSource) ? 4.0 : 2.5;
                if ((now - emptyStart).TotalSeconds < holdSeconds && !string.IsNullOrEmpty(stableSignature))
                {
                    return (true, emptyStart, stableSignature);
                }
            }
            else
            {
                emptyStart = DateTime.MinValue;
                stableSignature = "";
            }
        }
        else
        {
            emptyStart = DateTime.MinValue;
            stableSignature = currentSignature;
        }

        return (false, emptyStart, stableSignature);
    }

    public static (bool debounce, string pendingKey, DateTime pendingSince) EvaluateNewTrackDebounce(NewTrackDebounceParams p)
    {
        bool isNewTrack = !string.IsNullOrEmpty(p.CurrentTrack) &&
                          !string.Equals(
                              MediaHeuristics.BuildTrackIdentity(p.CurrentTrack, p.CurrentArtist),
                              p.LastPublishedTrackIdentity,
                              StringComparison.Ordinal);

        if (isNewTrack && !p.IsPlaying && !p.ForceRefresh)
        {
            string candidateKey = MediaHeuristics.BuildTrackIdentity(p.CurrentTrack, p.CurrentArtist);
            string pendingKey = p.PendingKey;
            DateTime pendingSince = p.PendingSince;

            if (candidateKey != pendingKey)
            {
                pendingKey = candidateKey;
                pendingSince = p.NowUtc;
            }

            bool debounce = (p.NowUtc - pendingSince).TotalMilliseconds < p.DebounceMs;
            return (debounce, pendingKey, pendingSince);
        }

        return (false, "", p.PendingSince);
    }

    public static (string artist, string stableArtist) EvaluateArtistStabilization(
        string currentArtist,
        string stableArtist,
        DateTime lastSourceConfirmedTime,
        DateTime now,
        double holdSeconds = 15.0)
    {
        bool isGeneric = currentArtist == MediaPlatform.YouTube.ToDisplayString()
                      || currentArtist == MediaPlatform.Browser.ToDisplayString();

        if (isGeneric &&
            !string.IsNullOrEmpty(stableArtist) &&
            (now - lastSourceConfirmedTime).TotalSeconds < holdSeconds)
        {
            return (stableArtist, stableArtist);
        }

        if (!string.IsNullOrEmpty(currentArtist) && !isGeneric)
        {
            return (currentArtist, currentArtist);
        }

        return (currentArtist, stableArtist);
    }

    public static bool ShouldPreserveSoundCloud(SoundCloudPreserveParams p)
    {
        if (!string.Equals(p.MediaSource, MediaPlatform.Browser.ToDisplayString(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(p.LastSource, MediaPlatform.SoundCloud.ToDisplayString(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(p.CurrentTrack) ||
            string.IsNullOrWhiteSpace(p.SourceAppId) ||
            !PlatformDetector.IsBrowserApp(p.SourceAppId) ||
            string.IsNullOrWhiteSpace(p.SessionInstanceKey))
            return false;

        if (!string.Equals(p.LastPublishedSessionInstanceKey, p.SessionInstanceKey, StringComparison.Ordinal))
            return false;

        if ((p.Now - p.LastMetadataChangeTime).TotalSeconds > p.FreshnessSeconds)
            return false;

        bool hasYouTubeHint = p.CurrentTrack.Contains("youtube", StringComparison.OrdinalIgnoreCase) ||
                              p.CurrentArtist.Contains("youtube", StringComparison.OrdinalIgnoreCase);
        if (hasYouTubeHint)
            return false;

        if (p.HasSessionOverride &&
            !string.IsNullOrEmpty(p.SessionOverride) &&
            !string.Equals(p.SessionOverride, MediaPlatform.SoundCloud.ToDisplayString(), StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}
