using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

public sealed class SettingsApplicationServiceTests
{
    [Fact]
    public async Task ApplyAsync_PersistsSettings_UpdatesAutoStart_AndFiresSettingsApplied()
    {
        var fakeService = new FakeSettingsService();
        var fakeStartup = new FakeStartupManager();
        var appService = new SettingsApplicationService(fakeService, fakeStartup);

        var settings = new NotchSettings
        {
            Width = 500,
            Height = 60,
            AutoStart = true
        };

        NotchSettings? eventArgs = null;
        object? eventSender = null;
        appService.SettingsApplied += (sender, args) =>
        {
            eventSender = sender;
            eventArgs = args;
        };

        await appService.ApplyAsync(settings);

        Assert.Same(settings, fakeService.LastSaved);
        Assert.True(fakeStartup.AutoStartEnabled);
        Assert.Same(appService, eventSender);
        Assert.Same(settings, eventArgs);
    }

    [Fact]
    public async Task ApplyAsync_WhenAutoStartDisabled_UpdatesAutoStartAccordingly()
    {
        var fakeService = new FakeSettingsService();
        var fakeStartup = new FakeStartupManager { AutoStartEnabled = true };
        var appService = new SettingsApplicationService(fakeService, fakeStartup);

        var settings = new NotchSettings { AutoStart = false };
        await appService.ApplyAsync(settings);

        Assert.False(fakeStartup.AutoStartEnabled);
    }

    [Fact]
    public async Task ApplyAsync_WhenSaveThrows_DoesNotUpdateAutoStartOrFireEvent()
    {
        var failingService = new ThrowingSaveSettingsService(new IOException("Disk full"));
        var fakeStartup = new FakeStartupManager();
        var appService = new SettingsApplicationService(failingService, fakeStartup);

        bool eventFired = false;
        appService.SettingsApplied += (_, _) => eventFired = true;

        var settings = new NotchSettings { AutoStart = true };
        await Assert.ThrowsAsync<IOException>(() => appService.ApplyAsync(settings));

        Assert.False(fakeStartup.SetAutoStartCalled);
        Assert.False(eventFired);
    }

    [Fact]
    public async Task ApplyAsync_RespectsCancellationToken()
    {
        var fakeService = new FakeSettingsService();
        var fakeStartup = new FakeStartupManager();
        var appService = new SettingsApplicationService(fakeService, fakeStartup);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            appService.ApplyAsync(new NotchSettings { AutoStart = true }, cts.Token));

        Assert.Null(fakeService.LastSaved);
        Assert.False(fakeStartup.SetAutoStartCalled);
    }

    [Fact]
    public async Task ImportAsync_ImportsFromFile_AppliesSettings_AndReturnsResult()
    {
        var importedSettings = new NotchSettings { Width = 620, AutoStart = true };
        var fakeService = new FakeImportSettingsService(importedSettings, requiresRestart: true);
        var fakeStartup = new FakeStartupManager();
        var appService = new SettingsApplicationService(fakeService, fakeStartup);

        NotchSettings? eventArgs = null;
        appService.SettingsApplied += (_, args) => eventArgs = args;

        var result = await appService.ImportAsync("dummy_path.vns");

        Assert.Same(importedSettings, result.Settings);
        Assert.True(result.RequiresRestart);
        Assert.Same(importedSettings, fakeService.LastSaved);
        Assert.True(fakeStartup.AutoStartEnabled);
        Assert.Same(importedSettings, eventArgs);
    }

    [Fact]
    public void Export_DelegatesToSettingsService()
    {
        var fakeService = new FakeSettingsService();
        var appService = new SettingsApplicationService(fakeService, new FakeStartupManager());

        var settings = new NotchSettings { Width = 400 };
        appService.Export("export.vns", settings);

        Assert.Same(settings, fakeService.LastSaved);
    }

    [Fact]
    public void Load_And_IsAutoStartEnabled_DelegateCorrectly()
    {
        var initial = new NotchSettings { Width = 350 };
        var fakeService = new FakeSettingsService(initial);
        var fakeStartup = new FakeStartupManager { AutoStartEnabled = true };
        var appService = new SettingsApplicationService(fakeService, fakeStartup);

        Assert.Same(initial, appService.Load());
        Assert.True(appService.IsAutoStartEnabled());
    }

    private sealed class FakeStartupManager : IStartupManager
    {
        public bool AutoStartEnabled { get; set; }
        public bool SetAutoStartCalled { get; private set; }

        public bool IsAutoStartEnabled() => AutoStartEnabled;

        public void SetAutoStart(bool enable)
        {
            SetAutoStartCalled = true;
            AutoStartEnabled = enable;
        }
    }

    private sealed class ThrowingSaveSettingsService : ISettingsService
    {
        private readonly Exception _ex;
        public ThrowingSaveSettingsService(Exception ex) => _ex = ex;

        public NotchSettings Load() => new();
        public void Save(NotchSettings settings) => throw _ex;
        public Task SaveAsync(NotchSettings settings) => Task.FromException(_ex);
        public void ExportSettingsToFile(string filePath, NotchSettings settings) => throw _ex;
        public (NotchSettings Settings, bool RequiresRestart) ImportSettingsFromFile(string filePath, NotchSettings? currentSettings = null) => throw _ex;
    }

    private sealed class FakeImportSettingsService : ISettingsService
    {
        private readonly NotchSettings _imported;
        private readonly bool _restart;

        public NotchSettings? LastSaved { get; private set; }

        public FakeImportSettingsService(NotchSettings imported, bool requiresRestart)
        {
            _imported = imported;
            _restart = requiresRestart;
        }

        public NotchSettings Load() => new();
        public void Save(NotchSettings settings) => LastSaved = settings;
        public Task SaveAsync(NotchSettings settings) { LastSaved = settings; return Task.CompletedTask; }
        public void ExportSettingsToFile(string filePath, NotchSettings settings) => LastSaved = settings;
        public (NotchSettings Settings, bool RequiresRestart) ImportSettingsFromFile(string filePath, NotchSettings? currentSettings = null)
            => (_imported, _restart);
    }
}
