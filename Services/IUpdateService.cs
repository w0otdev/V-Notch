using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services;

public interface IUpdateService
{
    Task<UpdateInfo?> CheckForUpdatesAsync();
    Task<IReadOnlyList<UpdateInfo>> GetAllReleasesAsync();
    Task<bool> DownloadAndInstallUpdateAsync(UpdateInfo updateInfo, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    string CurrentVersion { get; }
}

public class UpdateInfo
{
    public string Version { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    /// <summary>HTTPS URL of the SHA-256 sidecar for the selected installer.</summary>
    public string ChecksumUrl { get; set; } = string.Empty;
    public string InstallerName { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public DateTime PublishedAt { get; set; }
    public bool IsNewerVersion { get; set; }
}
