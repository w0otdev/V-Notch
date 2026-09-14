using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class ProgressEnginePreviousTrackTests
{
    [Fact]
    public void PreviousRequest_DoesNotOptimisticallyResetDisplayedPosition()
    {
        var engine = CreatePlayingEngine(TimeSpan.FromSeconds(60));

        engine.NotifyPreviousTrackRequested();

        Assert.InRange(engine.GetUiFrame().Position.TotalSeconds, 59.5, 61.0);
    }

    [Fact]
    public void PreviousRequest_AcceptsConfirmedZeroSnapshot()
    {
        var engine = CreatePlayingEngine(TimeSpan.FromSeconds(60));
        engine.NotifyPreviousTrackRequested();

        engine.OnMediaSnapshot(Snapshot(TimeSpan.Zero, sequence: 2));

        Assert.InRange(engine.GetUiFrame().Position.TotalSeconds, 0, 0.5);
    }

    [Fact]
    public void UnsolicitedZeroSnapshot_RemainsRejectedAsGlitch()
    {
        var engine = CreatePlayingEngine(TimeSpan.FromSeconds(60));

        engine.OnMediaSnapshot(Snapshot(TimeSpan.Zero, sequence: 2));

        Assert.InRange(engine.GetUiFrame().Position.TotalSeconds, 59.5, 61.0);
    }

    [Fact]
    public void UserSeekZero_AcceptsConfirmedZeroSnapshot()
    {
        var engine = CreatePlayingEngine(TimeSpan.FromSeconds(60));
        engine.NotifyUserSeek(TimeSpan.Zero);

        engine.OnMediaSnapshot(Snapshot(TimeSpan.Zero, sequence: 2));

        Assert.InRange(engine.GetUiFrame().Position.TotalSeconds, 0, 0.5);
    }

    [Fact]
    public void BackwardSeek_AcceptsSnapshot_WhenPredictedFarAhead()
    {
        var engine = CreatePlayingEngine(TimeSpan.FromSeconds(10));
        // Simulate playing for 60 seconds so predicted position is ~70s
        // then snapshot arrives at 12s (user seeked backward)
        var pastTimestamp = DateTime.UtcNow.AddSeconds(-60);
        engine.OnMediaSnapshot(new ProgressSnapshot
        {
            Position = TimeSpan.FromSeconds(10),
            Duration = TimeSpan.FromMinutes(3),
            IsPlaying = true,
            PlaybackRate = 1,
            IsSeekEnabled = true,
            Timestamp = pastTimestamp,
            SequenceNumber = 1
        });

        // Now snapshot at 12s with current timestamp
        engine.OnMediaSnapshot(new ProgressSnapshot
        {
            Position = TimeSpan.FromSeconds(12),
            Duration = TimeSpan.FromMinutes(3),
            IsPlaying = true,
            PlaybackRate = 1,
            IsSeekEnabled = true,
            Timestamp = DateTime.UtcNow,
            SequenceNumber = 2
        });

        Assert.InRange(engine.GetUiFrame().Position.TotalSeconds, 11.5, 13.0);
    }

    private static ProgressEngine CreatePlayingEngine(TimeSpan position)
    {
        var engine = new ProgressEngine();
        engine.OnMediaSnapshot(Snapshot(position, sequence: 1));
        return engine;
    }

    private static ProgressSnapshot Snapshot(TimeSpan position, long sequence) => new()
    {
        Position = position,
        Duration = TimeSpan.FromMinutes(3),
        IsPlaying = true,
        PlaybackRate = 1,
        IsSeekEnabled = true,
        Timestamp = DateTime.UtcNow,
        SequenceNumber = sequence
    };
}
