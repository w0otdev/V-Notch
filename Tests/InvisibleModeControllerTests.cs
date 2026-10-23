using System.IO;
using VNotch.Controllers;
using VNotch.Models;
using Xunit;

namespace VNotch.Tests;

public sealed class InvisibleModeControllerTests
{
    [Fact]
    public async Task RepeatedShortcutCannotOverlapAnUnfinishedSave()
    {
        var settings = new NotchSettings();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int saves = 0, applied = 0;
        var controller = new InvisibleModeController(() => settings, () => applied++, snapshot =>
        {
            saves++;
            Assert.True(snapshot.InvisibleMode);
            Assert.NotSame(settings, snapshot);
            return completion.Task;
        }, () => Task.FromResult(false));
        Task first = controller.ToggleAsync();
        await controller.ToggleAsync();
        Assert.True(settings.InvisibleMode);
        Assert.Equal(1, saves);
        Assert.Equal(1, applied);
        completion.SetResult();
        await first;
    }

    [Fact]
    public async Task OpenSettingsOwnsTheCommandAndFailureDoesNotStrandTheGuard()
    {
        int attempts = 0;
        var controller = new InvisibleModeController(() => throw new Exception("No direct settings read"),
            () => throw new Exception("No direct UI apply"), _ => throw new Exception("No direct save"), () =>
            {
                if (++attempts == 1) throw new IOException("Failed save");
                return Task.FromResult(true);
            });
        await Assert.ThrowsAsync<IOException>(() => controller.ToggleAsync());
        await controller.ToggleAsync();
        Assert.Equal(2, attempts);
    }
}
