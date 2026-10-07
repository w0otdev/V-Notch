using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowUpdateNotificationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void NewReleaseShowsAnimatedNotificationTooltipAndPulseThenClearsThem() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var service = new PendingUpdateService();
        using var fixture = CreateFixture(service);
        var window = fixture.Window;
        Field<NotchSettings>(window, "_settings").AutoCheckUpdates = true;
        var check = (Task)Invoke(window, "CheckForUpdatesAsync")!;
        service.Requests[0].SetResult(NewRelease("9.5.1"));
        await check;
        await WpfFrameWaiter.UntilAsync(() => Field<DispatcherTimer?>(window, "_updatePulseTimer")?.IsEnabled == true, "update notification appeared", ct);
        Assert.True(Field<bool>(window, "_isUpdateAvailable"));
        Assert.Equal(Visibility.Visible, window.UpdateNotificationButton.Visibility);
        Assert.Contains("9.5.1", window.UpdateNotificationButton.Tag.ToString());
        Invoke(window, "ShowUpdateNotification");
        Set(window, "_updatePulseStartedAtUtc", DateTime.UtcNow.AddMilliseconds(-1500));
        Invoke(window, "UpdatePulseTimer_Tick", null, EventArgs.Empty);
        Assert.InRange(window.UpdateIconBrush.Color.R, 250, 255);
        Invoke(window, "UpdateNotification_MouseEnter", window, new MouseEventArgs(Mouse.PrimaryDevice, 0));
        await WpfFrameWaiter.UntilAsync(() => window.UpdateInlineTooltip.Opacity == 1, "update tooltip shown", ct);
        Assert.True(Field<bool>(window, "_isUpdateTooltipOpen"));
        Assert.Contains("9.5.1", window.UpdateInlineStatusText.Text);
        Assert.NotNull(window.UpdateNotificationButton.CacheMode);
        Invoke(window, "UpdateNotification_MouseLeave", window, new MouseEventArgs(Mouse.PrimaryDevice, 0));
        await WpfFrameWaiter.UntilAsync(() => window.UpdateInlineTooltip.Visibility == Visibility.Collapsed && window.UpdateNotificationButton.CacheMode == null, "update hover dismissed", ct);
        Assert.False(Field<bool>(window, "_isUpdateTooltipOpen"));
        var currentCheck = (Task)Invoke(window, "CheckForUpdatesAsync")!;
        service.Requests[1].SetResult(new UpdateInfo { Version = "9.5.1", IsNewerVersion = false });
        await currentCheck;
        await WpfFrameWaiter.UntilAsync(() => window.UpdateNotificationButton.Visibility == Visibility.Collapsed, "current version clears notification", ct);
        Assert.Null(Field<UpdateInfo?>(window, "_availableUpdate"));
        Assert.False(Field<DispatcherTimer>(window, "_updatePulseTimer").IsEnabled);
        Assert.False(window.UpdateNotificationButton.IsHitTestVisible);
        Invoke(window, "ShowUpdateNotification");
        Invoke(window, "UpdatePulseTimer_Tick", null, EventArgs.Empty);
        Assert.Equal(Color.FromRgb(48, 209, 88), window.UpdateIconBrush.Color);
        Assert.Equal(0, service.InstallCalls);
    });

    [Fact]
    public void LatestCheckOwnsNotificationAndFailuresPreserveItsState() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var service = new PendingUpdateService();
        using var fixture = CreateFixture(service);
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        settings.AutoCheckUpdates = true;
        var older = (Task)Invoke(window, "CheckForUpdatesAsync")!;
        var newer = (Task)Invoke(window, "CheckForUpdatesAsync")!;
        service.Requests[1].SetResult(NewRelease("9.5.2"));
        await newer;
        service.Requests[0].SetResult(NewRelease("9.5.1"));
        await older;
        Assert.Equal("9.5.2", Field<UpdateInfo>(window, "_availableUpdate").Version);
        var failed = (Task)Invoke(window, "CheckForUpdatesAsync")!;
        service.Requests[2].SetException(new HttpRequestException("fixture offline"));
        await failed;
        Assert.Equal("9.5.2", Field<UpdateInfo>(window, "_availableUpdate").Version);
        var duringInstall = (Task)Invoke(window, "CheckForUpdatesAsync")!;
        Set(window, "_isUpdateInstalling", true);
        service.Requests[3].SetResult(NewRelease("9.5.3"));
        await duringInstall;
        Assert.Equal("9.5.2", Field<UpdateInfo>(window, "_availableUpdate").Version);
        await (Task)Invoke(window, "CheckForUpdatesAsync")!;
        Set(window, "_isUpdateInstalling", false);
        settings.EnableLocalOnlyMode = true;
        await (Task)Invoke(window, "CheckForUpdatesAsync")!;
        settings.EnableLocalOnlyMode = false;
        settings.AutoCheckUpdates = false;
        await (Task)Invoke(window, "CheckForUpdatesAsync")!;
        Assert.Equal(4, service.Requests.Count);
        Set(window, "_isUpdateAvailable", false);
        Invoke(window, "HideUpdateNotification");
        await WpfFrameWaiter.UntilAsync(() => window.UpdateNotificationButton.Visibility == Visibility.Collapsed, "notification cleanup", ct);
    });

    [Fact]
    public void RapidHideAndShowLeavesNewestReleaseVisible() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture(new PendingUpdateService());
        var window = fixture.Window;
        Set(window, "_availableUpdate", NewRelease("9.5.4"));
        Set(window, "_isUpdateAvailable", true);
        Invoke(window, "ShowUpdateNotification");
        Invoke(window, "ShowUpdateInlineTooltip");
        Invoke(window, "HideUpdateNotification");
        Invoke(window, "ShowUpdateNotification");
        await WpfFrameWaiter.UntilAsync(() => Field<DispatcherTimer?>(window, "_updatePulseTimer")?.IsEnabled == true, "latest update survives interrupted hide", ct);
        Assert.Equal(Visibility.Visible, window.UpdateNotificationButton.Visibility);
        Assert.True(window.UpdateNotificationButton.IsHitTestVisible);
        Assert.Equal(1, window.UpdateNotificationButton.Opacity);
        Set(window, "_isUpdateAvailable", false);
        Invoke(window, "HideUpdateNotification");
        await WpfFrameWaiter.UntilAsync(() => window.UpdateNotificationButton.Visibility == Visibility.Collapsed, "update hidden", ct);
    });

    [Fact]
    public void ManualUpdateCheckViaServiceOrSettingsRevealsUpdateNotificationEvenWhenAutoCheckDisabled() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var service = new PendingUpdateService();
        using var fixture = CreateFixture(service);
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        settings.AutoCheckUpdates = false;

        var backgroundCheck = (Task)Invoke(window, "CheckForUpdatesAsync")!;
        await backgroundCheck;
        Assert.False(Field<bool>(window, "_isUpdateAvailable"));

        service.RaiseUpdateCheckCompleted(NewRelease("9.5.5"));
        await WpfFrameWaiter.UntilAsync(() => Field<bool>(window, "_isUpdateAvailable") && window.UpdateNotificationButton.Visibility == Visibility.Visible, "update notification shown via event", ct);
        Assert.Equal("9.5.5", Field<UpdateInfo>(window, "_availableUpdate").Version);

        Invoke(window, "AnimateStatusBarReveal", true);
        Assert.Equal(Visibility.Visible, window.UpdateNotificationButton.Visibility);
        Assert.True(window.UpdateNotificationButton.IsHitTestVisible);
    });

    private static UpdateInfo NewRelease(string version) => new() { Version = version, IsNewerVersion = true };
    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture(IUpdateService updates) => new("en", greeting: false,
        configureServices: services => { services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()); services.AddSingleton(updates); });
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static object? Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
    private sealed class PendingUpdateService : IUpdateService
    {
        public List<TaskCompletionSource<UpdateInfo?>> Requests { get; } = new();
        public int InstallCalls { get; private set; }
        public string CurrentVersion => "9.5.0";
        public event EventHandler<UpdateInfo?>? UpdateCheckCompleted;
        public UpdateInfo? LatestUpdateInfo => null;
        public void RaiseUpdateCheckCompleted(UpdateInfo? info) => UpdateCheckCompleted?.Invoke(this, info);
        public Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            var request = new TaskCompletionSource<UpdateInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add(request);
            return request.Task;
        }
        public Task<IReadOnlyList<UpdateInfo>> GetAllReleasesAsync() => Task.FromResult<IReadOnlyList<UpdateInfo>>(Array.Empty<UpdateInfo>());
        public Task<bool> DownloadAndInstallUpdateAsync(UpdateInfo updateInfo, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            InstallCalls++;
            throw new InvalidOperationException("Notification tests must never install an update.");
        }
    }
}
