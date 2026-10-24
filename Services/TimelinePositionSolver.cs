using System.Runtime.InteropServices;

namespace VNotch.Services;

internal readonly struct TimelineSolveInputs
{
    public TimeSpan StartTime { get; init; }
    public TimeSpan EndTime { get; init; }
    public TimeSpan MaxSeekTime { get; init; }
    public TimeSpan Position { get; init; }
    public DateTimeOffset LastUpdatedUtc { get; init; }
    public bool IsInitialOrBigChange { get; init; }
    public bool IsNewTrack { get; init; }
    public bool IsBrowserTimelineTrack { get; init; }
    public bool IsLiveTimeline { get; init; }
    public bool IsPlaying { get; init; }
    public double PlaybackRate { get; init; }
    public DateTimeOffset NowUtc { get; init; }
}

internal readonly struct TimelineSolveResult
{
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset LastUpdatedUtc { get; init; }
    public bool IsIndeterminate { get; init; }
}

internal static partial class TimelinePositionSolver
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnTimelineSolveInputs
    {
        public long StartTimeTicks;
        public long EndTimeTicks;
        public long MaxSeekTimeTicks;
        public long PositionTicks;
        public long LastUpdatedUtcTicks;
        public long LastUpdatedOffsetTicks;
        public byte IsInitialOrBigChange;
        public byte IsNewTrack;
        public byte IsBrowserTimelineTrack;
        public byte IsLiveTimeline;
        public byte IsPlaying;
        public double PlaybackRate;
        public long NowUtcTicks;
        public long NowOffsetTicks;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnTimelineSolveResult
    {
        public long PositionTicks;
        public long DurationTicks;
        public long LastUpdatedUtcTicks;
        public long LastUpdatedOffsetTicks;
        public byte IsIndeterminate;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_timeline_solver_solve(in VnTimelineSolveInputs x, out VnTimelineSolveResult outRes);

    public static TimelineSolveResult Solve(in TimelineSolveInputs x)
    {
        var nativeIn = new VnTimelineSolveInputs
        {
            StartTimeTicks = x.StartTime.Ticks,
            EndTimeTicks = x.EndTime.Ticks,
            MaxSeekTimeTicks = x.MaxSeekTime.Ticks,
            PositionTicks = x.Position.Ticks,
            LastUpdatedUtcTicks = x.LastUpdatedUtc.UtcTicks,
            LastUpdatedOffsetTicks = x.LastUpdatedUtc.Offset.Ticks,
            IsInitialOrBigChange = x.IsInitialOrBigChange ? (byte)1 : (byte)0,
            IsNewTrack = x.IsNewTrack ? (byte)1 : (byte)0,
            IsBrowserTimelineTrack = x.IsBrowserTimelineTrack ? (byte)1 : (byte)0,
            IsLiveTimeline = x.IsLiveTimeline ? (byte)1 : (byte)0,
            IsPlaying = x.IsPlaying ? (byte)1 : (byte)0,
            PlaybackRate = x.PlaybackRate,
            NowUtcTicks = x.NowUtc.UtcTicks,
            NowOffsetTicks = x.NowUtc.Offset.Ticks,
        };
        _ = vn_timeline_solver_solve(in nativeIn, out VnTimelineSolveResult res);
        TimeSpan outOffset = TimeSpan.FromTicks(res.LastUpdatedOffsetTicks);
        return new TimelineSolveResult
        {
            Position = TimeSpan.FromTicks(res.PositionTicks),
            Duration = TimeSpan.FromTicks(res.DurationTicks),
            LastUpdatedUtc = res.LastUpdatedUtcTicks == 0
                ? default
                : new DateTimeOffset(res.LastUpdatedUtcTicks + outOffset.Ticks, outOffset),
            IsIndeterminate = res.IsIndeterminate != 0,
        };
    }
}
