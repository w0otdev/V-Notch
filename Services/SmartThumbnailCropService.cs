using System.Runtime.InteropServices.WindowsRuntime;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

public sealed partial class SmartThumbnailCropService : IDisposable
{
    private const string LogCategory = "SMART-CROP";
    private readonly object _lock = new();

    private readonly Dictionary<(ArtworkFingerprint Artwork, int Size, bool ObjectDetection), Int32Rect> _cropCache = new();
    private readonly Queue<(ArtworkFingerprint Artwork, int Size, bool ObjectDetection)> _cropCacheOrder = new();

    private bool _disposed;
    private bool _modelExists;
    private bool _modelExistsChecked;
    private NativeCropSession? _cachedSession;

    private const int MaxInferenceCacheEntries = 24;
    private readonly Dictionary<ArtworkFingerprint, InferenceCacheEntry> _inferenceCache = new();
    private readonly ConditionalWeakTable<BitmapSource, FingerprintHolder> _fingerprintCache = new();
    private long _inferenceCacheAccess;

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(5);

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

        public readonly SubjectBounds ToSubjectBounds() =>
            new(CenterX, CenterY, Width, Height, Confidence, ClassId);
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

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_smartcrop_session_create(string model, int modelLength,
        string directory, int directoryLength, byte preferDml, out long handle, out byte dml);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_smartcrop_session_destroy(long handle);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_smartcrop_session_infer(NativeCropSession handle, ReadOnlySpan<byte> pixels,
        int length, int width, int height, int stride, Span<Detection> detections, int capacity,
        out int count, out byte dml);

    private sealed class NativeCropSession : SafeHandle
    {
        public NativeCropSession() : base(IntPtr.Zero, true) { }
        internal NativeCropSession(long value) : this() => SetHandle((IntPtr)value);
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle() => vn_smartcrop_session_destroy((long)handle) == 0;
    }

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
    private static partial int vn_smartcrop_select_single_prominent_person(
        ReadOnlySpan<Detection> detections, int count,
        out Detection outPerson, out byte outFound);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_smartcrop_translate_crop_detections(
        ReadOnlySpan<Detection> src, int srcCount,
        int cropX, int cropY, int cropWidth, int cropHeight,
        Span<Detection> outDst, int dstCap, out int outWritten);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_smartcrop_partition_hybrid_candidates(
        ReadOnlySpan<Detection> src, int srcCount,
        Span<Detection> outCandidates, int cap,
        out int outPersonCount, out int outObjectCount,
        out int outKeptCount, out byte outIsPersonMode);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_smartcrop_select_portrait_face(
        ReadOnlySpan<VnCropRect> faceBoxes, int faceCount,
        double scale, in Detection person,
        out Detection outBestFace, out byte outFound);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_smartcrop_compute_saliency_subject_bounds(
        ReadOnlySpan<byte> pixels, int w, int h, int stride,
        out VnSubjectBounds outBounds, out byte outFound);

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

    public void ClearCaches()
    {
        lock (_lock)
        {
            _cropCache.Clear();
            _cropCacheOrder.Clear();
            _inferenceCache.Clear();
            _fingerprintCache.Clear();
            Unload();
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
            }
        }
    }

    public SubjectBounds? GetDominantSubjectBounds(BitmapSource source)
    {
        if (_disposed || source == null) return null;

        int imgWidth = source.PixelWidth;
        int imgHeight = source.PixelHeight;
        if (imgWidth < 64 || imgHeight < 64) return null;

        lock (_lock)
        {
            if (_disposed) return null;
            try
            {
                ArtworkFingerprint fingerprint = _fingerprintCache
                    .GetValue(source, static bitmap => new FingerprintHolder(ArtworkFingerprint.Create(bitmap)))
                    .Value;

                Detection[]? candidateDetections = null;
                if (_inferenceCache.TryGetValue(fingerprint, out var cached))
                {
                    cached.LastAccess = ++_inferenceCacheAccess;
                    candidateDetections = cached.Detections;
                }
                else if (source is CroppedBitmap crop && TryGetCachedCropDetections(crop, out var croppedDetections))
                {
                    candidateDetections = croppedDetections;
                }

                if (candidateDetections is { Length: > 0 } &&
                    vn_smartcrop_find_best_subject_bounds(candidateDetections, candidateDetections.Length, imgWidth, imgHeight, out var cachedBounds, out byte cachedFound) == 0 &&
                    cachedFound != 0)
                {
                    return cachedBounds.ToSubjectBounds();
                }
            }
            catch (Exception ex)
            {
                VNotch.Services.RuntimeLog.Error("SUBJECT-BOUNDS", $"Cached detection lookup failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Backdrop blur must not load YOLOX/DirectML. Reuse detections only when
        // already cached; cold artwork uses bounded C++ saliency analysis.
        if (!ArtworkFrameDetector.RentDownscaledPixels(source, out byte[]? pixels, out int w, out int h, out int stride, 96.0) || pixels == null)
            return null;

        try
        {
            if (w >= 8 && h >= 8 &&
                vn_smartcrop_compute_saliency_subject_bounds(pixels.AsSpan(0, h * stride), w, h, stride, out var salBounds, out byte salFound) == 0 &&
                salFound != 0)
            {
                return salBounds.ToSubjectBounds();
            }
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    public Int32Rect? GetSmartCropRect(BitmapSource source, int targetSquareSize, bool useObjectDetection = false) =>
        GetCpuCropRect(source, targetSquareSize, useObjectDetection);

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
                    ?? GetSaliencyCropRect(source, width, height, targetSquareSize)
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
            if (!fallback.HasValue || !useObjectDetection || targetSquareSize <= 96) return fallback;
            Detection[] detections;
            lock (_lock)
            {
                if (_disposed) return null;
                var fingerprint = _fingerprintCache.GetValue(bitmap,
                    static value => new FingerprintHolder(ArtworkFingerprint.Create(value))).Value;
                if (!_inferenceCache.TryGetValue(fingerprint, out var cached)) return fallback;
                detections = cached.Detections;
            }
            if (!TrySelectSingleProminentPerson(detections, out var person)) return fallback;
            return await TryGetPortraitCropAsync(bitmap, person, width, height, cancellationToken)
                .ConfigureAwait(false) ?? fallback;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool TrySelectSingleProminentPerson(ReadOnlySpan<Detection> detections, out Detection person)
    {
        if (vn_smartcrop_select_single_prominent_person(detections, detections.Length, out person, out byte found) != 0 || found == 0)
        {
            person = default;
            return false;
        }
        return true;
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

                // Decoded video thumbnails are commonly 320x180 or 384x288.
                // They still contain useful person detections after model preprocessing.
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

        int stride = checked(imgWidth * 4);
        int length = checked(stride * imgHeight);
        byte[] pixels = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            CopyBgraPixels(source, pixels, stride);
            Span<Detection> output = stackalloc Detection[3549];
            if (vn_smartcrop_session_infer(_cachedSession!, pixels.AsSpan(0, length), length,
                imgWidth, imgHeight, stride, output, output.Length, out int count, out _) != 0)
                throw new InvalidOperationException("Native YOLOX inference failed.");
            Detection[] detections = output[..count].ToArray();
            AddInferenceCacheEntryLocked(fingerprint, detections);
            return detections;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private void EnsureSessionLoadedLocked()
    {
        if (_cachedSession != null)
            return;

        string path = GetModelPath(), directory = AppContext.BaseDirectory;
        // DirectML initializes D3D12 + DirectML heaps (+130-160 MB working set that persists across unloads).
        // YOLOX-Nano 416x416 runs in ~8ms on AVX2 CPU without GPU driver memory retention.
        if (vn_smartcrop_session_create(path, path.Length, directory, directory.Length, 0, out long handle, out byte dml) != 0)
            throw new InvalidDataException("Could not load the bundled YOLOX-Nano model in the native runtime.");
        _cachedSession = new NativeCropSession(handle);
        RuntimeLog.Log(LogCategory, dml != 0 ? "Native YOLOX loaded (DirectML)" : "Native YOLOX loaded (CPU)");
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
        int srcLen = sourceDetections.Length;
        if (srcLen == 0)
        {
            detections = Array.Empty<Detection>();
            return true;
        }

        Span<Detection> translatedBuf = srcLen <= 64 ? stackalloc Detection[srcLen] : new Detection[srcLen];
        if (vn_smartcrop_translate_crop_detections(
                sourceDetections, srcLen, rect.X, rect.Y, rect.Width, rect.Height,
                translatedBuf, translatedBuf.Length, out int written) != 0 || written <= 0)
        {
            detections = Array.Empty<Detection>();
            return true;
        }

        detections = translatedBuf[..written].ToArray();
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

    private static void CopyBgraPixels(BitmapSource source, byte[] pixels, int stride)
    {
        if (source.Format != PixelFormats.Bgra32)
        {
            var formatted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            formatted.Freeze();
            formatted.CopyPixels(pixels, stride, 0);
        }
        else source.CopyPixels(pixels, stride, 0);
    }

    private static Int32Rect GetHybridCropRect(IReadOnlyList<Detection> detections, BitmapSource source, int imgWidth, int imgHeight, int targetSize)
    {
        int maxCropSize = Math.Min(imgWidth, imgHeight);
        int cropSize = Math.Min(targetSize, maxCropSize);

        int totalCount = detections.Count;
        Span<Detection> srcSpan = totalCount <= 64 ? stackalloc Detection[totalCount] : new Detection[totalCount];
        for (int i = 0; i < totalCount; i++)
            srcSpan[i] = detections[i];

        Span<Detection> buffer = totalCount <= 64 ? stackalloc Detection[totalCount] : new Detection[totalCount];
        vn_smartcrop_partition_hybrid_candidates(
            srcSpan, totalCount, buffer, buffer.Length,
            out int personCount, out int objectCount, out int keptCount, out byte isPersonMode);

        VNotch.Services.RuntimeLog.Log(LogCategory,
            $"img={imgWidth}x{imgHeight} detections total={totalCount} persons={personCount} objects={objectCount}");

        if (isPersonMode != 0)
        {
            VNotch.Services.RuntimeLog.Log(LogCategory,
                $"person path: {keptCount} person(s) -> centered crop");
            return GetPersonCropRect(buffer[..keptCount], imgWidth, imgHeight, targetSize);
        }

        if (objectCount > 0)
        {
            vn_smartcrop_get_best_object_crop_rect(buffer[..keptCount], keptCount, imgWidth, imgHeight, targetSize, out var objRect);
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

    private static Int32Rect GetPersonCropRect(ReadOnlySpan<Detection> persons, int imgWidth, int imgHeight, int targetSize)
    {
        return persons.Length == 1
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
                return SelectPortraitCrop(faces, scale, in person, width, height);
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

    private static Int32Rect? SelectPortraitCrop(
        System.Collections.Generic.IList<global::Windows.Media.FaceAnalysis.DetectedFace> faces,
        double scale,
        in Detection person,
        int width,
        int height)
    {
        int faceCount = faces.Count;
        if (faceCount == 0) return null;
        Span<VnCropRect> faceBoxes = faceCount <= 32 ? stackalloc VnCropRect[faceCount] : new VnCropRect[faceCount];
        for (int i = 0; i < faceCount; i++)
        {
            var box = faces[i].FaceBox;
            faceBoxes[i] = new VnCropRect
            {
                X = (int)box.X,
                Y = (int)box.Y,
                Width = (int)box.Width,
                Height = (int)box.Height
            };
        }
        return vn_smartcrop_select_portrait_face(faceBoxes, faceCount, scale, in person, out var detected, out byte foundFace) == 0 && foundFace != 0
            ? GetPortraitCropRect(detected, width, height)
            : null;
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

    private static Int32Rect GetGroupPersonCropRect(ReadOnlySpan<Detection> persons, int imgWidth, int imgHeight, int targetSize)
    {
        vn_smartcrop_get_group_person_crop_rect(persons, persons.Length, imgWidth, imgHeight, targetSize, out var rect);
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
