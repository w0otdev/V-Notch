using System.Runtime.InteropServices;

namespace VNotch.Services.Translation;

internal enum TranslationDownloadStage { Waiting, Connecting, Downloading, Verifying, Completed }

internal sealed partial record TranslationDownloadProgress(TranslationDownloadStage Stage, long DownloadedBytes,
    long TotalBytes, double BytesPerSecond = 0)
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_transdownload_compute_percent(
        long downloadedBytes, long totalBytes, out double outPercent);

    internal double Percent
    {
        get
        {
            vn_transdownload_compute_percent(DownloadedBytes, TotalBytes, out double pct);
            return pct;
        }
    }
}
