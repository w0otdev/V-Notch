using System;
using System.IO;
using System.Threading.Tasks;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task ApplyAsync_PersistsSettings_UpdatesValueAndDerived_AndRaisesAppliedEvent()
    {
        var initialSettings = new NotchSettings { Width = 300, Height = 40, CornerRadius = 15 };
        var fakeService = new FakeSettingsService(initialSettings);
        var vm = new SettingsViewModel(fakeService);

        var newSettings = new NotchSettings
        {
            Width = 450,
            Height = 60,
            CornerRadius = 25,
            EnableDynamicIslandMode = false
        };

        NotchSettings? eventArg = null;
        object? eventSender = null;
        vm.Applied += (sender, args) =>
        {
            eventSender = sender;
            eventArg = args;
        };

        await vm.ApplyAsync(newSettings);

        // Verification: Service received save
        Assert.Same(newSettings, fakeService.LastSaved);

        // Verification: ViewModel updated state
        Assert.Same(newSettings, vm.Value);
        Assert.Equal(450, vm.CollapsedWidth);
        Assert.Equal(60, vm.CollapsedHeight);
        Assert.Equal(25, vm.CornerRadiusCollapsed);

        // Verification: Applied event fired after successful save
        Assert.Same(vm, eventSender);
        Assert.Same(newSettings, eventArg);
    }

    [Fact]
    public async Task ApplyAsync_WhenSaveFails_DoesNotUpdateValueOrDerived_AndDoesNotRaiseApplied()
    {
        var initialSettings = new NotchSettings { Width = 300, Height = 40, CornerRadius = 15 };
        var failingService = new ThrowingSettingsService(new IOException("Disk write failed"));
        var vm = new SettingsViewModel(failingService);

        var newSettings = new NotchSettings { Width = 500, Height = 70, CornerRadius = 30 };

        bool eventFired = false;
        vm.Applied += (_, _) => eventFired = true;

        await Assert.ThrowsAsync<IOException>(() => vm.ApplyAsync(newSettings));

        // State remains unchanged
        Assert.Equal(300, vm.Value.Width);
        Assert.Equal(300, vm.CollapsedWidth);
        Assert.False(eventFired);
    }

    [Fact]
    public void Apply_Synchronous_PersistsSettings_UpdatesValueAndDerived_AndRaisesApplied()
    {
        var initialSettings = new NotchSettings { Width = 300, Height = 40, CornerRadius = 15 };
        var fakeService = new FakeSettingsService(initialSettings);
        var vm = new SettingsViewModel(fakeService);

        var newSettings = new NotchSettings
        {
            Width = 480,
            Height = 55,
            CornerRadius = 20,
            EnableDynamicIslandMode = true,
            DynamicIslandWidth = 200
        };

        NotchSettings? eventArg = null;
        vm.Applied += (_, args) => eventArg = args;

        vm.Apply(newSettings);

        Assert.Same(newSettings, fakeService.LastSaved);
        Assert.Same(newSettings, vm.Value);
        Assert.Equal(200, vm.CollapsedWidth);
        Assert.Same(newSettings, eventArg);
    }

    [Fact]
    public async Task SaveAsync_DelegatesToServiceSaveAsync()
    {
        var initialSettings = new NotchSettings { Width = 300, Height = 40 };
        var fakeService = new FakeSettingsService(initialSettings);
        var vm = new SettingsViewModel(fakeService);

        var settingsToSave = new NotchSettings { Width = 600, Height = 80 };
        await vm.SaveAsync(settingsToSave);

        Assert.Same(settingsToSave, fakeService.LastSaved);
        // SaveAsync does not mutate Value directly
        Assert.NotSame(settingsToSave, vm.Value);
    }

    [Fact]
    public void DynamicIslandMode_TogglesCollapsedWidth()
    {
        var settingsIsland = new NotchSettings
        {
            Width = 400,
            DynamicIslandWidth = 180,
            EnableDynamicIslandMode = true
        };
        var vm = new SettingsViewModel(new FakeSettingsService(settingsIsland));
        Assert.Equal(180, vm.CollapsedWidth);

        var settingsStandard = new NotchSettings
        {
            Width = 400,
            DynamicIslandWidth = 180,
            EnableDynamicIslandMode = false
        };
        vm.Apply(settingsStandard);
        Assert.Equal(400, vm.CollapsedWidth);
    }

    private sealed class ThrowingSettingsService : ISettingsService
    {
        private readonly Exception _exception;

        public ThrowingSettingsService(Exception exception)
        {
            _exception = exception;
        }

        public NotchSettings Load() => new() { Width = 300, Height = 40, CornerRadius = 15 };

        public void Save(NotchSettings settings) => throw _exception;

        public Task SaveAsync(NotchSettings settings) => Task.FromException(_exception);

        public void ExportSettingsToFile(string filePath, NotchSettings settings) => throw _exception;

        public (NotchSettings Settings, bool RequiresRestart) ImportSettingsFromFile(string filePath, NotchSettings? currentSettings = null)
            => throw _exception;
    }
}
