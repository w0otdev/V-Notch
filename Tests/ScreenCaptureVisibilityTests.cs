using System.Reflection;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ScreenCaptureVisibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullscreenPollingFreezesTheExistingVisibilityDuringCapture(bool hidden) => SharedStaTestRunner.Run(() =>
    {
        using var controller = new FullscreenAutoHideController(() => new IntPtr(1), new NotchSettings(), isVisibilityFrozen: () => true);
        typeof(FullscreenAutoHideController).GetField("_isHiddenByFullscreen", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, hidden);
        int changes = 0;
        controller.HideStateChanged += _ => changes++;
        Assert.False(controller.Evaluate(force: true));
        Assert.Equal(hidden, controller.IsHiddenByFullscreen);
        Assert.Equal(0, changes);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdlePollingCannotHideOrRevealTheNotchDuringCapture(bool hidden) => SharedStaTestRunner.Run(() =>
    {
        bool capture = true;
        var settings = new NotchSettings { EnableIdleAutoHide = true, IdleAutoHideDelay = 100 };
        var shell = new NotchShellState { IsHiddenByIdle = hidden };
        int changes = 0;
        using var controller = new IdleAutoHideController(shell, () => settings, () => true,
            () => changes++, () => { }, () => { }, isVisibilityFrozen: () => capture);
        controller.EvaluateIdleState(0);
        controller.EvaluateIdleState(10_000);
        controller.Wake();
        Assert.Equal(hidden, shell.IsHiddenByIdle);
        Assert.Equal(0, changes);
        capture = false;
        controller.EvaluateIdleState(11_000);
        Assert.Equal(hidden, shell.IsHiddenByIdle);
        controller.EvaluateIdleState(11_100);
        Assert.True(shell.IsHiddenByIdle);
    });
}
