using System;
using System.Runtime.InteropServices;

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

internal static partial class MediaTimingDecisions
{
    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_media_timing_is_long_hold_source(
        char* srcPtr, int srcLen, out byte outLongHold);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_media_timing_should_preserve_soundcloud(
        char* mediaSourcePtr, int mediaSourceLen,
        char* curTrackPtr, int curTrackLen,
        char* curArtistPtr, int curArtistLen,
        char* sourceAppIdPtr, int sourceAppIdLen,
        char* sessionKeyPtr, int sessionKeyLen,
        char* lastSourcePtr, int lastSourceLen,
        char* lastSessionKeyPtr, int lastSessionKeyLen,
        long lastMetaChangeTicks,
        long nowTicks,
        byte hasSessionOverride,
        char* sessionOverridePtr, int sessionOverrideLen,
        double freshnessSeconds,
        out byte outPreserve);

    private static unsafe bool IsLongHoldSource(string lastSource)
    {
        if (string.IsNullOrEmpty(lastSource))
            return false;
        fixed (char* ptr = lastSource)
        {
            _ = vn_media_timing_is_long_hold_source(ptr, lastSource.Length, out byte lh);
            return lh != 0;
        }
    }

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
        bool isGeneric = IsLongHoldSource(currentArtist);

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

    public static unsafe bool ShouldPreserveSoundCloud(SoundCloudPreserveParams p)
    {
        string ms = p.MediaSource ?? string.Empty;
        string ct = p.CurrentTrack ?? string.Empty;
        string ca = p.CurrentArtist ?? string.Empty;
        string app = p.SourceAppId ?? string.Empty;
        string sk = p.SessionInstanceKey ?? string.Empty;
        string ls = p.LastSource ?? string.Empty;
        string lsk = p.LastPublishedSessionInstanceKey ?? string.Empty;
        string so = p.SessionOverride ?? string.Empty;

        fixed (char* pMs = ms)
        fixed (char* pCt = ct)
        fixed (char* pCa = ca)
        fixed (char* pApp = app)
        fixed (char* pSk = sk)
        fixed (char* pLs = ls)
        fixed (char* pLsk = lsk)
        fixed (char* pSo = so)
        {
            _ = vn_media_timing_should_preserve_soundcloud(
                pMs, ms.Length,
                pCt, ct.Length,
                pCa, ca.Length,
                pApp, app.Length,
                pSk, sk.Length,
                pLs, ls.Length,
                pLsk, lsk.Length,
                p.LastMetadataChangeTime.Ticks,
                p.Now.Ticks,
                p.HasSessionOverride ? (byte)1 : (byte)0,
                pSo, so.Length,
                p.FreshnessSeconds,
                out byte preserve);
            return preserve != 0;
        }
    }
}
