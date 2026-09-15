using System;
using System.Collections.Generic;
using VNotch.Controllers;
using Xunit;

namespace VNotch.Tests;

public class CompactPresentationStressTests
{
    [Fact]
    public void VolumePauseVolume_RejectsPreviousRestoreEvenAfterNewVolumeEnds()
    {
        var arbiter = new CompactPillArbiter();
        int volume = arbiter.TryAcquire(CompactPillSlot.Volume).Token;
        arbiter.Release(volume); // Middle-click dismisses volume before showing playback.
        long pauseRestore = arbiter.Revision;
        Assert.True(arbiter.CanRestoreMedia(pauseRestore));
        int nextVolume = arbiter.TryAcquire(CompactPillSlot.Volume).Token;
        Assert.False(arbiter.CanRestoreMedia(pauseRestore));
        arbiter.Release(nextVolume);
        Assert.False(arbiter.CanRestoreMedia(pauseRestore));
        Assert.True(arbiter.CanRestoreMedia(arbiter.Revision));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(173)]
    [InlineData(2026)]
    public void RapidInteractionsAndDelayedCallbacks_OnlyCurrentOwnerCanCommit(int seed)
    {
        var random = new Random(seed);
        var arbiter = new CompactPillArbiter();
        var callbacks = new List<(int Token, long Revision)>();
        for (int i = 0; i < 10000; i++)
        {
            switch (random.Next(4))
            {
                case 0:
                case 1:
                    var result = arbiter.TryAcquire((CompactPillSlot)random.Next(1, 6));
                    if (result.Won) callbacks.Add((result.Token, arbiter.Revision));
                    break;
                case 2:
                    arbiter.Release(arbiter.ActiveToken);
                    callbacks.Add((0, arbiter.Revision));
                    break;
                default:
                    arbiter.ForceClear(); // Expand, close, or reset the compact view.
                    break;
            }

            if (callbacks.Count == 0) continue;
            var callback = callbacks[random.Next(callbacks.Count)];
            var owner = arbiter.ActiveSlot;
            int ownerToken = arbiter.ActiveToken;
            bool current = callback.Token != 0 && callback.Token == ownerToken;
            Assert.Equal(current, arbiter.IsTokenCurrent(callback.Token));
            Assert.Equal(owner == CompactPillSlot.None && callback.Revision == arbiter.Revision,
                arbiter.CanRestoreMedia(callback.Revision));
            if (!current)
            {
                arbiter.Release(callback.Token);
                Assert.Equal(owner, arbiter.ActiveSlot);
                Assert.Equal(ownerToken, arbiter.ActiveToken);
            }
        }
    }
}
