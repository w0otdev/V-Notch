using VNotch.Models;

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

internal static class TimelinePositionSolver
{
    public static TimelineSolveResult Solve(in TimelineSolveInputs x)
    {
        var duration = x.EndTime - x.StartTime;
        if (duration <= TimeSpan.Zero) duration = x.MaxSeekTime;
        bool indeterminate = MediaTimelinePolicy.IsIndeterminate(duration, x.IsLiveTimeline);
        if (!MediaTimelinePolicy.HasFiniteDuration(duration)) duration = TimeSpan.Zero;

        bool isNearlyEnd = duration.TotalSeconds > 0 &&
            (duration - x.Position).TotalMilliseconds < 800;
        bool isBrowserLiveEdge = x.IsBrowserTimelineTrack && duration > TimeSpan.Zero &&
            duration - x.Position <= TimeSpan.FromSeconds(5);

        TimeSpan chosenPosition;
        bool forceStartPosition = false;
        if (!indeterminate && !x.IsBrowserTimelineTrack && x.IsNewTrack && isNearlyEnd)
        {
            chosenPosition = TimeSpan.Zero;
            forceStartPosition = true;
        }
        else
        {
            chosenPosition = x.Position;
        }

        if (!indeterminate && !isBrowserLiveEdge && x.IsBrowserTimelineTrack && x.IsNewTrack && x.IsPlaying)
        {
            TimeSpan timelineAge = x.NowUtc - x.LastUpdatedUtc;
            if (timelineAge < TimeSpan.Zero) timelineAge = TimeSpan.Zero;

            bool suspiciousCarryOverPosition =
                chosenPosition.TotalSeconds > 20 &&
                (duration.TotalSeconds <= 0 ||
                 chosenPosition.TotalSeconds > duration.TotalSeconds * 0.2);
            bool staleTimelineAtTrackStart = timelineAge > TimeSpan.FromMilliseconds(900);

            if (suspiciousCarryOverPosition && staleTimelineAtTrackStart)
            {
                chosenPosition = TimeSpan.Zero;
                forceStartPosition = true;
            }
        }

        var rawTimelineUpdatedUtc = x.LastUpdatedUtc;
        var timelineUpdatedUtc = forceStartPosition
            ? x.NowUtc
            : rawTimelineUpdatedUtc;

        if (!indeterminate && !forceStartPosition && x.IsPlaying)
        {
            var nowUpdatedUtc = x.NowUtc;
            var timelineLatency = nowUpdatedUtc - rawTimelineUpdatedUtc;
            var maxCompensationWindow = TimeSpan.FromSeconds(15);
            if (x.IsBrowserTimelineTrack)
            {
                if (x.IsInitialOrBigChange)
                {
                    maxCompensationWindow = DurationCompensationWindow(duration);
                }
                else
                {
                    maxCompensationWindow = TimeSpan.FromMinutes(2);
                }
            }
            else if (x.IsInitialOrBigChange)
            {
                maxCompensationWindow = DurationCompensationWindow(duration);
            }

            bool validTimelineTimestamp =
                rawTimelineUpdatedUtc > DateTimeOffset.MinValue &&
                rawTimelineUpdatedUtc <= nowUpdatedUtc.AddMilliseconds(250) &&
                timelineLatency >= TimeSpan.Zero &&
                timelineLatency <= maxCompensationWindow;

            if (validTimelineTimestamp && timelineLatency.TotalMilliseconds > 100)
            {
                double playbackRate = x.PlaybackRate > 0 ? x.PlaybackRate : 1.0;
                var compensatedPosition = chosenPosition + TimeSpan.FromSeconds(timelineLatency.TotalSeconds * playbackRate);

                if (duration > TimeSpan.Zero)
                {
                    TimeSpan maxAllowed = duration;
                    if (compensatedPosition > maxAllowed)
                        compensatedPosition = maxAllowed;
                }

                if (compensatedPosition > chosenPosition)
                {
                    chosenPosition = compensatedPosition;
                    timelineUpdatedUtc = nowUpdatedUtc;
                }
            }
        }

        if (chosenPosition < TimeSpan.Zero)
        {
            chosenPosition = TimeSpan.Zero;
        }
        if (duration > TimeSpan.Zero && chosenPosition > duration)
        {
            chosenPosition = duration;
        }

        return new TimelineSolveResult
        {
            Position = chosenPosition,
            Duration = duration,
            LastUpdatedUtc = timelineUpdatedUtc,
            IsIndeterminate = indeterminate,
        };
    }

    private static TimeSpan DurationCompensationWindow(TimeSpan duration)
    {
        var durationWindow = duration > TimeSpan.Zero
            ? duration + TimeSpan.FromSeconds(5)
            : TimeSpan.FromMinutes(10);
        return durationWindow < TimeSpan.FromHours(4)
            ? durationWindow
            : TimeSpan.FromHours(4);
    }
}
