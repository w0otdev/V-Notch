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
}
