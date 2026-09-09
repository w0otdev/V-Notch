using System;
using System.IO;
using System.Threading.Tasks;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SettingsServiceAsyncTests : IDisposable
{
    private readonly string _tempFile;
    private readonly SettingsService _service;

    public SettingsServiceAsyncTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"vnotch_settings_{Guid.NewGuid():N}.json");
        _service = new SettingsService(_tempFile);
    }

    public void Dispose()
    {
        _service.Dispose();

        try
        {
            if (File.Exists(_tempFile)) File.Delete(_tempFile);
            if (File.Exists(_tempFile + ".tmp")) File.Delete(_tempFile + ".tmp");
        }
        catch (IOException)
        {
            // Ignored: best-effort cleanup of temporary test files
        }
        catch (UnauthorizedAccessException)
        {
            // Ignored: best-effort cleanup of temporary test files
        }
    }

    [Fact]
    public async Task SaveAsync_ViaInterface_ExecutesAsynchronouslyAndPersists()
    {
        ISettingsService interfaceRef = _service;

        var settings = new NotchSettings
        {
            Width = 420,
            Height = 55,
            Language = "vi"
        };

        // Invoke SaveAsync through the interface reference
        Task saveTask = interfaceRef.SaveAsync(settings);

        // Await the task returned by the implementation
        await saveTask;

        // Verify the file was persisted correctly
        Assert.True(File.Exists(_tempFile));

        var loaded = _service.Load();
        Assert.Equal(420, loaded.Width);
        Assert.Equal(55, loaded.Height);
        Assert.Equal("vi", loaded.Language);
    }

    [Fact]
    public async Task DisposeAsync_DrainsEnqueuedSaves_AndCompletesWorker()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"vnotch_dispose_{Guid.NewGuid():N}.json");
        var service = new SettingsService(tempFile);

        try
        {
            var saveTask = service.SaveAsync(new NotchSettings { Width = 777 });
            await service.DisposeAsync();
            await saveTask;

            Assert.True(File.Exists(tempFile));
            var raw = File.ReadAllText(tempFile);
            Assert.Contains("777", raw);

            await Assert.ThrowsAsync<ObjectDisposedException>(() => service.SaveAsync(new NotchSettings { Width = 888 }));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Dispose_Synchronous_DrainsAndPreventsFurtherOperations()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"vnotch_sync_disp_{Guid.NewGuid():N}.json");
        var service = new SettingsService(tempFile);

        try
        {
            service.Save(new NotchSettings { Width = 666 });
            service.Dispose();

            Assert.True(File.Exists(tempFile));
            Assert.Contains("666", File.ReadAllText(tempFile));

            Assert.Throws<ObjectDisposedException>(() => service.Load());
            Assert.Throws<ObjectDisposedException>(() => service.Save(new NotchSettings { Width = 999 }));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task BoundedChannel_HandlesBurstAboveCapacity_AppliesBackpressureAndPersists()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"vnotch_burst_{Guid.NewGuid():N}.json");
        var service = new SettingsService(tempFile);

        try
        {
            // Enqueue 40 operations (above channel capacity of 32)
            var tasks = new Task[40];
            for (int i = 0; i < 40; i++)
            {
                tasks[i] = service.SaveAsync(new NotchSettings { Width = 300 + i });
            }

            await Task.WhenAll(tasks);
            await service.DisposeAsync();

            Assert.True(File.Exists(tempFile));
            var raw = File.ReadAllText(tempFile);
            Assert.NotNull(raw);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
