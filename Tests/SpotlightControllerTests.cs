using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using Xunit;
    [Fact]
    public void EscapeRecoversAcrossClosingAndReopeningWhenKeyUpIsMissing()
    {
        var state = new SpotlightKeyboardState();
        var escape = new Win32Interop.KBDLLHOOKSTRUCT { vkCode = 0x1B };
        state.Configure(true, true, true);
        Assert.Equal(SpotlightKeyboardAction.Escape, state.Process(escape, Win32Interop.WM_KEYDOWN));
        Assert.Equal(SpotlightKeyboardAction.Suppress, state.Process(escape, Win32Interop.WM_KEYDOWN));
        state.Configure(true, true, false);
        // The hook sees no intervening key-up or other keyboard event.
        state.Configure(true, true, true);
        Assert.Equal(SpotlightKeyboardAction.Escape, state.Process(escape, Win32Interop.WM_KEYDOWN));
        state.Configure(true, true, false);
        Assert.Equal(SpotlightKeyboardAction.None, state.Process(escape, Win32Interop.WM_KEYUP));
    }

    [Fact]
    public void FallbackDoesNotRepeatWhenOpeningAndDisablingStopsSuppression()
    {
        var state = new SpotlightKeyboardState();
        var space = new Win32Interop.KBDLLHOOKSTRUCT
        {
            vkCode = Win32Interop.VK_SPACE, flags = Win32Interop.LLKHF_ALTDOWN, time = 1000
        };
        state.Configure(true, true, false);
        Assert.Equal(SpotlightKeyboardAction.Toggle, state.Process(space, Win32Interop.WM_SYSKEYDOWN));
        state.Configure(true, true, true);
        space.time = 1100;
        Assert.Equal(SpotlightKeyboardAction.Suppress, state.Process(space, Win32Interop.WM_SYSKEYDOWN));
        state.Configure(false, false, false);
        Assert.Equal(SpotlightKeyboardAction.None, state.Process(space, Win32Interop.WM_SYSKEYUP));
    }

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SpotlightControllerTests
{
    [Fact]
    public void ClosingHostLeavesControllerAvailableForAnotherHost() => SharedStaTestRunner.Run(() =>
    {
        using var controller = new SpotlightController(() => throw new InvalidOperationException("Spotlight is disabled"));
        var firstHost = new BackgroundWindow();
        var secondHost = new BackgroundWindow();
        try
        {
            controller.Initialize(firstHost, new NotchSettings { EnableSpotlight = false });
            firstHost.Close();
            controller.Initialize(secondHost, new NotchSettings { EnableSpotlight = false });
            Assert.False(controller.IsHotkeyRegistered);
            secondHost.Close();
            controller.ApplySettings(new NotchSettings { EnableSpotlight = false });
            controller.Dispose();
            Assert.Throws<ObjectDisposedException>(() =>
                controller.Initialize(secondHost, new NotchSettings { EnableSpotlight = false }));
        }
        finally
        {
            firstHost.Close();
            secondHost.Close();
        }
    });

    [Theory]
    [InlineData(Win32Interop.VK_SPACE, Win32Interop.LLKHF_ALTDOWN, true)]
    [InlineData(Win32Interop.VK_SPACE, 0, false)]
    [InlineData(0x41, Win32Interop.LLKHF_ALTDOWN, false)]
    public void KeyboardFallback_OnlyRecognizesAltSpace(uint key, uint flags, bool expected)
    {
        Assert.Equal(expected, SpotlightController.IsAltSpaceKey(key, flags));
    }

    [Theory]
    [InlineData(0x1Bu, true)]
    [InlineData(Win32Interop.VK_SPACE, false)]
    [InlineData(0x41u, false)]
    public void GlobalDismiss_OnlyRecognizesEscape(uint key, bool expected)
    {
        Assert.Equal(expected, SpotlightController.IsEscapeKey(key));
    }

    [Theory]
    [InlineData(false, 1000u, 1001u, true)]
    [InlineData(true, 1000u, 1200u, false)]
    [InlineData(true, 1000u, 1600u, true)]
    [InlineData(true, uint.MaxValue - 100u, 450u, true)]
    public void KeyboardFallback_RecoversWhenAnotherHookSwallowsKeyUp(
        bool spaceIsAlreadyDown,
        uint lastSpaceEventTime,
        uint currentTime,
        bool expected)
    {
        Assert.Equal(expected, SpotlightController.ShouldDispatchFallbackToggle(
            spaceIsAlreadyDown,
            lastSpaceEventTime,
            currentTime));
    }

}
