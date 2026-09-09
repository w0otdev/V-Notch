using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;

namespace VNotch.Services;

public interface IMediaArtworkService
{
    Task<BitmapImage?> DownloadImageAsync(string url, CancellationToken ct = default);
    BitmapImage? CropToSquare(BitmapImage source, string mediaSource, bool forceCenterCrop = false);
    Task<BitmapImage?> ConvertToWpfBitmapAsync(IRandomAccessStreamWithContentType stream, CancellationToken ct = default);
    void ConfigureSmartCrop(bool enabled);

    SubjectBounds? GetDominantSubjectBounds(BitmapImage source);
}

public sealed class MediaArtworkService : IMediaArtworkService, IDisposable
{
    private const string ArtworkLogTag = "ARTWORK";
    private const string CropPathLogTag = "CROP-PATH";
    private const long MaxArtworkDownloadSizeBytes = 8 * 1024 * 1024; // 8 MiB
    private const int MaxDecodePixelWidth = 1024;

    private static readonly HttpClient _httpClient = new();
    private readonly HttpClient _client;
    private readonly SmartThumbnailCropService _smartCrop;
    private bool _smartCropAvailable;
    private bool _disposed;

    public MediaArtworkService() : this(_httpClient)
    {
    }

    internal MediaArtworkService(HttpClient client)
    {
        _client = client;
        _smartCrop = new SmartThumbnailCropService();
        _smartCropAvailable = false;
    }
    public bool EnableSmartCrop { get; set; } = false;
    public void InitializeSmartCrop()
    {
        if (!_smartCropAvailable)
        {
            _smartCropAvailable = _smartCrop.TryInitialize();
        }
    }
    public void ConfigureSmartCrop(bool enabled)
    {
        EnableSmartCrop = enabled;
    }

    public SubjectBounds? GetDominantSubjectBounds(BitmapImage source)
    {
        if (!_smartCropAvailable && !_smartCrop.TryInitialize())
            return null;
        return _smartCrop.GetDominantSubjectBounds(source);
    }

    static MediaArtworkService()
    {
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }
    }

    public async Task<BitmapImage?> DownloadImageAsync(string url, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return null;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(4000));

            using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            if (response.Content.Headers.ContentLength is { } contentLength && contentLength > MaxArtworkDownloadSizeBytes)
            {
                RuntimeLog.Log(ArtworkLogTag, $"DownloadImageAsync rejected {url}: Content-Length {contentLength} exceeds limit of {MaxArtworkDownloadSizeBytes} bytes");
                return null;
            }

            using var contentStream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var ms = new MemoryStream();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long totalRead = 0;
                int bytesRead;
                while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutCts.Token)) > 0)
                {
                    totalRead += bytesRead;
                    if (totalRead > MaxArtworkDownloadSizeBytes)
                    {
                        RuntimeLog.Log(ArtworkLogTag, $"DownloadImageAsync exceeded max allowed size ({MaxArtworkDownloadSizeBytes} bytes) for {url}");
                        return null;
                    }
                    ms.Write(buffer, 0, bytesRead);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (ms.Length == 0) return null;

            var bytes = ms.ToArray();
            return await DecodeImageAsync(bytes, timeoutCts.Token);
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(ArtworkLogTag, $"DownloadImageAsync failed for {url}: {ex.Message}");
            return null;
        }
    }

    private readonly struct CropCacheKey : IEquatable<CropCacheKey>
    {
        public readonly ArtworkFingerprint Fingerprint;
        public readonly Int32Rect Rect;
        public readonly bool ForceCenterCrop;
        public readonly bool SmartCropEnabled;

        public CropCacheKey(ArtworkFingerprint fingerprint, Int32Rect rect, bool forceCenterCrop, bool smartCropEnabled)
        {
            Fingerprint = fingerprint;
            Rect = rect;
            ForceCenterCrop = forceCenterCrop;
            SmartCropEnabled = smartCropEnabled;
        }

        public bool Equals(CropCacheKey other) =>
            Fingerprint.Equals(other.Fingerprint) &&
            Rect.Equals(other.Rect) &&
            ForceCenterCrop == other.ForceCenterCrop &&
            SmartCropEnabled == other.SmartCropEnabled;

        public override bool Equals(object? obj) => obj is CropCacheKey other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(Fingerprint, Rect, ForceCenterCrop, SmartCropEnabled);
    }

    private static readonly Dictionary<CropCacheKey, (BitmapImage Image, DateTime LastAccessedUtc)> _cropCache = new();
    private static readonly object _cropCacheLock = new();
    private const int MaxCropCacheSize = 24;

    public BitmapImage? CropToSquare(BitmapImage source, string mediaSource, bool forceCenterCrop = false)
    {
        try
        {
            int width = source.PixelWidth;
            int height = source.PixelHeight;

            RuntimeLog.Log("CROP-START",
                $"src={width}x{height} aspect={(double)width / height:F2} mediaSource='{mediaSource}' forceCenterCrop={forceCenterCrop} smartEnabled={EnableSmartCrop} smartAvail={_smartCropAvailable}");

            double srcAspect = (double)width / height;
            if (Math.Abs(srcAspect - 1.0) < 0.02 && !forceCenterCrop)
            {
                RuntimeLog.Log(CropPathLogTag, $"already square ({width}x{height}) — skip crop");
                return source;
            }

            var contentRect = DetectContentBounds(source, width, height);
            BitmapSource workingSource = source;

            if (contentRect.Width < width * 0.95 || contentRect.Height < height * 0.95)
            {
                var trimmed = new CroppedBitmap(source, contentRect);
                trimmed.Freeze();
                workingSource = trimmed;
                width = trimmed.PixelWidth;
                height = trimmed.PixelHeight;
            }

            double aspect = (double)width / height;
            double zoom = aspect >= 0.9 && aspect <= 1.1 ? 1.0 : 0.97;
            int squareSize = (int)(Math.Min(width, height) * zoom);

            Int32Rect rect = DetermineCropRect(workingSource, width, height, squareSize, aspect, forceCenterCrop);

            if (ReferenceEquals(workingSource, source) && rect.X == 0 && rect.Y == 0 && rect.Width == width && rect.Height == height)
            {
                return source;
            }

            var fingerprint = ArtworkFingerprint.Create(source);
            var cacheKey = new CropCacheKey(fingerprint, rect, forceCenterCrop, EnableSmartCrop);

            lock (_cropCacheLock)
            {
                if (_cropCache.TryGetValue(cacheKey, out var cached))
                {
                    _cropCache[cacheKey] = (cached.Image, DateTime.UtcNow);
                    return cached.Image;
                }
            }

            var encoded = EncodeCroppedBitmap(workingSource, rect);

            lock (_cropCacheLock)
            {
                if (_cropCache.Count >= MaxCropCacheSize && !_cropCache.ContainsKey(cacheKey))
                {
                    CropCacheKey oldestKey = default;
                    DateTime oldestTime = DateTime.MaxValue;
                    foreach (var kvp in _cropCache)
                    {
                        if (kvp.Value.LastAccessedUtc < oldestTime)
                        {
                            oldestTime = kvp.Value.LastAccessedUtc;
                            oldestKey = kvp.Key;
                        }
                    }
                    if (oldestTime != DateTime.MaxValue)
                    {
                        _cropCache.Remove(oldestKey);
                    }
                }
                _cropCache[cacheKey] = (encoded, DateTime.UtcNow);
            }

            return encoded;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(ArtworkLogTag, $"CropToSquare failed: {ex.Message}");
            return null;
        }
    }

    private Int32Rect DetermineCropRect(BitmapSource workingSource, int width, int height, int squareSize, double aspect, bool forceCenterCrop)
    {
        if (EnableSmartCrop && _smartCropAvailable && aspect > 1.4 && !forceCenterCrop)
        {
            BitmapImage? workingBitmap = workingSource as BitmapImage ?? ConvertToBitmapImage(workingSource);
            if (workingBitmap != null)
            {
                var smartRect = _smartCrop.GetSmartCropRect(workingBitmap, squareSize);
                if (smartRect.HasValue)
                {
                    RuntimeLog.Log(CropPathLogTag, $"smart-crop OK rect=({smartRect.Value.X},{smartRect.Value.Y},{smartRect.Value.Width}x{smartRect.Value.Height})");
                    return smartRect.Value;
                }
                RuntimeLog.Log(CropPathLogTag, "smart-crop returned null -> fallback");
            }
            else
            {
                RuntimeLog.Log(CropPathLogTag, "workingBitmap null -> fallback");
            }
        }
        else if (forceCenterCrop)
        {
            var centerRect = new Int32Rect((width - squareSize) / 2, (height - squareSize) / 2, squareSize, squareSize);
            RuntimeLog.Log(CropPathLogTag, $"force-center rect=({centerRect.X},{centerRect.Y},{centerRect.Width}x{centerRect.Height})");
            return centerRect;
        }

        var fallbackRect = GetFallbackCropRect(width, height, squareSize);
        RuntimeLog.Log(CropPathLogTag, $"fallback rect=({fallbackRect.X},{fallbackRect.Y},{fallbackRect.Width}x{fallbackRect.Height})");
        return fallbackRect;
    }

    private static BitmapImage EncodeCroppedBitmap(BitmapSource workingSource, Int32Rect rect)
    {
        var cropped = new CroppedBitmap(workingSource, rect);
        cropped.Freeze();

        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(cropped));
        encoder.Save(ms);
        ms.Position = 0;

        var bitmapImage = new BitmapImage();
        bitmapImage.BeginInit();
        bitmapImage.StreamSource = ms;
        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
        bitmapImage.EndInit();
        bitmapImage.Freeze();
        return bitmapImage;
    }

    private static Int32Rect GetFallbackCropRect(int width, int height, int squareSize)
    {
        int offsetX = (width - squareSize) / 2;
        int offsetY = (height - squareSize) / 2;
        return new Int32Rect(offsetX, offsetY, squareSize, squareSize);
    }

    public async Task<BitmapImage?> ConvertToWpfBitmapAsync(IRandomAccessStreamWithContentType stream, CancellationToken ct = default)
    {
        try
        {
            if (stream == null || stream.Size == 0)
            {
                return null;
            }

            if (stream.Size > MaxArtworkDownloadSizeBytes)
            {
                RuntimeLog.Log(ArtworkLogTag, $"ConvertToWpfBitmapAsync rejected stream: Size {stream.Size} exceeds limit of {MaxArtworkDownloadSizeBytes} bytes");
                return null;
            }

            byte[] bytes;
            using (var reader = new DataReader(stream))
            {
                await reader.LoadAsync((uint)stream.Size).AsTask(ct);
                bytes = new byte[stream.Size];
                reader.ReadBytes(bytes);
            }

            return await DecodeImageAsync(bytes, ct);
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(ArtworkLogTag, $"ConvertToWpfBitmapAsync failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<BitmapImage?> DecodeImageAsync(byte[] bytes, CancellationToken ct = default)
    {
        if (bytes == null || bytes.Length == 0) return null;

        return await Task.Run(() =>
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = MaxDecodePixelWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }, ct);
    }

    private static Int32Rect DetectContentBounds(BitmapSource source, int width, int height)
    {
        const int sampleSize = 64;
        double scaleX = (double)sampleSize / width;
        double scaleY = (double)sampleSize / height;
        var small = new TransformedBitmap(source, new ScaleTransform(scaleX, scaleY));
        small.Freeze();

        int sw = small.PixelWidth;
        int sh = small.PixelHeight;
        if (sw < 4 || sh < 4) return new Int32Rect(0, 0, width, height);

        int stride = sw * 4;
        byte[] pixels = System.Buffers.ArrayPool<byte>.Shared.Rent(sh * stride);
        try
        {
            BitmapSource src = small;
            if (small.Format != System.Windows.Media.PixelFormats.Bgra32)
            {
                var conv = new FormatConvertedBitmap(small, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                conv.Freeze();
                src = conv;
            }
            src.CopyPixels(pixels, stride, 0);

            const int blackThreshold = 25;

            int topBar = DetectTopDarkBar(pixels, stride, sw, sh, blackThreshold);
            int bottomBar = DetectBottomDarkBar(pixels, stride, sw, sh, blackThreshold);
            int leftBar = DetectLeftDarkBar(pixels, stride, sw, sh, blackThreshold);
            int rightBar = DetectRightDarkBar(pixels, stride, sw, sh, blackThreshold);

            int contentX = (int)(leftBar / scaleX);
            int contentY = (int)(topBar / scaleY);
            int contentW = width - contentX - (int)(rightBar / scaleX);
            int contentH = height - contentY - (int)(bottomBar / scaleY);

            if (contentW < width / 2 || contentH < height / 2)
                return new Int32Rect(0, 0, width, height);

            return new Int32Rect(contentX, contentY, contentW, contentH);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private static int DetectTopDarkBar(byte[] pixels, int stride, int sw, int sh, int blackThreshold)
    {
        int topBar = 0;
        for (int y = 0; y < sh / 3; y++)
        {
            if (IsRowDark(pixels, y * stride, sw / 4, sw * 3 / 4, blackThreshold))
                topBar = y + 1;
            else
                break;
        }
        return topBar;
    }

    private static int DetectBottomDarkBar(byte[] pixels, int stride, int sw, int sh, int blackThreshold)
    {
        int bottomBar = 0;
        for (int y = sh - 1; y >= sh * 2 / 3; y--)
        {
            if (IsRowDark(pixels, y * stride, sw / 4, sw * 3 / 4, blackThreshold))
                bottomBar++;
            else
                break;
        }
        return bottomBar;
    }

    private static int DetectLeftDarkBar(byte[] pixels, int stride, int sw, int sh, int blackThreshold)
    {
        int leftBar = 0;
        for (int x = 0; x < sw / 3; x++)
        {
            if (IsColumnDark(pixels, x, stride, sh / 4, sh * 3 / 4, blackThreshold))
                leftBar = x + 1;
            else
                break;
        }
        return leftBar;
    }

    private static int DetectRightDarkBar(byte[] pixels, int stride, int sw, int sh, int blackThreshold)
    {
        int rightBar = 0;
        for (int x = sw - 1; x >= sw * 2 / 3; x--)
        {
            if (IsColumnDark(pixels, x, stride, sh / 4, sh * 3 / 4, blackThreshold))
                rightBar++;
            else
                break;
        }
        return rightBar;
    }

    private static bool IsRowDark(byte[] pixels, int rowOffset, int startX, int endX, int blackThreshold)
    {
        int darkPixels = 0;
        int total = endX - startX;
        if (total <= 0) return false;

        for (int x = startX; x < endX; x++)
        {
            int i = rowOffset + x * 4;
            if (pixels[i] < blackThreshold && pixels[i + 1] < blackThreshold && pixels[i + 2] < blackThreshold)
                darkPixels++;
        }
        return (double)darkPixels / total > 0.85;
    }

    private static bool IsColumnDark(byte[] pixels, int colX, int stride, int startY, int endY, int blackThreshold)
    {
        int darkPixels = 0;
        int total = endY - startY;
        if (total <= 0) return false;

        for (int y = startY; y < endY; y++)
        {
            int i = y * stride + colX * 4;
            if (pixels[i] < blackThreshold && pixels[i + 1] < blackThreshold && pixels[i + 2] < blackThreshold)
                darkPixels++;
        }
        return (double)darkPixels / total > 0.85;
    }

    private static BitmapImage? ConvertToBitmapImage(BitmapSource source)
    {
        try
        {
            using var ms = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            encoder.Save(ms);
            ms.Position = 0;

            var bitmapImage = new BitmapImage();
            bitmapImage.BeginInit();
            bitmapImage.StreamSource = ms;
            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
            bitmapImage.EndInit();
            bitmapImage.Freeze();
            return bitmapImage;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(ArtworkLogTag, $"ConvertToBitmapImage failed: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _smartCrop.Dispose();
    }
}
