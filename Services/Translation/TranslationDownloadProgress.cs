namespace VNotch.Services.Translation;

internal enum TranslationDownloadStage { Waiting, Connecting, Downloading, Verifying, Completed }

internal sealed record TranslationDownloadProgress(TranslationDownloadStage Stage, long DownloadedBytes,
    long TotalBytes, double BytesPerSecond = 0)
{
    internal double Percent => TotalBytes > 0
        ? Math.Clamp((double)DownloadedBytes * 100.0 / TotalBytes, 0.0, 100.0)
        : 0.0;
}
