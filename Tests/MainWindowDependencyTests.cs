using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Media;
using VNotch.Models;
using VNotch.Controllers;
using VNotch.Contracts;
using VNotch.Services;
using VNotch.Tests.Fakes;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

public sealed class MainWindowDependencyTests
{
    [Fact]
    public void ClosingMainWindowLeavesInjectedDependenciesAliveUntilProviderDisposal() => SharedStaTestRunner.Run(() =>
    {
        if (Application.Current == null)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources["SFProDisplay"] = new FontFamily("Segoe UI");
            app.Resources["SFProText"] = new FontFamily("Segoe UI");
            app.Resources["IconFont"] = new FontFamily("Segoe MDL2 Assets");
        }
        var settings = new FakeSettingsService(new NotchSettings
        {
            EnableSpotlight = false, AutoCheckUpdates = false, EnableWeather = false,
            KeepMediaPinnedOnTrackChange = true, EnableSmartCrop = true
        });
        var media = new FakeMediaDetectionService();
        var spotlight = new TrackingSpotlightController();
        var moduleHost = new TrackingModuleHost();
        var services = new ServiceCollection();
        ServiceConfigurator.ConfigureServices(services);
        services.AddSingleton<ISettingsService>(settings);
        // Factory registrations make the container own these tracking instances.
        services.AddSingleton<IMediaDetectionService>(_ => media);
        services.AddSingleton<ISpotlightController>(_ => spotlight);
        services.AddSingleton<IModuleLifecycleManager>(_ => moduleHost);
        services.AddSingleton<IDispatcherService>(new FakeDispatcherService());
        using var provider = services.BuildServiceProvider();
        var window = provider.GetRequiredService<MainWindow>();
        try
        {
            Assert.Same(provider.GetRequiredService<ShellViewModel>(), window.DataContext);
            Assert.Same(settings, provider.GetRequiredService<ISettingsService>());
            Assert.Same(media, provider.GetRequiredService<IMediaDetectionService>());

            var viewModel = provider.GetRequiredService<ShellViewModel>();
            var coordinator = provider.GetRequiredService<NotchTransitionCoordinator>();
            int mediaUpdates = 0;
            viewModel.MediaInfoUpdated += (_, _) => mediaUpdates++;
            window.Close();

            Assert.Equal(0, media.DisposeCount);
            Assert.Equal(0, spotlight.DisposeCount);
            Assert.Equal(0, moduleHost.DisposeCount);
            Assert.Null(viewModel.IsExpandedCheck);
            Assert.Null(coordinator.CanInitiateTransition);
            media.RaiseMediaChanged(new MediaInfo { CurrentTrack = "Still running", IsAnyMediaPlaying = true });
            Assert.Equal(1, mediaUpdates);
            coordinator.RequestView(NotchView.Timer, "AfterWindowClose");
            Assert.Equal(NotchView.Timer, coordinator.Snapshot.TargetView);

            provider.Dispose();
            Assert.Equal(1, media.DisposeCount);
            Assert.Equal(1, spotlight.DisposeCount);
            Assert.Equal(1, moduleHost.DisposeCount);
            media.RaiseMediaChanged(new MediaInfo { CurrentTrack = "After disposal" });
            Assert.Equal(1, mediaUpdates);
        }
        finally { window.Close(); }
    });

    private sealed class TrackingSpotlightController : ISpotlightController
    {
        public int DisposeCount { get; private set; }
        public bool IsHotkeyRegistered => false;
        public void Initialize(Window host, NotchSettings settings) { }
        public void ApplySettings(NotchSettings settings) { }
        public void Dispose() => DisposeCount++;
    }

    private sealed class TrackingModuleHost : IModuleLifecycleManager
    {
        public int DisposeCount { get; private set; }
        public IReadOnlyCollection<INotchModule> Modules => Array.Empty<INotchModule>();
        public void Register(INotchModule module) { }
        public void InitializeAll() { }
        public void StartAll() { }
        public void StopAll() { }
        public T? Get<T>() where T : class, INotchModule => null;
        public void Dispose() => DisposeCount++;
    }
}
