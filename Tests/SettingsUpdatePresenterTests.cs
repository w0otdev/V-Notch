using System.Windows;
using System.Windows.Controls;
using VNotch.Presenters;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SettingsUpdatePresenterTests
{
    [Fact]
    public async Task PendingCheckIsSharedAndLateCompletionDoesNotTouchClosedSettings()
    {
        var service = new PendingUpdateService();
        SettingsUpdatePresenter? presenter = null;
        SettingsUpdateViewRefs? refs = null;
        Task? check = null;
        string? statusAtClose = null;
        SharedStaTestRunner.Run(() =>
        {
            refs = new(new Window(), new TextBlock(), new Button(), new Button());
            presenter = new(service, refs, () => true);
            check = presenter.CheckForUpdatesAsync();
            Assert.False(check.IsCompleted);
            Assert.True(presenter.CheckForUpdatesAsync().IsCompleted);
            Assert.Equal(1, service.CheckCount);
            statusAtClose = refs.Status.Text;
            presenter.Dispose();
            presenter.Dispose();
        });
        service.Result.SetResult(new UpdateInfo { IsNewerVersion = true, Version = "99.0" });
        await check!.WaitAsync(TimeSpan.FromSeconds(5));
        SharedStaTestRunner.Run(() => Assert.Equal(statusAtClose, refs!.Status.Text));
    }

    [Fact]
    public void DisabledNetworkingNeverStartsAnUpdateCheck() => SharedStaTestRunner.Run(() =>
    {
        var service = new PendingUpdateService();
        using var presenter = new SettingsUpdatePresenter(service,
            new(new Window(), new TextBlock(), new Button(), new Button()), () => false);
        Assert.True(presenter.CheckForUpdatesAsync().IsCompleted);
        Assert.Equal(0, service.CheckCount);
    });

    private sealed class PendingUpdateService : IUpdateService
    {
        public int CheckCount { get; private set; }
        public TaskCompletionSource<UpdateInfo?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string CurrentVersion => "1.0";
        public event EventHandler<UpdateInfo?>? UpdateCheckCompleted { add { } remove { } }
        public UpdateInfo? LatestUpdateInfo => null;
        public Task<UpdateInfo?> CheckForUpdatesAsync() { CheckCount++; return Result.Task; }
        public Task<IReadOnlyList<UpdateInfo>> GetAllReleasesAsync() => Task.FromResult<IReadOnlyList<UpdateInfo>>([]);
        public Task<bool> DownloadAndInstallUpdateAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}
