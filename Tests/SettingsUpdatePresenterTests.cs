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
        public Task<UpdateInfo?> CheckForUpdatesAsync(bool includePrereleases = false) { CheckCount++; return Result.Task; }
        public Task<IReadOnlyList<UpdateInfo>> GetAllReleasesAsync() => Task.FromResult<IReadOnlyList<UpdateInfo>>([]);
        public Task<bool> DownloadAndInstallUpdateAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    [Fact]
    public void ChangingChannelDuringPendingCheckDiscardsOldResultAndChecksNewChannel() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool includePrereleases = true;
        var service = new ChannelUpdateService();
        var refs = new SettingsUpdateViewRefs(new Window(), new TextBlock(), new Button(), new Button());
        using var presenter = new SettingsUpdatePresenter(service, refs, () => true, includePrereleases: () => includePrereleases);
        var detected = new List<UpdateInfo?>();
        presenter.UpdateDetected += (_, update) => detected.Add(update);
        var pending = presenter.CheckForUpdatesAsync();
        includePrereleases = false;
        await presenter.RefreshUpdateChannelAsync();
        service.Requests[0].SetResult(new() { Version = "99.1.0-beta.1", IsNewerVersion = true, IsPrerelease = true });
        await WpfFrameWaiter.UntilAsync(() => service.Requests.Count == 2, "new update channel checked", ct);
        Assert.Empty(detected);
        Assert.Equal(Visibility.Collapsed, refs.Download.Visibility);
        Assert.Equal(new[] { true, false }, service.Channels);
        service.Requests[1].SetResult(new() { Version = "99.0.0", IsNewerVersion = true });
        await pending;
        Assert.Equal("99.0.0", Assert.Single(detected)!.Version);
        Assert.Equal(Visibility.Visible, refs.Download.Visibility);
    });

    private sealed class ChannelUpdateService : IUpdateService
    {
        public List<bool> Channels { get; } = [];
        public List<TaskCompletionSource<UpdateInfo?>> Requests { get; } = [];
        public string CurrentVersion => "1.0";
        public event EventHandler<UpdateInfo?>? UpdateCheckCompleted { add { } remove { } }
        public UpdateInfo? LatestUpdateInfo => null;
        public Task<UpdateInfo?> CheckForUpdatesAsync(bool includePrereleases = false)
        {
            Channels.Add(includePrereleases);
            var request = new TaskCompletionSource<UpdateInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add(request);
            return request.Task;
        }
        public Task<IReadOnlyList<UpdateInfo>> GetAllReleasesAsync() => Task.FromResult<IReadOnlyList<UpdateInfo>>([]);
        public Task<bool> DownloadAndInstallUpdateAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StableReleaseEnablesDownloadRegardlessOfBetaPreference(bool includePrereleases) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var service = new ChannelUpdateService();
        var refs = new SettingsUpdateViewRefs(new Window(), new TextBlock(), new Button(), new Button());
        using var presenter = new SettingsUpdatePresenter(service, refs, () => true, includePrereleases: () => includePrereleases);
        var detected = new List<UpdateInfo?>();
        presenter.UpdateDetected += (_, update) => detected.Add(update);
        var check = presenter.CheckForUpdatesAsync();
        service.Requests[0].SetResult(new() { Version = "2.0.1", IsNewerVersion = true, IsPrerelease = false });
        await check;
        Assert.Equal(includePrereleases, Assert.Single(service.Channels));
        Assert.Equal("2.0.1", Assert.Single(detected)!.Version);
        Assert.Equal(Visibility.Visible, refs.Download.Visibility);
        Assert.True(refs.Check.IsEnabled);
    });
}
