using System.Diagnostics;
using System.Reflection;
using System.Windows;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class IdleAutoHideControllerTests
{
    [Theory]
    [InlineData(100)]
    [InlineData(300)]
    [InlineData(500)]
    public void ShortDelaysHideAtTheirDeadlineAndActivityRestartsTheCountdown(int delay) => SharedStaTestRunner.Run(() =>
    {
        var settings = new NotchSettings { EnableIdleAutoHide = true, IdleAutoHideDelay = delay };
        var shell = new NotchShellState();
        bool idle = true;
        int changes = 0, stops = 0;
        using var controller = new IdleAutoHideController(shell, () => settings, () => idle,
            () => changes++, () => stops++, () => { });

        controller.EvaluateIdleState(0);
        controller.EvaluateIdleState(delay - 1);
        Assert.False(shell.IsHiddenByIdle);
        Assert.Equal(TimeSpan.FromMilliseconds(1), controller.Timer.Interval);
        idle = false;
        controller.EvaluateIdleState(delay);
        idle = true;
        controller.EvaluateIdleState(delay + 10);
        controller.EvaluateIdleState(2 * delay + 9);
        Assert.False(shell.IsHiddenByIdle);
        controller.EvaluateIdleState(2 * delay + 10);
        Assert.True(shell.IsHiddenByIdle);
        Assert.Equal(1, changes);
        Assert.Equal(1, stops);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WakeIsImmediateAndRespectsOtherVisibilityReasons(bool fullscreen) => SharedStaTestRunner.Run(() =>
    {
        var settings = new NotchSettings { EnableIdleAutoHide = true, IdleAutoHideDelay = 100 };
        var shell = new NotchShellState { IsHiddenByFullscreen = fullscreen };
        int reveals = 0;
        using var controller = new IdleAutoHideController(shell, () => settings, () => true,
            () => { }, () => { }, () => reveals++);
        controller.EvaluateIdleState(0);
        controller.EvaluateIdleState(100);
        Assert.True(shell.IsHiddenByIdle);

        controller.Wake();
        Assert.False(shell.IsHiddenByIdle);
        Assert.Equal(fullscreen ? 0 : 1, reveals);
        controller.EvaluateIdleState(500);
        controller.EvaluateIdleState(599);
        Assert.False(shell.IsHiddenByIdle);
        controller.EvaluateIdleState(600);
        Assert.True(shell.IsHiddenByIdle);
    });

    [Fact]
    public void DisablingRevealsAndDisposalIgnoresLateActivity() => SharedStaTestRunner.Run(() =>
    {
        var settings = new NotchSettings { EnableIdleAutoHide = true, IdleAutoHideDelay = 100 };
        var shell = new NotchShellState();
        int changes = 0;
        using var controller = new IdleAutoHideController(shell, () => settings, () => true,
            () => changes++, () => { }, () => { });
        controller.ApplySettings();
        Assert.True(controller.Timer.IsEnabled);
        controller.Wake();
        controller.EvaluateIdleState(0);
        controller.EvaluateIdleState(100);
        settings.EnableIdleAutoHide = false;
        controller.ApplySettings();
        Assert.False(shell.IsHiddenByIdle);
        Assert.False(controller.Timer.IsEnabled);
        Assert.Equal(2, changes);

        controller.Dispose();
        settings.EnableIdleAutoHide = true;
        controller.ApplySettings();
        controller.EvaluateIdleState(1000);
        controller.Wake();
        Assert.False(controller.Timer.IsEnabled);
        Assert.Equal(2, changes);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PointerRevealsCollapsedNotchWithoutWaitingForIdlePoll(bool fullscreen, bool topEdge) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            s => { s.EnableIdleAutoHide = true; s.IdleAutoHideDelay = 100; s.EnableLocalOnlyMode = true; });
        var window = fixture.Window;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var shell = (NotchShellState)typeof(MainWindow).GetField("_shellState", flags)!.GetValue(window)!;
        var manager = (NotchManager)typeof(MainWindow).GetField("_notchManager", flags)!.GetValue(window)!;
        var controller = (IdleAutoHideController)typeof(MainWindow).GetField("_idleAutoHide", flags)!.GetValue(window)!;
        manager.HoverService.UpdateNotchBounds(-2000, -1000, 300, 40);
        manager.HoverService.ResetHoverState();
        typeof(MainWindow).GetMethod("ApplyIdleAutoHideSettings", flags)!.Invoke(window, null);
        controller.Timer.Stop();
        shell.IsHiddenByIdle = true;
        shell.IsHiddenByFullscreen = fullscreen;
        typeof(MainWindow).GetMethod("ApplyNotchVisibilityState", flags)!.Invoke(window, null);
        window.NotchContainer.Visibility = Visibility.Collapsed;
        window.NotchContainerTranslate.Y = -50;

        var point = topEdge ? new Point(-2020, -999) : new Point(-1850, -990);
        manager.HoverService.ProcessMousePosition(point, Stopwatch.GetTimestamp());
        Assert.False(shell.IsHiddenByIdle);
        Assert.Equal(fullscreen ? Visibility.Collapsed : Visibility.Visible, window.NotchContainer.Visibility);
        controller.EvaluateIdleState(0);
        controller.EvaluateIdleState(1000);
        Assert.False(shell.IsHiddenByIdle); // A stationary pointer keeps the revealed notch open.
    });
}
