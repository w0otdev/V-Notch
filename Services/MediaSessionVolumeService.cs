using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VNotch.Services;

public sealed partial class MediaSessionVolumeService : IDisposable
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_session_volume_matches_source(
        ReadOnlySpan<char> displayPtr, int displayLen,
        ReadOnlySpan<char> sessionIdPtr, int sessionIdLen,
        ReadOnlySpan<char> instanceIdPtr, int instanceIdLen,
        ReadOnlySpan<char> sourceAppPtr, int sourceAppLen,
        out byte outMatches);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_media_session_volume_compute_score(
        byte matchedByProcess,
        byte matchedByMetadata,
        byte sourceMatchesLast,
        uint processId,
        uint lastProcessId,
        byte sessionIdMatchesLast,
        int state,
        out double outScore);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_session_volume_get_process_ids(
        ReadOnlySpan<char> source, int sourceLength, Span<uint> processIds, int capacity, out int count);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_session_volume_extract_candidates(
        ReadOnlySpan<char> sourceAppPtr,
        int sourceAppLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    private readonly Func<MediaAudioSessions> _getAudioSessions;
    private readonly Func<string, HashSet<uint>> _getProcessIds;
    private readonly Func<DateTime> _utcNow;

    public MediaSessionVolumeService() : this(WindowsMediaAudioSessions.ReadAllEndpoints, GetProcessIds, () => DateTime.UtcNow) { }

    internal MediaSessionVolumeService(Func<MediaAudioSessions> getAudioSessions,
        Func<string, HashSet<uint>> getProcessIds, Func<DateTime> utcNow)
    {
        _getAudioSessions = getAudioSessions;
        _getProcessIds = getProcessIds;
        _utcNow = utcNow;
    }

    private readonly object _volumeCacheLock = new();
    private string _lastMatchedSessionId = "";
    private uint _lastMatchedProcessId;
    private string _lastMatchedSourceAppId = "";
    private string _cachedProcessSourceAppId = "";
    private DateTime _cachedProcessIdsAtUtc = DateTime.MinValue;
    private HashSet<uint> _cachedProcessIds = new();

    private IMediaAudioVolume? _cachedSimpleVolume;
    private string _cachedVolumeSourceAppId = "";
    private DateTime _cachedVolumeSessionAtUtc = DateTime.MinValue;
    private const double SessionCacheLifetimeMs = 3000;

    public bool TryGetVolume(string sourceAppId, out float volume, out bool isMuted)
    {
        float resolvedVolume = 0f;
        bool resolvedMuted = false;

        bool success = TryWithAudioSession(sourceAppId, session =>
        {
            using var simpleVolume = session.SimpleAudioVolume;
            resolvedVolume = Math.Clamp(simpleVolume.Volume, 0f, 1f);
            resolvedMuted = simpleVolume.Mute;
            return true;
        });

        volume = resolvedVolume;
        isMuted = resolvedMuted;
        return success;
    }

    public bool TrySetVolume(string sourceAppId, float volume)
    {
        float target = Math.Clamp(volume, 0f, 1f);

        lock (_volumeCacheLock)
        {
            if (_cachedSimpleVolume != null && !_cachedSimpleVolume.IsActive)
            {
                // Browser audio helpers can restart while the old COM volume still accepts writes.
                InvalidateVolumeSessionCacheLocked();
                _cachedProcessIdsAtUtc = DateTime.MinValue;
            }
            if (_cachedSimpleVolume != null &&
                string.Equals(sourceAppId, _cachedVolumeSourceAppId, StringComparison.OrdinalIgnoreCase) &&
                (_utcNow() - _cachedVolumeSessionAtUtc).TotalMilliseconds < SessionCacheLifetimeMs)
            {
                try
                {
                    _cachedSimpleVolume.Volume = target;
                    if (target > 0.001f && _cachedSimpleVolume.Mute)
                    {
                        _cachedSimpleVolume.Mute = false;
                    }
                    return true;
                }
                catch
                {
                    InvalidateVolumeSessionCacheLocked();
                }
            }
        }

        return TryWithAudioSession(sourceAppId, session =>
        {
            lock (_volumeCacheLock)
            {
                InvalidateVolumeSessionCacheLocked();
                _cachedSimpleVolume = session.SimpleAudioVolume;
                _cachedVolumeSourceAppId = sourceAppId;
                _cachedVolumeSessionAtUtc = _utcNow();

                _cachedSimpleVolume.Volume = target;
                if (target > 0.001f && _cachedSimpleVolume.Mute)
                {
                    _cachedSimpleVolume.Mute = false;
                }
                return true;
            }
        });
    }

    public void InvalidateVolumeSessionCache()
    {
        lock (_volumeCacheLock)
        {
            InvalidateVolumeSessionCacheLocked();
        }
    }

    public void Dispose() => InvalidateVolumeSessionCache();

    private void InvalidateVolumeSessionCacheLocked()
    {
        if (_cachedSimpleVolume != null)
        {
            try
            {
                _cachedSimpleVolume.Dispose();
            }
            catch
            {
                // Ignore COM disposal errors if audio session is already invalid
            }
            _cachedSimpleVolume = null;
        }
        _cachedVolumeSourceAppId = "";
    }

    public bool TryToggleMute(string sourceAppId)
    {
        InvalidateVolumeSessionCache();

        return TryWithAudioSession(sourceAppId, session =>
        {
            using var simpleVolume = session.SimpleAudioVolume;
            simpleVolume.Mute = !simpleVolume.Mute;
            return true;
        });
    }

    private bool TryWithAudioSession(string sourceAppId, Func<MediaAudioSessionSnapshot, bool> action)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
            return false;

        try
        {
            using var snapshot = _getAudioSessions();
            var sessions = snapshot.Sessions;
            if (sessions == null || sessions.Count == 0)
                return false;

            var candidateProcessIds = GetCachedProcessIdsForSourceApp(sourceAppId);
            var targetSession = FindBestMatchingSession(sessions, sourceAppId, candidateProcessIds);

            if (targetSession == null)
                return false;

            _lastMatchedSourceAppId = sourceAppId;
            _lastMatchedProcessId = targetSession.GetProcessID;
            _lastMatchedSessionId = targetSession.GetSessionIdentifier ?? "";

            return action(targetSession);
        }
        catch
        {
            return false;
        }
    }

    private MediaAudioSessionSnapshot? FindBestMatchingSession(IReadOnlyList<MediaAudioSessionSnapshot> sessions, string sourceAppId, HashSet<uint> candidateProcessIds)
    {
        MediaAudioSessionSnapshot? targetSession = null;
        double bestScore = double.MinValue;

        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            if (session == null || session.IsSystemSoundsSession || session.State == 2)
                continue;

            double score = CalculateSessionScore(session, sourceAppId, candidateProcessIds);
            if (score > bestScore)
            {
                bestScore = score;
                targetSession = session;
            }
        }

        return targetSession;
    }

    private double CalculateSessionScore(MediaAudioSessionSnapshot session, string sourceAppId, HashSet<uint> candidateProcessIds)
    {
        uint processId = session.GetProcessID;
        bool matchedByProcess = candidateProcessIds.Contains(processId);
        bool matchedByMetadata = SessionMatchesSourceAppId(session, sourceAppId);
        if (!matchedByProcess && !matchedByMetadata)
            return double.MinValue;

        bool sourceMatchesLast = string.Equals(sourceAppId, _lastMatchedSourceAppId, StringComparison.OrdinalIgnoreCase);
        bool sessionIdMatchesLast = string.Equals(session.GetSessionIdentifier, _lastMatchedSessionId, StringComparison.OrdinalIgnoreCase);
        vn_media_session_volume_compute_score(
            matchedByProcess ? (byte)1 : (byte)0,
            matchedByMetadata ? (byte)1 : (byte)0,
            sourceMatchesLast ? (byte)1 : (byte)0,
            processId,
            _lastMatchedProcessId,
            sessionIdMatchesLast ? (byte)1 : (byte)0,
            session.State,
            out double score);

        try
        {
            score += session.ReadPeak() * 100;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("VOLUME-SCORE", ex.Message);
        }

        return score;
    }

    internal static HashSet<string> GetProcessNameCandidates(string sourceAppId)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(sourceAppId))
            return candidates;

        ReadOnlySpan<char> srcSpan = sourceAppId.AsSpan();
        Span<char> buf = stackalloc char[512];
        if (vn_media_session_volume_extract_candidates(srcSpan, srcSpan.Length, buf, buf.Length, out int outLen) == 0 && outLen > 0)
        {
            ReadOnlySpan<char> list = buf[..outLen];
            int start = 0;
            while (start < list.Length)
            {
                int sep = list[start..].IndexOf(';');
                ReadOnlySpan<char> item = sep < 0 ? list[start..] : list.Slice(start, sep);
                if (!item.IsEmpty)
                {
                    candidates.Add(new string(item));
                }
                if (sep < 0) break;
                start += sep + 1;
            }
        }

        return candidates;
    }

    private HashSet<uint> GetCachedProcessIdsForSourceApp(string sourceAppId)
    {
        bool canUseCache =
            string.Equals(sourceAppId, _cachedProcessSourceAppId, StringComparison.OrdinalIgnoreCase) &&
            (_utcNow() - _cachedProcessIdsAtUtc).TotalMilliseconds < 1200;

        if (canUseCache)
            return _cachedProcessIds;

        _cachedProcessSourceAppId = sourceAppId;
        _cachedProcessIds = _getProcessIds(sourceAppId);
        _cachedProcessIdsAtUtc = _utcNow();
        return _cachedProcessIds;
    }

    internal static HashSet<uint> GetProcessIds(string sourceAppId)
    {
        Span<uint> processIds = stackalloc uint[256];
        int status = vn_media_session_volume_get_process_ids(sourceAppId, sourceAppId.Length, processIds, processIds.Length, out int count);
        if (status == -2 && count > processIds.Length)
        {
            uint[] larger = new uint[count];
            status = vn_media_session_volume_get_process_ids(sourceAppId, sourceAppId.Length, larger, larger.Length, out count);
            return status == 0 ? new HashSet<uint>(larger.AsSpan(0, count).ToArray()) : [];
        }
        return status == 0 ? new HashSet<uint>(processIds[..count].ToArray()) : [];
    }

    private static bool SessionMatchesSourceAppId(MediaAudioSessionSnapshot session, string sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
            return false;

        ReadOnlySpan<char> disp = (session.DisplayName ?? string.Empty).AsSpan();
        ReadOnlySpan<char> sid = (session.GetSessionIdentifier ?? string.Empty).AsSpan();
        ReadOnlySpan<char> iid = (session.GetSessionInstanceIdentifier ?? string.Empty).AsSpan();
        ReadOnlySpan<char> src = sourceAppId.AsSpan();
        return vn_media_session_volume_matches_source(
            disp, disp.Length,
            sid, sid.Length,
            iid, iid.Length,
            src, src.Length,
            out byte matches) == 0 && matches != 0;
    }
}
