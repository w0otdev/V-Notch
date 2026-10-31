using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VNotch.Services;

public sealed partial class SmartThumbnailCropService : IDisposable
{
    private const string LogCategory = "SMART-CROP";
    private readonly object _lock = new();

    private const int ModelInputSize = 416;

    private readonly Dictionary<(ArtworkFingerprint Artwork, int Size, bool ObjectDetection), Int32Rect> _cropCache = new();
    private readonly Queue<(ArtworkFingerprint Artwork, int Size, bool ObjectDetection)> _cropCacheOrder = new();

    private bool _disposed;
    private bool _modelExists;
    private bool _modelExistsChecked;
    private InferenceSession? _cachedSession;

    private const int MaxInferenceCacheEntries = 24;
    private readonly Dictionary<ArtworkFingerprint, InferenceCacheEntry> _inferenceCache = new();
    private readonly ConditionalWeakTable<BitmapSource, FingerprintHolder> _fingerprintCache = new();
    private long _inferenceCacheAccess;

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    [LibraryImport("ucrtbase.dll")]
    private static partial int _heapmin();

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool K32EmptyWorkingSet(IntPtr hProcess);

    private static long _lastWorkingSetTrimTicks;

    internal static void TryMinimizeNativeHeap(bool trimWorkingSet = false)
    {
        try
        {
            _ = _heapmin();
        }
        catch
        {
            // Best-effort CRT heap compaction.
        }

        if (!trimWorkingSet)
            return;

        long now = Environment.TickCount64;
        long last = System.Threading.Interlocked.Read(ref _lastWorkingSetTrimTicks);
        if (now - last < 30_000 || System.Threading.Interlocked.CompareExchange(ref _lastWorkingSetTrimTicks, now, last) != last)
            return;

        try
        {
#pragma warning disable S1215 // Intentional non-blocking gen2 trim after heavy ONNX inference idle timeout
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
#pragma warning restore S1215
            _ = _heapmin();
            _ = K32EmptyWorkingSet(GetCurrentProcess());
        }
        catch
        {
            // Best-effort working-set trim.
        }
    }
    private DateTime _lastUsedUtc;
    private System.Threading.Timer? _idleTimer;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnSubjectBounds
    {
        public float CenterX;
        public float CenterY;
        public float Width;
        public float Height;
        public float Confidence;
        public int ClassId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnCropRect
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;

        public readonly Int32Rect ToRect() => new(X, Y, Width, Height);
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_smartcrop_fill_tensor_from_bgra32(
        ReadOnlySpan<byte> pixels, int scaledW, int scaledH, int stride,
        Span<float> tensorBuffer);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_smartcrop_apply_nms(
        ReadOnlySpan<Detection> inDetections, int inCount,
        Span<Detection> outDetections, int outCap, out int outCount);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_smartcrop_parse_yolox_and_nms(
        ReadOnlySpan<float> buffer, int imgWidth, int imgHeight, float scale,
        Span<Detection> outDetections, int outCap,
        out int outRawCount, out int outNmsCount);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_smartcrop_find_best_subject_bounds(
        ReadOnlySpan<Detection> detections, int count,
        int imgWidth, int imgHeight,
        out VnSubjectBounds outBounds, out byte outFound);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_smartcrop_get_portrait_crop_rect(
        in Detection face, int width, int height,
        out VnCropRect outRect);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_smartcrop_get_single_person_crop_rect(
        in Detection person, int imgWidth, int imgHeight, int targetSize,
        out VnCropRect outRect);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_smartcrop_get_group_person_crop_rect(
        ReadOnlySpan<Detection> persons, int count,
        int imgWidth, int imgHeight, int targetSize,
        out VnCropRect outRect);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_smartcrop_get_best_object_crop_rect(
        ReadOnlySpan<Detection> objects, int count,
        int imgWidth, int imgHeight, int targetSize,
        out VnCropRect outRect);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_smartcrop_compute_saliency_crop_rect(
        ReadOnlySpan<byte> pixels, int w, int h, int stride,
        int imgWidth, int imgHeight, int targetSize,
        out VnCropRect outRect);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_smartcrop_try_detect_text_crop_rect(
        ReadOnlySpan<byte> pixels, int w, int h, int stride,
        int imgWidth, int imgHeight, int cropSize,
        out VnCropRect outRect, out byte outFound);

    public bool TryInitialize()
    {
        if (_disposed) return false;
        if (_modelExistsChecked) return _modelExists;

        _modelExists = File.Exists(GetModelPath());
        _modelExistsChecked = true;

        if (!_modelExists)
            System.Diagnostics.Debug.WriteLine($"[SmartCrop] Model not found at: {GetModelPath()}");

        return _modelExists;
    }

    public bool IsLoaded => _modelExists;

    public void Unload()
    {
        lock (_lock)
        {
            _cachedSession?.Dispose();
            _cachedSession = null;
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
    }

    private void MarkSessionUsed()
    {
        _lastUsedUtc = DateTime.UtcNow;
        _idleTimer ??= new System.Threading.Timer(OnIdleCheck, null, IdleTimeout, IdleTimeout);
    }

    private void OnIdleCheck(object? state)
    {
        lock (_lock)
        {
            if (_cachedSession == null)
            {
                _idleTimer?.Dispose();
                _idleTimer = null;
                return;
            }

            if (DateTime.UtcNow - _lastUsedUtc >= IdleTimeout)
            {
                _cachedSession.Dispose();
                _cachedSession = null;
                _idleTimer?.Dispose();
                _idleTimer = null;
                VNotch.Services.RuntimeLog.Log(LogCategory, "Session unloaded after idle timeout");
                TryMinimizeNativeHeap(trimWorkingSet: true);
            }
        }
    }

    public SubjectBounds? GetDominantSubjectBounds(BitmapSource source)
    {
        if (_disposed) return null;
        if (!_modelExists && !TryInitialize()) return null;
        if (!_modelExists) return null;
        if (source == null) return null;

        int imgWidth = source.PixelWidth;
        int imgHeight = source.PixelHeight;
        if (imgWidth < 64 || imgHeight < 64) return null;

        lock (_lock)
        {
            try
            {
                var detections = GetOrRunInferenceLocked(source, imgWidth, imgHeight);
                if (detections.Length == 0) return null;

                if (vn_smartcrop_find_best_subject_bounds(detections, detections.Length, imgWidth, imgHeight, out var bounds, out byte found) == 0 && found != 0)
                {
                    return new SubjectBounds(bounds.CenterX, bounds.CenterY, bounds.Width, bounds.Height, bounds.Confidence, bounds.ClassId);
                }
                return null;
            }
            catch (Exception ex)
            {
                VNotch.Services.RuntimeLog.Error("SUBJECT-BOUNDS", $"Inference failed: {ex.GetType().Name}: {ex.Message}");
                _cachedSession?.Dispose();
                _cachedSession = null;
                return null;
            }
        }
    }

    public Int32Rect? GetSmartCropRect(BitmapSource source, int targetSquareSize) =>
        GetCpuCropRect(source, targetSquareSize, useObjectDetection: false);

    internal static Int32Rect? Analyze(BitmapSource source, int targetSquareSize = 120)
    {
        if (source == null || targetSquareSize <= 0) return null;
        int imgWidth = source.PixelWidth;
        int imgHeight = source.PixelHeight;
        if (imgWidth < 64 || imgHeight < 64) return null;

        if (!ArtworkFrameDetector.RentDownscaledPixels(source, out byte[]? pixels, out int w, out int h, out int stride, 120.0) || pixels == null)
            return null;

        try
        {
            var frame = ArtworkFrameDetector.DetectFrameFromPixels(pixels, w, h, imgWidth, imgHeight);
            if (frame.HasValue && frame.Value.Width > 0 && frame.Value.Height > 0)
                return frame.Value;

            if (w >= 4 && h >= 4 && vn_smartcrop_compute_saliency_crop_rect(pixels.AsSpan(0, h * stride), w, h, stride, imgWidth, imgHeight, targetSquareSize, out var rect) == 0)
                return rect.ToRect();

            int cropSize = Math.Min(targetSquareSize, Math.Min(imgWidth, imgHeight));
            return new Int32Rect((imgWidth - cropSize) / 2, (imgHeight - cropSize) / 2, cropSize, cropSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private Int32Rect? GetCpuCropRect(BitmapSource source, int targetSquareSize, bool useObjectDetection)
    {
        if (source == null || targetSquareSize <= 0) return null;
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        if (width < 64 || height < 64 || Math.Abs(width - height) < 10) return null;
        useObjectDetection &= targetSquareSize > 96;
        lock (_lock)
        {
            if (_disposed) return null;
            var fingerprint = _fingerprintCache
                .GetValue(source, static bitmap => new FingerprintHolder(ArtworkFingerprint.Create(bitmap))).Value;
            var key = (fingerprint, targetSquareSize, useObjectDetection);
            if (_cropCache.TryGetValue(key, out var cached)) return cached;

            var rect = useObjectDetection
                ? ComputeSmartCropRectCore(source, targetSquareSize)
                : GetSaliencyCropRect(source, width, height, targetSquareSize);
            if (rect.HasValue)
            {
                if (_cropCache.Count >= MaxInferenceCacheEntries)
                    _cropCache.Remove(_cropCacheOrder.Dequeue());
                _cropCache[key] = rect.Value;
                _cropCacheOrder.Enqueue(key);
            }
            return rect;
        }
    }

    /// <summary>Runs CPU inference off the caller's dispatcher and awaits optional WinRT face framing.</summary>
    public async Task<Int32Rect?> GetSmartCropRectAsync(BitmapSource source, int targetSquareSize,
        bool useObjectDetection = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source == null || targetSquareSize <= 0) return null;
        BitmapSource bitmap = source;
        if (!bitmap.IsFrozen)
        {
            bitmap = source.CloneCurrentValue();
            bitmap.Freeze();
        }
        return await Task.Run(async () =>
        {
            var fallback = GetCpuCropRect(bitmap, targetSquareSize, useObjectDetection);
            cancellationToken.ThrowIfCancellationRequested();
            int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
            if (!fallback.HasValue || !useObjectDetection || targetSquareSize <= 96 ||
                (width < 400 && height < 400)) return fallback;
            Detection[] persons;
            lock (_lock)
            {
                if (_disposed) return null;
                var fingerprint = _fingerprintCache.GetValue(bitmap,
                    static value => new FingerprintHolder(ArtworkFingerprint.Create(value))).Value;
                if (!_inferenceCache.TryGetValue(fingerprint, out var cached)) return fallback;
                persons = cached.Detections.Where(static d => d.ClassId == 0).ToArray();
            }
            if (persons.Length == 0) return fallback;
            float bestConfidence = persons.Max(static p => p.Confidence);
            persons = persons.Where(p => p.Confidence >= bestConfidence * 0.5f).ToArray();
            if (persons.Length != 1) return fallback;
            return await TryGetPortraitCropAsync(bitmap, persons[0], width, height, cancellationToken)
                .ConfigureAwait(false) ?? fallback;
        }, cancellationToken).ConfigureAwait(false);
    }

    private Int32Rect? ComputeSmartCropRectCore(BitmapSource source, int targetSquareSize)
    {
        if (_disposed) return null;
        if (!_modelExists && !TryInitialize())
        {
            VNotch.Services.RuntimeLog.Log(LogCategory, $"GetSmartCropRect: model init failed");
            return null;
        }
        if (!_modelExists)
        {
            VNotch.Services.RuntimeLog.Log(LogCategory, $"GetSmartCropRect: model not exist");
            return null;
        }

        lock (_lock)
        {
            try
            {
                int imgWidth = source.PixelWidth;
                int imgHeight = source.PixelHeight;

                if (Math.Abs(imgWidth - imgHeight) < 10 || imgWidth < 64 || imgHeight < 64)
                {
                    VNotch.Services.RuntimeLog.Log(LogCategory, $"GetSmartCropRect: skip (square/small) {imgWidth}x{imgHeight}");
                    return null;
                }

                if (imgWidth < 400 && imgHeight < 400)
                {
                    int maxCrop = Math.Min(imgWidth, imgHeight);
                    int cropSz = Math.Min(targetSquareSize, maxCrop);
                    VNotch.Services.RuntimeLog.Log(LogCategory, $"GetSmartCropRect: small image {imgWidth}x{imgHeight} -> saliency only");
                    return GetSaliencyCropRect(source, imgWidth, imgHeight, cropSz);
                }

                var detections = GetOrRunInferenceLocked(source, imgWidth, imgHeight);

                if (detections.Length == 0)
                {
                    VNotch.Services.RuntimeLog.Log(LogCategory, "ONNX produced 0 detections -> saliency fallback");
                    return GetSaliencyCropRect(source, imgWidth, imgHeight, targetSquareSize);
                }

                VNotch.Services.RuntimeLog.Log(LogCategory, $"raw detections count={detections.Length}");
                return GetHybridCropRect(detections, source, imgWidth, imgHeight, targetSquareSize);
            }
            catch (Exception ex)
            {
                VNotch.Services.RuntimeLog.Error(LogCategory, $"Inference failed: {ex.GetType().Name}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[SmartCrop] Inference failed: {ex.Message}");
                _cachedSession?.Dispose();
                _cachedSession = null;
                return null;
            }
        }
    }

    private Detection[] GetOrRunInferenceLocked(BitmapSource source, int imgWidth, int imgHeight)
    {
        ArtworkFingerprint fingerprint = _fingerprintCache
            .GetValue(source, static bitmap => new FingerprintHolder(ArtworkFingerprint.Create(bitmap)))
            .Value;

        if (_inferenceCache.TryGetValue(fingerprint, out var cached))
        {
            cached.LastAccess = ++_inferenceCacheAccess;
            VNotch.Services.RuntimeLog.Debug(LogCategory, () =>
                $"ONNX cache hit artwork={fingerprint.ContentHash:X16} detections={cached.Detections.Length}");
            return cached.Detections;
        }

        if (source is CroppedBitmap crop && TryGetCachedCropDetections(crop, out var croppedDetections))
        {
            AddInferenceCacheEntryLocked(fingerprint, croppedDetections);
            return croppedDetections;
        }

        EnsureSessionLoadedLocked();
        MarkSessionUsed();

        float[]? tensorBuffer = null;
        try
        {
            int requiredLength = 3 * ModelInputSize * ModelInputSize;
            tensorBuffer = ArrayPool<float>.Shared.Rent(requiredLength);

            float scale = PreprocessImageFast(source, tensorBuffer);
            var tensor = new DenseTensor<float>(
                new Memory<float>(tensorBuffer, 0, requiredLength),
                new[] { 1, 3, ModelInputSize, ModelInputSize });
            var inputs = new List<NamedOnnxValue>(1)
            {
                NamedOnnxValue.CreateFromTensor(_cachedSession!.InputNames[0], tensor)
            };

            using var results = _cachedSession.Run(inputs);
            var output = results[0].AsTensor<float>();
            Detection[] detections = ParseYoloxOutput(output, imgWidth, imgHeight, scale).ToArray();

            AddInferenceCacheEntryLocked(fingerprint, detections);
            return detections;
        }
        finally
        {
            if (tensorBuffer != null)
                ArrayPool<float>.Shared.Return(tensorBuffer);
            TryMinimizeNativeHeap();
        }
    }

    private void EnsureSessionLoadedLocked()
    {
        if (_cachedSession != null)
            return;

        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = 2,
            EnableMemoryPattern = false,
            EnableCpuMemArena = false
        };

        var session = new InferenceSession(GetModelPath(), options);
        if (session.InputMetadata.Count != 1 || session.OutputMetadata.Count != 1 ||
            !session.InputMetadata.Single().Value.Dimensions.SequenceEqual(new[] { 1, 3, ModelInputSize, ModelInputSize }) ||
            !session.OutputMetadata.Single().Value.Dimensions.SequenceEqual(new[] { 1, 3549, 85 }))
        {
            session.Dispose();
            throw new InvalidDataException("Smart crop requires the bundled YOLOX-Nano raw-output model.");
        }
        _cachedSession = session;
        System.Diagnostics.Debug.WriteLine("[SmartCrop] Model loaded (cached session).");
    }

    private bool TryGetCachedCropDetections(CroppedBitmap crop, out Detection[] detections)
    {
        var original = crop.Source;
        var key = _fingerprintCache.GetValue(original,
            static bitmap => new FingerprintHolder(ArtworkFingerprint.Create(bitmap))).Value;
        Detection[] sourceDetections;
        if (_inferenceCache.TryGetValue(key, out var cached))
            sourceDetections = cached.Detections;
        else if (original is CroppedBitmap parent && TryGetCachedCropDetections(parent, out var inherited))
            sourceDetections = inherited;
        else
        {
            detections = Array.Empty<Detection>();
            return false;
        }

        var rect = crop.SourceRect;
        if (rect.IsEmpty) rect = new Int32Rect(0, 0, original.PixelWidth, original.PixelHeight);
        var mapped = new List<Detection>(sourceDetections.Length);
        foreach (var detection in sourceDetections)
        {
            var translated = detection;
            translated.X1 = Math.Clamp(detection.X1 - rect.X, 0, rect.Width);
            translated.X2 = Math.Clamp(detection.X2 - rect.X, 0, rect.Width);
            translated.Y1 = Math.Clamp(detection.Y1 - rect.Y, 0, rect.Height);
            translated.Y2 = Math.Clamp(detection.Y2 - rect.Y, 0, rect.Height);
            if (translated.X2 > translated.X1 && translated.Y2 > translated.Y1) mapped.Add(translated);
        }
        detections = mapped.ToArray();
        return true;
    }

    private void AddInferenceCacheEntryLocked(ArtworkFingerprint fingerprint, Detection[] detections)
    {
        if (_inferenceCache.Count >= MaxInferenceCacheEntries && !_inferenceCache.ContainsKey(fingerprint))
        {
            ArtworkFingerprint oldestKey = _inferenceCache
                .MinBy(static pair => pair.Value.LastAccess)
                .Key;
            _inferenceCache.Remove(oldestKey);
        }

        _inferenceCache[fingerprint] = new InferenceCacheEntry(detections, ++_inferenceCacheAccess);
    }

    internal static float PreprocessImageFast(BitmapSource source, float[] tensorBuffer)
    {
        int imgWidth = source.PixelWidth;
        int imgHeight = source.PixelHeight;

        float scale = Math.Min((float)ModelInputSize / imgWidth, (float)ModelInputSize / imgHeight);
        int newWidth = Math.Max(1, (int)(imgWidth * scale));
        int newHeight = Math.Max(1, (int)(imgHeight * scale));

        var scaled = new TransformedBitmap(source, new ScaleTransform(
            (double)newWidth / imgWidth,
            (double)newHeight / imgHeight));
        scaled.Freeze();

        int scaledW = scaled.PixelWidth;
        int scaledH = scaled.PixelHeight;
        int stride = scaledW * 4;

        byte[] pixels = ArrayPool<byte>.Shared.Rent(scaledH * stride);
        try
        {
            if (scaled.Format != PixelFormats.Bgra32)
            {
                var formatted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
                formatted.Freeze();
                formatted.CopyPixels(pixels, stride, 0);
            }
            else
            {
                scaled.CopyPixels(pixels, stride, 0);
            }

            vn_smartcrop_fill_tensor_from_bgra32(pixels.AsSpan(0, scaledH * stride), scaledW, scaledH, stride, tensorBuffer);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }

        return scale;
    }

    internal static List<Detection> ParseYoloxOutput(Tensor<float> output, int imgWidth, int imgHeight, float scale)
    {
        var dims = output.Dimensions;
        if (dims.Length != 3 || dims[0] != 1 || dims[1] != 3549 || dims[2] != 85)
            throw new InvalidDataException("Unexpected YOLOX-Nano output shape.");
        if (imgWidth <= 0 || imgHeight <= 0 || !float.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale));

        const int totalElements = 3549 * 85;
        float[]? rented = null;
        try
        {
            ReadOnlySpan<float> buffer;
            if (output is DenseTensor<float> dense && dense.Buffer.Span.Length == totalElements)
            {
                buffer = dense.Buffer.Span;
            }
            else
            {
                rented = ArrayPool<float>.Shared.Rent(totalElements);
                int idx = 0;
                for (int i = 0; i < 3549; i++)
                {
                    for (int c = 0; c < 85; c++)
                    {
                        rented[idx++] = output[0, i, c];
                    }
                }
                buffer = rented.AsSpan(0, totalElements);
            }

            Span<Detection> stackBuf = stackalloc Detection[128];
            int rc = vn_smartcrop_parse_yolox_and_nms(buffer, imgWidth, imgHeight, scale, stackBuf, stackBuf.Length, out int rawCount, out int nmsCount);
            VNotch.Services.RuntimeLog.Log(LogCategory,
                $"ParseYolox: raw={rawCount} predictions scanned=3549 classes=80");

            if (rc == 0)
            {
                var list = new List<Detection>(nmsCount);
                for (int i = 0; i < nmsCount; i++) list.Add(stackBuf[i]);
                return list;
            }
            if (rc == -2 && nmsCount > 0)
            {
                var arr = new Detection[nmsCount];
                if (vn_smartcrop_parse_yolox_and_nms(buffer, imgWidth, imgHeight, scale, arr, arr.Length, out _, out int actualCount) == 0)
                {
                    var list = new List<Detection>(actualCount);
                    for (int i = 0; i < actualCount; i++) list.Add(arr[i]);
                    return list;
                }
            }
            return new List<Detection>();
        }
        finally
        {
            if (rented != null)
                ArrayPool<float>.Shared.Return(rented);
        }
    }

    internal static List<Detection> ApplyNms(List<Detection> detections)
    {
        if (detections.Count == 0) return new List<Detection>();
        ReadOnlySpan<Detection> inSpan = CollectionsMarshal.AsSpan(detections);
        Span<Detection> outSpan = detections.Count <= 256 ? stackalloc Detection[detections.Count] : new Detection[detections.Count];
        if (vn_smartcrop_apply_nms(inSpan, inSpan.Length, outSpan, outSpan.Length, out int outCount) == 0)
        {
            var result = new List<Detection>(outCount);
            for (int i = 0; i < outCount; i++) result.Add(outSpan[i]);
            return result;
        }
        return new List<Detection>();
    }

    private static Int32Rect? TryDetectTextCropRect(BitmapSource source, int imgWidth, int imgHeight, int cropSize)
    {
        try
        {
            if (!ArtworkFrameDetector.RentDownscaledPixels(source, out byte[]? pixels, out int w, out int h, out int stride, 128.0) || pixels == null)
                return null;

            try
            {
                if (w < 8 || h < 8) return null;

                if (vn_smartcrop_try_detect_text_crop_rect(pixels.AsSpan(0, h * stride), w, h, stride, imgWidth, imgHeight, cropSize, out var rect, out byte found) == 0 && found != 0)
                {
                    return rect.ToRect();
                }
                return null;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pixels);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SmartCrop] Text detection failed: {ex.Message}");
            return null;
        }
    }

    private static Int32Rect GetHybridCropRect(IReadOnlyList<Detection> detections, BitmapSource source, int imgWidth, int imgHeight, int targetSize)
    {
        int maxCropSize = Math.Min(imgWidth, imgHeight);
        int cropSize = Math.Min(targetSize, maxCropSize);

        var persons = new List<Detection>();
        var objects = new List<Detection>();

        foreach (var d in detections)
        {
            if (d.ClassId == 0)
                persons.Add(d);
            else
                objects.Add(d);
        }

        VNotch.Services.RuntimeLog.Log(LogCategory,
            $"img={imgWidth}x{imgHeight} detections total={detections.Count} persons={persons.Count} objects={objects.Count}");

        if (persons.Count >= 1)
        {
            float bestConfidence = persons.Max(static p => p.Confidence);
            persons.RemoveAll(p => p.Confidence < bestConfidence * 0.5f);
            VNotch.Services.RuntimeLog.Log(LogCategory,
                $"person path: {persons.Count} person(s) -> centered crop");
            return GetPersonCropRect(persons, imgWidth, imgHeight, targetSize);
        }

        if (objects.Count > 0)
        {
            vn_smartcrop_get_best_object_crop_rect(CollectionsMarshal.AsSpan(objects), objects.Count, imgWidth, imgHeight, targetSize, out var objRect);
            return objRect.ToRect();
        }

        if (ArtworkFrameDetector.RentDownscaledPixels(source, out byte[]? pixels, out int w, out int h, out int stride, 128.0) && pixels != null)
        {
            try
            {
                ReadOnlySpan<byte> pixelSpan = pixels.AsSpan(0, h * stride);
                if (w >= 8 && h >= 8 &&
                    vn_smartcrop_try_detect_text_crop_rect(pixelSpan, w, h, stride, imgWidth, imgHeight, cropSize, out var textRect, out byte found) == 0 &&
                    found != 0)
                {
                    VNotch.Services.RuntimeLog.Log(LogCategory, "text path -> centered text crop");
                    return textRect.ToRect();
                }

                VNotch.Services.RuntimeLog.Log(LogCategory, "no person/object/text -> saliency fallback");
                if (w >= 4 && h >= 4 &&
                    vn_smartcrop_compute_saliency_crop_rect(pixelSpan, w, h, stride, imgWidth, imgHeight, targetSize, out var salRect) == 0)
                {
                    return salRect.ToRect();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pixels);
            }
        }

        int cropX = Math.Max(0, Math.Min((imgWidth - cropSize) / 2, imgWidth - cropSize));
        int cropY = Math.Max(0, Math.Min((imgHeight - cropSize) / 2, imgHeight - cropSize));
        return new Int32Rect(cropX, cropY, cropSize, cropSize);
    }

    private static Int32Rect GetPersonCropRect(List<Detection> persons, int imgWidth, int imgHeight, int targetSize)
    {
        return persons.Count == 1
            ? GetSinglePersonCropRect(persons[0], imgWidth, imgHeight, targetSize)
            : GetGroupPersonCropRect(persons, imgWidth, imgHeight, targetSize);
    }

    private async Task<Int32Rect?> TryGetPortraitCropAsync(BitmapSource source, Detection person, int width, int height, CancellationToken cancellationToken)
    {
        if (!global::Windows.Media.FaceAnalysis.FaceDetector.IsSupported) return null;
        try
        {
            double scale = Math.Min(1, 640.0 / Math.Max(width, height));
            var resized = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            resized.Freeze();
            var gray = new FormatConvertedBitmap(resized, PixelFormats.Gray8, null, 0);
            gray.Freeze();
            int length = gray.PixelWidth * gray.PixelHeight;
            byte[] pixels = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                gray.CopyPixels(pixels, gray.PixelWidth, 0);
                using var bitmap = new global::Windows.Graphics.Imaging.SoftwareBitmap(
                    global::Windows.Graphics.Imaging.BitmapPixelFormat.Gray8, gray.PixelWidth, gray.PixelHeight);
                bitmap.CopyFromBuffer(pixels.AsBuffer(0, length));
                var detector = await global::Windows.Media.FaceAnalysis.FaceDetector.CreateAsync().AsTask(cancellationToken).ConfigureAwait(false);
                var faces = await detector.DetectFacesAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
                Detection? best = null;
                float bestArea = 0;
                foreach (var face in faces)
                {
                    var box = face.FaceBox;
                    var candidate = new Detection
                    {
                        X1 = (float)(box.X / scale),
                        Y1 = (float)(box.Y / scale),
                        X2 = (float)((box.X + box.Width) / scale),
                        Y2 = (float)((box.Y + box.Height) / scale)
                    };
                    float cx = (candidate.X1 + candidate.X2) / 2;
                    float cy = (candidate.Y1 + candidate.Y2) / 2;
                    float area = (candidate.X2 - candidate.X1) * (candidate.Y2 - candidate.Y1);
                    if (cx < person.X1 || cx > person.X2 || cy < person.Y1 || cy > person.Y2 || area <= bestArea) continue;
                    best = candidate;
                    bestArea = area;
                }
                return best is Detection detected ? GetPortraitCropRect(detected, width, height) : null;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pixels);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug(LogCategory, () => $"Face framing unavailable: {ex.Message}");
            return null;
        }
    }

    internal static Int32Rect GetPortraitCropRect(Detection face, int width, int height)
    {
        vn_smartcrop_get_portrait_crop_rect(in face, width, height, out var rect);
        return rect.ToRect();
    }

    internal static Int32Rect GetSinglePersonCropRect(Detection p, int imgWidth, int imgHeight, int targetSize)
    {
        vn_smartcrop_get_single_person_crop_rect(in p, imgWidth, imgHeight, targetSize, out var rect);
        return rect.ToRect();
    }

    private static Int32Rect GetGroupPersonCropRect(List<Detection> persons, int imgWidth, int imgHeight, int targetSize)
    {
        vn_smartcrop_get_group_person_crop_rect(CollectionsMarshal.AsSpan(persons), persons.Count, imgWidth, imgHeight, targetSize, out var rect);
        return rect.ToRect();
    }

    private static Int32Rect? GetSaliencyCropRect(BitmapSource source, int imgWidth, int imgHeight, int targetSize)
    {
        try
        {
            if (!ArtworkFrameDetector.RentDownscaledPixels(source, out byte[]? pixels, out int w, out int h, out int stride, 64.0) || pixels == null)
                return null;

            try
            {
                if (w < 4 || h < 4) return null;

                if (vn_smartcrop_compute_saliency_crop_rect(pixels.AsSpan(0, h * stride), w, h, stride, imgWidth, imgHeight, targetSize, out var rect) == 0)
                {
                    return rect.ToRect();
                }
                return null;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pixels);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SmartCrop] Saliency analysis failed: {ex.Message}");
            return null;
        }
    }

    private static string GetModelPath()
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(appDir, "Models", "yolox_nano.onnx");
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_lock)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
            _cachedSession?.Dispose();
            _cachedSession = null;
            _inferenceCache.Clear();
            _cropCache.Clear();
            _cropCacheOrder.Clear();
        }
    }

    private sealed class FingerprintHolder
    {
        public FingerprintHolder(ArtworkFingerprint value) => Value = value;
        public ArtworkFingerprint Value { get; }
    }

    private sealed class InferenceCacheEntry
    {
        public InferenceCacheEntry(Detection[] detections, long lastAccess)
        {
            Detections = detections;
            LastAccess = lastAccess;
        }

        public Detection[] Detections { get; }
        public long LastAccess { get; set; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct Detection
    {
        public float X1, Y1, X2, Y2;
        public float Confidence;
        public int ClassId;
    }
}

public readonly record struct SubjectBounds(
    float CenterX,
    float CenterY,
    float Width,
    float Height,
    float Confidence,
    int ClassId);
