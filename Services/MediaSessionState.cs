using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VNotch.Services;

public sealed partial class MediaSessionState
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnTimelineAdvanceState
    {
        public long LastTimelinePositionTicks;
        public long LastTimelineSampleUtcTicks;
        public long LastPositionAdvanceUtcTicks;
        public byte HasTimelinePosition;
        public byte HasTimelineSample;
        public byte HasPositionAdvance;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_mediasess_record_timeline_position(
        ref VnTimelineAdvanceState state,
        long positionTicks, long nowUtcTicks,
        out int outResult);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_mediasess_is_recently_advancing(
        in VnTimelineAdvanceState state,
        long nowUtcTicks, long windowTicks,
        out byte outAdvancing);

    private readonly Dictionary<string, SessionEntry> _sessions = new();
    private readonly Dictionary<string, string> _sourceOverrides = new();
    internal int SessionCount => _sessions.Count;
    internal int SourceOverrideCount => _sourceOverrides.Count;

    public SessionEntry GetOrCreate(string sessionKey)
    {
        if (!_sessions.TryGetValue(sessionKey, out var entry))
        {
            if (_sessions.Count >= 256) { using var e = _sessions.Keys.GetEnumerator(); if (e.MoveNext()) _sessions.Remove(e.Current); }
            entry = new SessionEntry();
            _sessions[sessionKey] = entry;
        }
        return entry;
    }

    public void SetLastPlayingTime(string sourceAppId, DateTime time)
    {
        GetOrCreate(sourceAppId).LastPlayingTime = time;
    }

    public bool TryGetLastPlayingTime(string sourceAppId, out DateTime time)
    {
        if (_sessions.TryGetValue(sourceAppId, out var entry) && entry.LastPlayingTime.HasValue)
        {
            time = entry.LastPlayingTime.Value;
            return true;
        }
        time = default;
        return false;
    }

    public void SetPlayStartTime(string sessionKey, DateTime time)
    {
        GetOrCreate(sessionKey).PlayStartTime = time;
    }

    public bool TryGetPlayStartTime(string sessionKey, out DateTime time)
    {
        if (_sessions.TryGetValue(sessionKey, out var entry) && entry.PlayStartTime.HasValue)
        {
            time = entry.PlayStartTime.Value;
            return true;
        }
        time = default;
        return false;
    }

    public void SetPlayingState(string sessionKey, bool isPlaying)
    {
        GetOrCreate(sessionKey).IsPlaying = isPlaying;
    }

    public bool GetPlayingState(string sessionKey)
    {
        return _sessions.TryGetValue(sessionKey, out var entry) && entry.IsPlaying;
    }

    public enum TimelineAdvanceResult
    {
        Indeterminate,
        Advanced,
        Stalled
    }

    public TimelineAdvanceResult RecordTimelinePosition(string sessionKey, TimeSpan position, DateTime nowUtc)
    {
        var entry = GetOrCreate(sessionKey);
        var state = new VnTimelineAdvanceState
        {
            LastTimelinePositionTicks = entry.LastTimelinePosition?.Ticks ?? 0,
            LastTimelineSampleUtcTicks = entry.LastTimelineSampleUtc?.Ticks ?? 0,
            LastPositionAdvanceUtcTicks = entry.LastPositionAdvanceUtc?.Ticks ?? 0,
            HasTimelinePosition = entry.LastTimelinePosition.HasValue ? (byte)1 : (byte)0,
            HasTimelineSample = entry.LastTimelineSampleUtc.HasValue ? (byte)1 : (byte)0,
            HasPositionAdvance = entry.LastPositionAdvanceUtc.HasValue ? (byte)1 : (byte)0
        };

        vn_mediasess_record_timeline_position(ref state, position.Ticks, nowUtc.Ticks, out int res);
        entry.LastTimelinePosition = state.HasTimelinePosition != 0 ? new TimeSpan(state.LastTimelinePositionTicks) : null;
        entry.LastTimelineSampleUtc = state.HasTimelineSample != 0 ? new DateTime(state.LastTimelineSampleUtcTicks, DateTimeKind.Utc) : null;
        entry.LastPositionAdvanceUtc = state.HasPositionAdvance != 0 ? new DateTime(state.LastPositionAdvanceUtcTicks, DateTimeKind.Utc) : null;
        return (TimelineAdvanceResult)res;
    }

    public bool IsRecentlyAdvancing(string sessionKey, DateTime nowUtc, TimeSpan window)
    {
        if (_sessions.TryGetValue(sessionKey, out var entry) && entry.LastPositionAdvanceUtc.HasValue)
        {
            var state = new VnTimelineAdvanceState
            {
                LastPositionAdvanceUtcTicks = entry.LastPositionAdvanceUtc.Value.Ticks,
                HasPositionAdvance = 1
            };
            return vn_mediasess_is_recently_advancing(in state, nowUtc.Ticks, window.Ticks, out byte adv) == 0 && adv != 0;
        }
        return false;
    }

    public void SetSourceOverride(string key, string source)
    {
        if (!string.IsNullOrEmpty(key))
        { if (!_sourceOverrides.ContainsKey(key) && _sourceOverrides.Count >= 256) { using var e = _sourceOverrides.Keys.GetEnumerator(); if (e.MoveNext()) _sourceOverrides.Remove(e.Current); } _sourceOverrides[key] = source; }
    }

    public bool TryGetSourceOverride(string key, out string source)
    {
        return _sourceOverrides.TryGetValue(key, out source!);
    }

    public void RemoveSourceOverride(string key)
    {
        if (!string.IsNullOrEmpty(key))
            _sourceOverrides.Remove(key);
    }

    public void Clear()
    {
        _sessions.Clear();
        _sourceOverrides.Clear();
    }

    public sealed class SessionEntry
    {
        public DateTime? LastPlayingTime { get; set; }

        public DateTime? PlayStartTime { get; set; }

        public bool IsPlaying { get; set; }

        public TimeSpan? LastTimelinePosition { get; set; }

        public DateTime? LastTimelineSampleUtc { get; set; }

        public DateTime? LastPositionAdvanceUtc { get; set; }
    }
}
