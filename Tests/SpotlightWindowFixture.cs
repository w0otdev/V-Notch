using System.IO;
using VNotch.Models;
using VNotch.Services.Spotlight;
using VNotch.Services.Spotlight.Providers;
using VNotch.ViewModels;

namespace VNotch.Tests;

internal sealed class SpotlightWindowFixture : IDisposable
{
    private bool _disposed;
    internal string DirectoryPath { get; }
    internal string UsagePath { get; }
    internal SpotlightUsageStore Usage { get; }
    internal SpotlightWindow Window { get; }

    internal SpotlightWindowFixture(NotchSettings settings)
    {
        LiquidGlassSpotlightTests.CreateApplicationResources();
        DirectoryPath = Directory.CreateTempSubdirectory("vnotch-test-lg-").FullName;
        UsagePath = Path.Combine(DirectoryPath, "usage.json");
        Usage = new SpotlightUsageStore(UsagePath, () => DateTime.UtcNow);
        var viewModel = new SpotlightViewModel(new SpotlightSearchService([new EmptyProvider()]), Usage);
        try
        {
            var isolatedSettings = settings.Clone();
            isolatedSettings.SaveAiChatHistory = false;
            Window = new SpotlightWindow(viewModel, new SpotlightLauncher(), isolatedSettings)
            {
                Opacity = 0,
                SuppressForegroundActivationForTests = true
            };
            BackgroundTestWindows.ProtectInput(Window);
        }
        catch
        {
            viewModel.Dispose();
            DeleteDirectory();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Window.Shutdown(); }
        finally
        {
            // The usage worker has no dispatcher dependency. Drain it before
            // removing its files so a late save cannot recreate the directory.
            Usage.WaitForPendingSavesAsync().GetAwaiter().GetResult();
            DeleteDirectory();
        }
    }

    private void DeleteDirectory()
    {
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        string target = Path.GetFullPath(DirectoryPath);
        if (!target.StartsWith(Path.TrimEndingDirectorySeparator(temporaryRoot) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fixture directory is outside the temporary root.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }

    private sealed class EmptyProvider : ISpotlightProvider
    {
        public bool IsAvailable => true;
        public Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SpotlightSearchItem>>(Array.Empty<SpotlightSearchItem>());
    }
}
