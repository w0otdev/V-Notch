using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services;

public interface IUpdateService
{
    Task<UpdateInfo?> CheckForUpdatesAsync(bool includePrereleases = false, bool forceRefresh = false);
    Task<IReadOnlyList<UpdateInfo>> GetAllReleasesAsync();
    async Task<ReleaseHistoryPage> GetChangelogPageAsync(int page, CancellationToken token) =>
        page == 1 ? new(await GetAllReleasesAsync().WaitAsync(token), false) : new([], false);
    Task<bool> DownloadAndInstallUpdateAsync(UpdateInfo updateInfo, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    string CurrentVersion { get; }
    event EventHandler<UpdateInfo?>? UpdateCheckCompleted;
    UpdateInfo? LatestUpdateInfo { get; }
}

public sealed record ReleaseHistoryPage(IReadOnlyList<UpdateInfo> Releases, bool HasMore);

public class UpdateInfo
{
    public string Version { get; set; } = string.Empty;
    public string? ReleaseTag { get; set; }
    public string DownloadUrl { get; set; } = string.Empty;
    public string ManifestUrl { get; set; } = string.Empty;
    public string ManifestSignatureUrl { get; set; } = string.Empty;
    public string InstallerName { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public DateTime PublishedAt { get; set; }
    public bool IsNewerVersion { get; set; }
    public bool IsPrerelease { get; set; }
}
