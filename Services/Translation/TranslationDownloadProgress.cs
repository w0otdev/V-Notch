namespace VNotch.Services.Translation;

internal enum TranslationDownloadStage { Waiting, Connecting, Downloading, Verifying, Completed }

internal sealed record TranslationDownloadProgress(TranslationDownloadStage Stage, long DownloadedBytes,
    long TotalBytes, double BytesPerSecond = 0)
{
    internal double Percent => TotalBytes > 0 ? Math.Clamp(DownloadedBytes * 100d / TotalBytes, 0, 100) : 0;
}
