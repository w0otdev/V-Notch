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
    // 512 keeps a 16:9 thumbnail sharp after square-crop at up to 200% DPI
    // (102 DIU display size) and matches the 416px smart-crop model input.
    private const int ArtworkDecodeWidth = 512;

    private static readonly HttpClient _httpClient = new();
    private readonly SmartThumbnailCropService _smartCrop;
    private bool _smartCropAvailable;
    private bool _disposed;

    public MediaArtworkService()
    {
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
        if (enabled && !_smartCropAvailable)
        {
            InitializeSmartCrop();
        }
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
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(1800));
            var bytes = await _httpClient.GetByteArrayAsync(url, timeoutCts.Token);

            BitmapImage? bitmap = null;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                await dispatcher.InvokeAsync(() =>
                {
                    var ms = BitmapBufferPool.RentStream();
                    try
                    {
                        ms.Write(bytes, 0, bytes.Length);
                        ms.Position = 0;

                        bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.StreamSource = ms;
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.DecodePixelWidth = ArtworkDecodeWidth;
                        bitmap.EndInit();
                        bitmap.Freeze();
                    }
                    finally
                    {
                        BitmapBufferPool.ReturnStream(ms);
                    }
                });
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public BitmapImage? CropToSquare(BitmapImage source, string mediaSource, bool forceCenterCrop = false)
    {
        try
        {
            int width = source.PixelWidth;
            int height = source.PixelHeight;

            VNotch.Services.RuntimeLog.Log("CROP-START",
                $"src={width}x{height} aspect={(double)width / height:F2} mediaSource='{mediaSource}' forceCenterCrop={forceCenterCrop} smartEnabled={EnableSmartCrop} smartAvail={_smartCropAvailable}");

            double srcAspect = (double)width / height;
            if (Math.Abs(srcAspect - 1.0) < 0.02 && !forceCenterCrop)
            {
                VNotch.Services.RuntimeLog.Log("CROP-PATH", $"already square ({width}x{height}) — skip crop");
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

            Int32Rect rect;

            if (EnableSmartCrop && _smartCropAvailable && aspect > 1.4 && !forceCenterCrop)
            {
                BitmapImage? workingBitmap = workingSource as BitmapImage;
                if (workingBitmap == null)
                {
                    workingBitmap = ConvertToBitmapImage(workingSource);
                }

                if (workingBitmap != null)
                {
                    var smartRect = _smartCrop.GetSmartCropRect(workingBitmap, squareSize);
                    if (smartRect.HasValue)
                    {
                        rect = smartRect.Value;
                        VNotch.Services.RuntimeLog.Log("CROP-PATH", $"smart-crop OK rect=({rect.X},{rect.Y},{rect.Width}x{rect.Height})");
                    }
                    else
                    {
                        rect = GetFallbackCropRect(width, height, squareSize, mediaSource, aspect);
                        VNotch.Services.RuntimeLog.Log("CROP-PATH", $"smart-crop returned null -> fallback rect=({rect.X},{rect.Y},{rect.Width}x{rect.Height})");
                    }
                }
                else
                {
                    rect = GetFallbackCropRect(width, height, squareSize, mediaSource, aspect);
                    VNotch.Services.RuntimeLog.Log("CROP-PATH", $"workingBitmap null -> fallback rect=({rect.X},{rect.Y},{rect.Width}x{rect.Height})");
                }
            }
            else
            {
                if (forceCenterCrop)
                {
                    rect = new Int32Rect((width - squareSize) / 2, (height - squareSize) / 2, squareSize, squareSize);
                    VNotch.Services.RuntimeLog.Log("CROP-PATH", $"force-center rect=({rect.X},{rect.Y},{rect.Width}x{rect.Height})");
                }
                else
                {
                    rect = GetFallbackCropRect(width, height, squareSize, mediaSource, aspect);
                    VNotch.Services.RuntimeLog.Log("CROP-PATH", $"smart-disabled -> fallback rect=({rect.X},{rect.Y},{rect.Width}x{rect.Height})");
                }
            }

            var result = BitmapBufferPool.CreateCroppedBitmapImage(workingSource, rect);
            if (result != null)
                return result;

            var cropped = new CroppedBitmap(workingSource, rect);
            cropped.Freeze();

            int cropW = cropped.PixelWidth;
            int cropH = cropped.PixelHeight;
            int stride = cropW * 4;

            BitmapSource cropSource = cropped;
            if (cropped.Format != System.Windows.Media.PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap(cropped, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                cropSource = converted;
            }

            var wb = new WriteableBitmap(cropW, cropH, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            wb.Lock();
            try
            {
                cropSource.CopyPixels(new Int32Rect(0, 0, cropW, cropH), wb.BackBuffer, wb.BackBufferStride * cropH, wb.BackBufferStride);
                wb.AddDirtyRect(new Int32Rect(0, 0, cropW, cropH));
            }
            finally
            {
                wb.Unlock();
            }
            wb.Freeze();

            var ms = BitmapBufferPool.RentStream();
            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(wb));
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
            finally
            {
                BitmapBufferPool.ReturnStream(ms);
            }
        }
        catch
        {
            return null;
        }
    }

    private static Int32Rect GetFallbackCropRect(int width, int height, int squareSize, string mediaSource, double aspect)
    {
        int offsetX = (width - squareSize) / 2;
        int offsetY = (height - squareSize) / 2;
        return new Int32Rect(offsetX, offsetY, squareSize, squareSize);
    }

    public async Task<BitmapImage?> ConvertToWpfBitmapAsync(IRandomAccessStreamWithContentType stream, CancellationToken ct = default)
    {
        try
        {
            using var memoryStream = new MemoryStream();
            using (var reader = new DataReader(stream))
            {
                await reader.LoadAsync((uint)stream.Size).AsTask(ct);
                var bytes = new byte[stream.Size];
                reader.ReadBytes(bytes);
                memoryStream.Write(bytes, 0, bytes.Length);
            }

            memoryStream.Position = 0;

            BitmapImage? bitmap = null;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return null;
            }

            await dispatcher.InvokeAsync(() =>
            {
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = memoryStream;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = ArtworkDecodeWidth;
                bitmap.EndInit();
                bitmap.Freeze();
            });

            return bitmap;
        }
        catch
        {
            return null;
        }
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

            int topBar = 0;
            for (int y = 0; y < sh / 3; y++)
            {
                int darkPixels = 0, total = 0;
                for (int x = sw / 4; x < sw * 3 / 4; x++)
                {
                    int i = y * stride + x * 4;
                    if (pixels[i] < blackThreshold && pixels[i + 1] < blackThreshold && pixels[i + 2] < blackThreshold)
                        darkPixels++;
                    total++;
                }
                if (total > 0 && (double)darkPixels / total > 0.85)
                    topBar = y + 1;
                else
                    break;
            }

            int bottomBar = 0;
            for (int y = sh - 1; y >= sh * 2 / 3; y--)
            {
                int darkPixels = 0, total = 0;
                for (int x = sw / 4; x < sw * 3 / 4; x++)
                {
                    int i = y * stride + x * 4;
                    if (pixels[i] < blackThreshold && pixels[i + 1] < blackThreshold && pixels[i + 2] < blackThreshold)
                        darkPixels++;
                    total++;
                }
                if (total > 0 && (double)darkPixels / total > 0.85)
                    bottomBar++;
                else
                    break;
            }

            int leftBar = 0;
            for (int x = 0; x < sw / 3; x++)
            {
                int darkPixels = 0, total = 0;
                for (int y = sh / 4; y < sh * 3 / 4; y++)
                {
                    int i = y * stride + x * 4;
                    if (pixels[i] < blackThreshold && pixels[i + 1] < blackThreshold && pixels[i + 2] < blackThreshold)
                        darkPixels++;
                    total++;
                }
                if (total > 0 && (double)darkPixels / total > 0.85)
                    leftBar = x + 1;
                else
                    break;
            }

            int rightBar = 0;
            for (int x = sw - 1; x >= sw * 2 / 3; x--)
            {
                int darkPixels = 0, total = 0;
                for (int y = sh / 4; y < sh * 3 / 4; y++)
                {
                    int i = y * stride + x * 4;
                    if (pixels[i] < blackThreshold && pixels[i + 1] < blackThreshold && pixels[i + 2] < blackThreshold)
                        darkPixels++;
                    total++;
                }
                if (total > 0 && (double)darkPixels / total > 0.85)
                    rightBar++;
                else
                    break;
            }

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

    private static BitmapImage? ConvertToBitmapImage(BitmapSource source)
    {
        MemoryStream? ms = null;
        try
        {
            ms = BitmapBufferPool.RentStream();
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
        catch
        {
            return null;
        }
        finally
        {
            if (ms != null)
                BitmapBufferPool.ReturnStream(ms);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _smartCrop.Dispose();
    }
}
