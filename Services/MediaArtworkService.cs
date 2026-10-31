using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;

namespace VNotch.Services;

public interface IMediaArtworkService
{
    Task<BitmapImage?> DownloadImageAsync(string url, CancellationToken ct = default);
    BitmapSource? CropToSquare(BitmapSource source, string mediaSource, bool forceCenterCrop = false);
    Task<BitmapImage?> ConvertToWpfBitmapAsync(IRandomAccessStreamWithContentType stream, CancellationToken ct = default);
    void ConfigureSmartCrop(bool enabled);

    SubjectBounds? GetDominantSubjectBounds(BitmapSource source);
}

public sealed partial class MediaArtworkService : IMediaArtworkService, IDisposable
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_media_artwork_detect_content_bounds(
        ReadOnlySpan<byte> pixels,
        int pixelsLen,
        int stride,
        int sw,
        int sh,
        int width,
        int height,
        double scaleX,
        double scaleY,
        out int outX,
        out int outY,
        out int outW,
        out int outH);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_media_artwork_compute_square_crop_metrics(
        int width,
        int height,
        out double outAspect,
        out int outSquareSize,
        out int outCenterX,
        out int outCenterY);

    private const string ArtworkLogTag = "ARTWORK";
    private const string CropPathLogTag = "CROP-PATH";
    private const long MaxArtworkDownloadSizeBytes = 8 * 1024 * 1024; // 8 MiB
    private const int MaxDecodePixelDimension = 1024;

    private static readonly HttpClient _httpClient = new(NetworkPrivacy.Handler(NetworkFeature.Artwork));
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
    public void ConfigureSmartCrop(bool enabled)
    {
        EnableSmartCrop = enabled;
    }

    public SubjectBounds? GetDominantSubjectBounds(BitmapSource source)
    {
        if (!_smartCropAvailable)
        {
            _smartCropAvailable = _smartCrop.TryInitialize();
            if (!_smartCropAvailable)
                return null;
        }
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
                    await ms.WriteAsync(buffer.AsMemory(0, bytesRead), timeoutCts.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (ms.Length == 0) return null;

            return await DecodeImageAsync(ms.GetBuffer(), timeoutCts.Token, checked((int)ms.Length));
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
        public readonly bool ForceCenterCrop;
        public readonly bool SmartCropEnabled;

        public CropCacheKey(ArtworkFingerprint fingerprint, bool forceCenterCrop, bool smartCropEnabled)
        {
            Fingerprint = fingerprint;
            ForceCenterCrop = forceCenterCrop;
            SmartCropEnabled = smartCropEnabled;
        }

        public bool Equals(CropCacheKey other) =>
            Fingerprint.Equals(other.Fingerprint) &&
            ForceCenterCrop == other.ForceCenterCrop &&
            SmartCropEnabled == other.SmartCropEnabled;

        public override bool Equals(object? obj) => obj is CropCacheKey other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(Fingerprint, ForceCenterCrop, SmartCropEnabled);
    }

    private static readonly Dictionary<CropCacheKey, (BitmapSource Image, DateTime LastAccessedUtc)> _cropCache = new();
    private static readonly object _cropCacheLock = new();
    private const int MaxCropCacheSize = 24;

    public BitmapSource? CropToSquare(BitmapSource source, string mediaSource, bool forceCenterCrop = false)
    {
        try
        {
            // SoundCloud supplies finished cover art; preserve its framing and bypass crop analysis/cache.
            if (MediaPlatformExtensions.ParsePlatform(mediaSource) == MediaPlatform.SoundCloud)
                return source;

            int width = source.PixelWidth;
            int height = source.PixelHeight;

            bool smartCropEnabled = EnableSmartCrop;
            var cacheKey = new CropCacheKey(ArtworkFingerprint.Create(source), forceCenterCrop, smartCropEnabled);
            lock (_cropCacheLock)
            {
                if (_cropCache.TryGetValue(cacheKey, out var cached))
                {
                    _cropCache[cacheKey] = (cached.Image, DateTime.UtcNow);
                    return cached.Image;
                }
            }

            RuntimeLog.Log("CROP-START",
                $"src={width}x{height} aspect={(double)width / height:F2} mediaSource='{mediaSource}' forceCenterCrop={forceCenterCrop} smartEnabled={EnableSmartCrop} smartAvail={_smartCropAvailable}");

            double srcAspect = (double)width / height;
            var artworkFrame = ArtworkFrameDetector.DetectFrame(source);
            if (artworkFrame == null && Math.Abs(srcAspect - 1.0) < 0.02 && !forceCenterCrop)
            {
                RuntimeLog.Log(CropPathLogTag, $"already square ({width}x{height}) — skip crop");
                return CacheCropResult(cacheKey, source);
            }

            var contentRect = artworkFrame ?? DetectContentBounds(source, width, height);
            BitmapSource workingSource = source;

            if (contentRect.Width < width * 0.95 || contentRect.Height < height * 0.95)
            {
                var trimmed = new CroppedBitmap(source, contentRect);
                trimmed.Freeze();
                workingSource = trimmed;
                width = trimmed.PixelWidth;
                height = trimmed.PixelHeight;
            }

            vn_media_artwork_compute_square_crop_metrics(width, height, out double aspect, out int squareSize, out _, out _);

            Int32Rect rect = DetermineCropRect(workingSource, width, height, squareSize, aspect, forceCenterCrop, smartCropEnabled);

            if (ReferenceEquals(workingSource, source) && rect.X == 0 && rect.Y == 0 && rect.Width == width && rect.Height == height)
            {
                return CacheCropResult(cacheKey, source);
            }

            var cropped = new CroppedBitmap(workingSource, rect);
            cropped.Freeze();

            return CacheCropResult(cacheKey, cropped);
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(ArtworkLogTag, $"CropToSquare failed: {ex.Message}");
            return null;
        }
    }

    private static BitmapSource CacheCropResult(CropCacheKey cacheKey, BitmapSource image)
    {
        // A no-op crop can return a mutable caller-owned source. Never share it.
        if (!image.IsFrozen) return image;

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
            _cropCache[cacheKey] = (image, DateTime.UtcNow);
        }

        return image;
    }

    private Int32Rect DetermineCropRect(BitmapSource workingSource, int width, int height, int squareSize, double aspect, bool forceCenterCrop, bool smartCropEnabled)
    {
        if (smartCropEnabled && aspect > 1.4 && !forceCenterCrop)
        {
            if (!_smartCropAvailable)
            {
                _smartCropAvailable = _smartCrop.TryInitialize();
            }
            var smartRect = _smartCrop.GetSmartCropRect(workingSource, squareSize);
            if (smartRect.HasValue)
            {
                RuntimeLog.Log(CropPathLogTag, $"smart-crop OK rect=({smartRect.Value.X},{smartRect.Value.Y},{smartRect.Value.Width}x{smartRect.Value.Height})");
                return smartRect.Value;
            }
            RuntimeLog.Log(CropPathLogTag, "smart-crop returned null -> fallback");
        }
        else if (forceCenterCrop)
        {
            var centerRect = GetFallbackCropRect(width, height, squareSize);
            RuntimeLog.Log(CropPathLogTag, $"force-center rect=({centerRect.X},{centerRect.Y},{centerRect.Width}x{centerRect.Height})");
            return centerRect;
        }

        var fallbackRect = GetFallbackCropRect(width, height, squareSize);
        RuntimeLog.Log(CropPathLogTag, $"fallback rect=({fallbackRect.X},{fallbackRect.Y},{fallbackRect.Width}x{fallbackRect.Height})");
        return fallbackRect;
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
            if (stream == null || !stream.CanRead || stream.Size == 0)
            {
                return null;
            }

            if (stream.Size > MaxArtworkDownloadSizeBytes)
            {
                RuntimeLog.Log(ArtworkLogTag, $"ConvertToWpfBitmapAsync rejected stream: Size {stream.Size} exceeds limit of {MaxArtworkDownloadSizeBytes} bytes");
                return null;
            }

            try
            {
                if (stream.Position != 0)
                {
                    stream.Seek(0);
                }
            }
            catch
            {
                // Some WinRT stream implementations may not support Position or Seek(0).
            }

            int requestedBytes = (int)stream.Size;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(requestedBytes);
            try
            {
                int bytesToRead;
                using (var reader = new DataReader(stream))
                {
                    uint loaded = await reader.LoadAsync((uint)requestedBytes).AsTask(ct);
                    if (loaded == 0)
                    {
                        return null;
                    }

                    bytesToRead = (int)Math.Min(loaded, (uint)buffer.Length);
                    IBuffer dataBuffer = reader.ReadBuffer((uint)bytesToRead);
                    dataBuffer.CopyTo(0, buffer, 0, bytesToRead);
                }

                return await DecodeImageAsync(buffer, ct, bytesToRead);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(ArtworkLogTag, $"ConvertToWpfBitmapAsync failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<BitmapImage?> DecodeImageAsync(byte[] bytes, CancellationToken ct = default, int? length = null)
    {
        if (bytes == null || bytes.Length == 0) return null;

        return await Task.Run(() =>
        {
            try
            {
                using var probeStream = new MemoryStream(bytes, 0, length ?? bytes.Length, writable: false);
                var decoder = BitmapDecoder.Create(probeStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var frame = decoder.Frames.Count > 0 ? decoder.Frames[0] : null;
                int originalWidth = frame?.PixelWidth ?? 0;
                int originalHeight = frame?.PixelHeight ?? 0;

                // Decompression bomb protection
                if (originalWidth > 8192 || originalHeight > 8192)
                {
                    RuntimeLog.Warn(ArtworkLogTag, $"Rejected image with excessive dimensions: {originalWidth}x{originalHeight}");
                    return null;
                }

                using var stream = new MemoryStream(bytes, 0, length ?? bytes.Length, writable: false);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                if (originalWidth >= originalHeight && originalWidth > MaxDecodePixelDimension)
                {
                    image.DecodePixelWidth = MaxDecodePixelDimension;
                }
                else if (originalHeight > MaxDecodePixelDimension)
                {
                    image.DecodePixelHeight = MaxDecodePixelDimension;
                }
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn(ArtworkLogTag, $"DecodeImageAsync failed: {ex.Message}");
                return null;
            }
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
        int byteLen = sh * stride;
        byte[] pixels = System.Buffers.ArrayPool<byte>.Shared.Rent(byteLen);
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

            if (vn_media_artwork_detect_content_bounds(
                    pixels.AsSpan(0, byteLen), byteLen, stride, sw, sh, width, height, scaleX, scaleY,
                    out int contentX, out int contentY, out int contentW, out int contentH) == 0)
            {
                return new Int32Rect(contentX, contentY, contentW, contentH);
            }

            return new Int32Rect(0, 0, width, height);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _smartCrop.Dispose();
    }
}
