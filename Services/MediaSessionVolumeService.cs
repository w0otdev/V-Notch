using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;

namespace VNotch.Services;

public sealed class MediaSessionVolumeService
{
    private readonly object _volumeCacheLock = new();
    private string _lastMatchedSessionId = "";
    private uint _lastMatchedProcessId;
    private string _lastMatchedSourceAppId = "";
    private string _cachedProcessSourceAppId = "";
    private DateTime _cachedProcessIdsAtUtc = DateTime.MinValue;
    private HashSet<uint> _cachedProcessIds = new();

    private SimpleAudioVolume? _cachedSimpleVolume;
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
            if (_cachedSimpleVolume != null &&
                string.Equals(sourceAppId, _cachedVolumeSourceAppId, StringComparison.OrdinalIgnoreCase) &&
                (DateTime.UtcNow - _cachedVolumeSessionAtUtc).TotalMilliseconds < SessionCacheLifetimeMs)
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
                _cachedVolumeSessionAtUtc = DateTime.UtcNow;

                _cachedSimpleVolume.Volume = target;
                if (target > 0.001f && _cachedSimpleVolume.Mute)
                {
                    _cachedSimpleVolume.Mute = false;
                }
                return true;
            }
        }, skipSimpleVolumeDispose: true);
    }

    public void InvalidateVolumeSessionCache()
    {
        lock (_volumeCacheLock)
        {
            InvalidateVolumeSessionCacheLocked();
        }
    }

    private void InvalidateVolumeSessionCacheLocked()
    {
        if (_cachedSimpleVolume != null)
        {
            try { _cachedSimpleVolume.Dispose(); } catch { }
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

    private bool TryWithAudioSession(string sourceAppId, Func<AudioSessionControl, bool> action, bool skipSimpleVolumeDispose = false)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
            return false;

        try
        {
            using var deviceEnumerator = new MMDeviceEnumerator();
            using var defaultDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = defaultDevice.AudioSessionManager.Sessions;
            if (sessions == null || sessions.Count == 0)
                return false;

            var candidateProcessNames = GetProcessNameCandidates(sourceAppId);
            var candidateProcessIds = GetCachedProcessIdsForSourceApp(sourceAppId, candidateProcessNames);
            AudioSessionControl? targetSession = null;
            double bestScore = double.MinValue;

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session == null || session.IsSystemSoundsSession)
                    continue;

                uint processId = session.GetProcessID;
                bool matchedByProcess = candidateProcessIds.Contains(processId);
                bool matchedByMetadata = SessionMatchesSourceAppId(session, sourceAppId);
                if (!matchedByProcess && !matchedByMetadata)
                    continue;

                double score = 0;
                if (matchedByProcess) score += 1000;
                if (matchedByMetadata) score += 200;

                if (string.Equals(sourceAppId, _lastMatchedSourceAppId, StringComparison.OrdinalIgnoreCase))
                {
                    if (processId != 0 && processId == _lastMatchedProcessId) score += 300;
                    if (string.Equals(session.GetSessionIdentifier, _lastMatchedSessionId, StringComparison.OrdinalIgnoreCase)) score += 400;
                }

                try
                {
                    score += session.AudioMeterInformation.MasterPeakValue * 100;
                }
                catch (Exception ex)
                {
                    RuntimeLog.Log("VOLUME-SCORE", ex.Message);
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    targetSession = session;
                }
            }

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

    internal static HashSet<string> GetProcessNameCandidates(string sourceAppId)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(sourceAppId))
            return candidates;

        foreach (Match match in Regex.Matches(sourceAppId, @"([A-Za-z0-9_\-]+)\.exe", RegexOptions.IgnoreCase))
        {
            string processName = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(processName))
                candidates.Add(processName);
        }

        AddAlias(sourceAppId, candidates, "spotify", "Spotify");
        AddAlias(sourceAppId, candidates, "msedge", "msedge");
        AddAlias(sourceAppId, candidates, "edge", "msedge");
        AddAlias(sourceAppId, candidates, "chrome", "chrome");
        AddAlias(sourceAppId, candidates, "firefox", "firefox");
        AddAlias(sourceAppId, candidates, "opera", "opera");
        AddAlias(sourceAppId, candidates, "brave", "brave");
        AddAlias(sourceAppId, candidates, "vivaldi", "vivaldi");
        AddAlias(sourceAppId, candidates, "coccoc", "browser");
        AddAlias(sourceAppId, candidates, "arc", "arc");
        AddAlias(sourceAppId, candidates, "sidekick", "sidekick");
        AddAlias(sourceAppId, candidates, "zen", "zen");
        AddAlias(sourceAppId, candidates, "applemusic", "AppleMusic");
        AddAlias(sourceAppId, candidates, "apple music", "AppleMusic");

        return candidates;
    }

    private static void AddAlias(string sourceAppId, ISet<string> candidates, string token, string processName)
    {
        if (sourceAppId.Contains(token, StringComparison.OrdinalIgnoreCase))
            candidates.Add(processName);
    }

    private HashSet<uint> GetCachedProcessIdsForSourceApp(string sourceAppId, IEnumerable<string> processNames)
    {
        bool canUseCache =
            string.Equals(sourceAppId, _cachedProcessSourceAppId, StringComparison.OrdinalIgnoreCase) &&
            (DateTime.UtcNow - _cachedProcessIdsAtUtc).TotalMilliseconds < 1200;

        if (canUseCache)
            return _cachedProcessIds;

        _cachedProcessSourceAppId = sourceAppId;
        _cachedProcessIds = GetProcessIds(processNames);
        _cachedProcessIdsAtUtc = DateTime.UtcNow;
        return _cachedProcessIds;
    }

    internal static HashSet<uint> GetProcessIds(IEnumerable<string> processNames)
    {
        var processIds = new HashSet<uint>();

        foreach (string processName in processNames)
        {
            if (string.IsNullOrWhiteSpace(processName))
                continue;

            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch (Exception ex)
            {
                RuntimeLog.Log("VOLUME-PID", ex.Message);
                continue;
            }

            foreach (var process in processes)
            {
                try
                {
                    processIds.Add((uint)process.Id);
                }
                catch { }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return processIds;
    }

    private static bool SessionMatchesSourceAppId(AudioSessionControl session, string sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
            return false;

        return ContainsEitherWay(session.DisplayName, sourceAppId) ||
               ContainsEitherWay(session.GetSessionIdentifier, sourceAppId) ||
               ContainsEitherWay(session.GetSessionInstanceIdentifier, sourceAppId);
    }

    private static bool ContainsEitherWay(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return left.Contains(right, StringComparison.OrdinalIgnoreCase) ||
               right.Contains(left, StringComparison.OrdinalIgnoreCase);
    }
}
