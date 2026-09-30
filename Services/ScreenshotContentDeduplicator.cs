using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

/// <summary>Coalesces repeated clipboard/folder publications without retaining bitmaps.</summary>
internal sealed class ScreenshotContentDeduplicator
{
    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(30);
    private const int Capacity = 32;
    private readonly Dictionary<string, DateTime> _recent = new(StringComparer.Ordinal);

    // Called on the UI dispatcher after background hashing completes.
    public bool TryAccept(string fingerprint, DateTime now)
    {
        foreach (var expired in _recent.Where(pair => now - pair.Value >= Retention).Select(pair => pair.Key).ToArray())
            _recent.Remove(expired);

        bool duplicate = _recent.ContainsKey(fingerprint);
        _recent[fingerprint] = now;
        if (_recent.Count > Capacity)
            _recent.Remove(_recent.MinBy(pair => pair.Value).Key);
        return !duplicate;
    }

    public static string Fingerprint(BitmapSource image)
    {
        var normalized = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = checked(normalized.PixelWidth * 4);
        var row = new byte[stride];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int y = 0; y < normalized.PixelHeight; y++)
        {
            normalized.CopyPixels(new System.Windows.Int32Rect(0, y, normalized.PixelWidth, 1), row, stride, 0);
            hash.AppendData(row);
        }
        return $"{normalized.PixelWidth}:{normalized.PixelHeight}:" + Convert.ToHexString(hash.GetHashAndReset());
    }
}
