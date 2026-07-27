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
