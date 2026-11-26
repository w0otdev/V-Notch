using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("AudioDevice")]
public class NativeAudioPlayersTests
{
    [Fact]
    public void WhiteNoiseService_PlayStopLifecycle_DoesNotThrowOrCrash()
    {
        var exception = Record.Exception(() =>
        {
            WhiteNoiseService.Volume = 0.0f;
            WhiteNoiseService.Toggle(WhiteNoiseService.Catalog[0]);
            WhiteNoiseService.Pause();
            Thread.Sleep(350);
        });

        Assert.Null(exception);
    }
}
