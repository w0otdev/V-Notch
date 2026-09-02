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
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VNotch.Services;

public sealed class SmartThumbnailCropService : IDisposable
{
    private readonly object _lock = new();

    private const int ModelInputSize = 416;

    private const float ConfidenceThreshold = 0.35f;
    private const float PersonConfidenceThreshold = 0.10f;
    private const float NmsThreshold = 0.50f;
    private const float MinAreaRatio = 0.02f;
    private const float MinPersonAreaRatio = 0.005f;

    private const float SubjectMarginRatio = 0.15f;
    private const float MinCropRatio = 0.45f;

    private const float StabilizeCenterThreshold = 0.03f;
    private const float StabilizeSizeThreshold = 0.04f;

    private Int32Rect _lastCropRect;
    private int _lastCropImgWidth;
    private int _lastCropImgHeight;
    private bool _hasLastCrop;

    private bool _disposed;
    private bool _modelExists;
    private bool _modelExistsChecked;
    private InferenceSession? _cachedSession;

    private const int MaxInferenceCacheEntries = 64;
    private readonly Dictionary<ArtworkFingerprint, InferenceCacheEntry> _inferenceCache = new();
    private readonly ConditionalWeakTable<BitmapSource, FingerprintHolder> _fingerprintCache = new();
    private long _inferenceCacheAccess;

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    private DateTime _lastUsedUtc;
    private System.Threading.Timer? _idleTimer;

    private static readonly HashSet<int> _personClasses = new() { 0 };
    private static readonly HashSet<int> _animalClasses = new()
    {
        14, 15, 16, 17, 18, 19, 20, 21, 22, 23
    };
    private static readonly HashSet<int> _productClasses = new()
    {
        39, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59,
        60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79
    };

    private static float GetClassPriority(int classId)
    {
        if (_personClasses.Contains(classId)) return 5.0f;
        if (_animalClasses.Contains(classId)) return 2.5f;
        if (_productClasses.Contains(classId)) return 2.0f;
        return 1.0f;
    }

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
                VNotch.Services.RuntimeLog.Log("SMART-CROP", "Session unloaded after idle timeout");
            }
        }
    }

    public SubjectBounds? GetDominantSubjectBounds(BitmapImage source)
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

                Detection? best = null;
                float bestScore = float.MinValue;
                float imgArea = imgWidth * imgHeight;

                foreach (var d in detections)
                {
                    float area = (d.X2 - d.X1) * (d.Y2 - d.Y1);
                    if (area <= 0) continue;
                    float areaShare = area / imgArea;
                    float score = d.Confidence * GetClassPriority(d.ClassId) * MathF.Sqrt(areaShare) * GetCenterWeight(d, imgWidth, imgHeight);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = d;
                    }
                }

                if (best is not Detection b) return null;

                float cx = (b.X1 + b.X2) / 2f / imgWidth;
                float cy = (b.Y1 + b.Y2) / 2f / imgHeight;
                float w = (b.X2 - b.X1) / imgWidth;
                float h = (b.Y2 - b.Y1) / imgHeight;

                if (_personClasses.Contains(b.ClassId))
                {
                    cy = (b.Y1 + (b.Y2 - b.Y1) * 0.12f) / imgHeight;
                }

                return new SubjectBounds(cx, cy, w, h, b.Confidence, b.ClassId);
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

    public Int32Rect? GetSmartCropRect(BitmapImage source, int targetSquareSize)
    {
        var rect = ComputeSmartCropRectCore(source, targetSquareSize);
        if (rect.HasValue && source != null)
        {
            return Stabilize(rect.Value, source.PixelWidth, source.PixelHeight);
        }
        return rect;
    }

    private Int32Rect? ComputeSmartCropRectCore(BitmapImage source, int targetSquareSize)
    {
        if (_disposed) return null;
        if (!_modelExists && !TryInitialize())
        {
            VNotch.Services.RuntimeLog.Log("SMART-CROP", $"GetSmartCropRect: model init failed");
            return null;
        }
        if (!_modelExists)
        {
            VNotch.Services.RuntimeLog.Log("SMART-CROP", $"GetSmartCropRect: model not exist");
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
                    VNotch.Services.RuntimeLog.Log("SMART-CROP", $"GetSmartCropRect: skip (square/small) {imgWidth}x{imgHeight}");
                    return null;
                }

                if (imgWidth < 400 && imgHeight < 400)
                {
                    int maxCrop = Math.Min(imgWidth, imgHeight);
                    int cropSz = Math.Min(targetSquareSize, maxCrop);
                    VNotch.Services.RuntimeLog.Log("SMART-CROP", $"GetSmartCropRect: small image {imgWidth}x{imgHeight} -> saliency only");
                    return GetSaliencyCropRect(source, imgWidth, imgHeight, cropSz);
                }

                var detections = GetOrRunInferenceLocked(source, imgWidth, imgHeight);

                if (detections.Length == 0)
                {
                    VNotch.Services.RuntimeLog.Log("SMART-CROP", "ONNX produced 0 detections -> saliency fallback");
                    return GetSaliencyCropRect(source, imgWidth, imgHeight, targetSquareSize);
                }

                VNotch.Services.RuntimeLog.Log("SMART-CROP", $"raw detections count={detections.Length}");
                return GetHybridCropRect(detections, source, imgWidth, imgHeight, targetSquareSize);
            }
            catch (Exception ex)
            {
                VNotch.Services.RuntimeLog.Error("SMART-CROP", $"Inference failed: {ex.GetType().Name}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[SmartCrop] Inference failed: {ex.Message}");
                _cachedSession?.Dispose();
                _cachedSession = null;
                return null;
            }
        }
    }

    private Detection[] GetOrRunInferenceLocked(BitmapImage source, int imgWidth, int imgHeight)
    {
        ArtworkFingerprint fingerprint = _fingerprintCache
            .GetValue(source, static bitmap => new FingerprintHolder(ArtworkFingerprint.Create(bitmap)))
            .Value;

        if (_inferenceCache.TryGetValue(fingerprint, out var cached))
        {
            cached.LastAccess = ++_inferenceCacheAccess;
            VNotch.Services.RuntimeLog.Debug("SMART-CROP", () =>
                $"ONNX cache hit artwork={fingerprint.ContentHash:X16} detections={cached.Detections.Length}");
            return cached.Detections;
        }

        EnsureSessionLoadedLocked();
        MarkSessionUsed();

        float[]? tensorBuffer = null;
        try
        {
            int requiredLength = 3 * ModelInputSize * ModelInputSize;
            tensorBuffer = ArrayPool<float>.Shared.Rent(requiredLength);

            var (scale, _, padX, padY) = PreprocessImageFast(source, tensorBuffer);
            var tensor = new DenseTensor<float>(
                new Memory<float>(tensorBuffer, 0, requiredLength),
                new[] { 1, 3, ModelInputSize, ModelInputSize });
            var inputs = new List<NamedOnnxValue>(1)
            {
                NamedOnnxValue.CreateFromTensor(_cachedSession!.InputNames[0], tensor)
            };

            using var results = _cachedSession.Run(inputs);
            var output = results.First().AsTensor<float>();
            Detection[] detections = ParseYolov8Output(
                output, imgWidth, imgHeight, scale, padX, padY).ToArray();

            AddInferenceCacheEntryLocked(fingerprint, detections);
            return detections;
        }
        finally
        {
            if (tensorBuffer != null)
                ArrayPool<float>.Shared.Return(tensorBuffer);
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
            EnableMemoryPattern = true,
            EnableCpuMemArena = false
        };

        _cachedSession = new InferenceSession(GetModelPath(), options);
        System.Diagnostics.Debug.WriteLine("[SmartCrop] Model loaded (cached session).");
    }

    private void AddInferenceCacheEntryLocked(ArtworkFingerprint fingerprint, Detection[] detections)
    {
        if (_inferenceCache.Count >= MaxInferenceCacheEntries)
        {
            ArtworkFingerprint oldestKey = _inferenceCache
                .MinBy(static pair => pair.Value.LastAccess)
                .Key;
            _inferenceCache.Remove(oldestKey);
        }

        _inferenceCache[fingerprint] = new InferenceCacheEntry(detections, ++_inferenceCacheAccess);
    }

    private (float scale, float scaleY, float padX, float padY) PreprocessImageFast(BitmapImage source, float[] tensorBuffer)
    {
        int imgWidth = source.PixelWidth;
        int imgHeight = source.PixelHeight;

        float scale = Math.Min((float)ModelInputSize / imgWidth, (float)ModelInputSize / imgHeight);
        int newWidth = (int)(imgWidth * scale);
        int newHeight = (int)(imgHeight * scale);
        float padX = (ModelInputSize - newWidth) / 2f;
        float padY = (ModelInputSize - newHeight) / 2f;
        int padXi = (int)padX;
        int padYi = (int)padY;

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

            int planeSize = ModelInputSize * ModelInputSize;
            const float grayVal = 114f / 255f;
            const float inv255 = 1f / 255f;

            tensorBuffer.AsSpan().Fill(grayVal);

            for (int y = 0; y < scaledH && (y + padYi) < ModelInputSize; y++)
            {
                int tensorY = y + padYi;
                int rowOffset = y * stride;
                int tensorRowBase = tensorY * ModelInputSize;

                for (int x = 0; x < scaledW && (x + padXi) < ModelInputSize; x++)
                {
                    int pixelIdx = rowOffset + x * 4;
                    int tensorIdx = tensorRowBase + (x + padXi);

                    tensorBuffer[tensorIdx] = pixels[pixelIdx + 2] * inv255;
                    tensorBuffer[planeSize + tensorIdx] = pixels[pixelIdx + 1] * inv255;
                    tensorBuffer[2 * planeSize + tensorIdx] = pixels[pixelIdx] * inv255;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }

        return (scale, scale, padX, padY);
    }

    private List<Detection> ParseYolov8Output(Tensor<float> output, int imgWidth, int imgHeight, float scale, float padX, float padY)
    {
        var detections = new List<Detection>(32);
        var dims = output.Dimensions;
        int numChannels = dims[1];
        int numPredictions = dims[2];
        int numClasses = numChannels - 4;
        float imgArea = imgWidth * imgHeight;

        ReadOnlySpan<float> buffer = output is DenseTensor<float> dense
            ? dense.Buffer.Span
            : ReadOnlySpan<float>.Empty;
        bool useSpan = buffer.Length >= numChannels * numPredictions;

        for (int i = 0; i < numPredictions; i++)
        {
            float maxScore = 0f;
            int maxClassId = -1;

            for (int c = 4; c < numChannels; c++)
            {
                float score = useSpan ? buffer[c * numPredictions + i] : output[0, c, i];
                if (score > maxScore)
                {
                    maxScore = score;
                    maxClassId = c - 4;
                }
            }

            if (maxClassId < 0) continue;

            bool isPerson = _personClasses.Contains(maxClassId);
            float threshold = isPerson ? PersonConfidenceThreshold : ConfidenceThreshold;
            if (maxScore < threshold) continue;

            float cx = useSpan ? buffer[i] : output[0, 0, i];
            float cy = useSpan ? buffer[numPredictions + i] : output[0, 1, i];
            float w = useSpan ? buffer[2 * numPredictions + i] : output[0, 2, i];
            float h = useSpan ? buffer[3 * numPredictions + i] : output[0, 3, i];

            float x1 = (cx - w / 2f - padX) / scale;
            float y1 = (cy - h / 2f - padY) / scale;
            float x2 = (cx + w / 2f - padX) / scale;
            float y2 = (cy + h / 2f - padY) / scale;

            x1 = Math.Clamp(x1, 0, imgWidth);
            y1 = Math.Clamp(y1, 0, imgHeight);
            x2 = Math.Clamp(x2, 0, imgWidth);
            y2 = Math.Clamp(y2, 0, imgHeight);

            float bboxArea = (x2 - x1) * (y2 - y1);
            float areaRatio = bboxArea / imgArea;

            if (isPerson)
            {
                if (areaRatio < MinPersonAreaRatio) continue;
            }
            else
            {
                if (areaRatio < MinAreaRatio) continue;
            }

            if (x2 - x1 < 5 || y2 - y1 < 5) continue;

            detections.Add(new Detection
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Confidence = maxScore,
                ClassId = maxClassId
            });
        }

        VNotch.Services.RuntimeLog.Log("SMART-CROP",
            $"ParseYolov8: raw={detections.Count} predictions scanned={numPredictions} classes={numClasses}");

        return detections.Count > 1 ? ApplyNms(detections) : detections;
    }

    private List<Detection> ApplyNms(List<Detection> detections)
    {
        var sorted = detections.OrderByDescending(d => d.Confidence).ToList();
        var result = new List<Detection>(sorted.Count);

        Span<bool> suppressed = stackalloc bool[sorted.Count];
        for (int i = 0; i < sorted.Count; i++)
        {
            if (suppressed[i]) continue;
            result.Add(sorted[i]);

            for (int j = i + 1; j < sorted.Count; j++)
            {
                if (suppressed[j]) continue;
                if (IoU(sorted[i], sorted[j]) > NmsThreshold)
                    suppressed[j] = true;
            }
        }

        return result;
    }

    private static float IoU(Detection a, Detection b)
    {
        float interX1 = Math.Max(a.X1, b.X1);
        float interY1 = Math.Max(a.Y1, b.Y1);
        float interX2 = Math.Min(a.X2, b.X2);
        float interY2 = Math.Min(a.Y2, b.Y2);

        float interArea = Math.Max(0, interX2 - interX1) * Math.Max(0, interY2 - interY1);
        float areaA = (a.X2 - a.X1) * (a.Y2 - a.Y1);
        float areaB = (b.X2 - b.X1) * (b.Y2 - b.Y1);

        return interArea / (areaA + areaB - interArea + 1e-6f);
    }

    private static float GetCenterWeight(Detection d, int imgWidth, int imgHeight)
    {
        float objCenterX = (d.X1 + d.X2) / 2f;
        float objCenterY = (d.Y1 + d.Y2) / 2f;
        float imgCenterX = imgWidth / 2f;
        float imgCenterY = imgHeight / 2f;

        float dx = (objCenterX - imgCenterX) / imgCenterX;
        float dy = (objCenterY - imgCenterY) / imgCenterY;
        float dist = MathF.Sqrt(dx * dx + dy * dy) / MathF.Sqrt(2f);

        return 1.0f - dist * 0.5f;
    }

    private List<TextRegion> DetectTextRegions(BitmapImage source, int imgWidth, int imgHeight)
    {
        var regions = new List<TextRegion>();

        try
        {
            const int gridSize = 8;
            int cellW = imgWidth / gridSize;
            int cellH = imgHeight / gridSize;

            const int analysisSize = 128;
            double scaleX = (double)analysisSize / imgWidth;
            double scaleY = (double)analysisSize / imgHeight;
            var scaled = new TransformedBitmap(source, new ScaleTransform(scaleX, scaleY));
            scaled.Freeze();

            int w = scaled.PixelWidth;
            int h = scaled.PixelHeight;
            if (w < 8 || h < 8) return regions;

            int stride = w * 4;
            byte[] pixels = ArrayPool<byte>.Shared.Rent(h * stride);
            try
            {

                BitmapSource scaledSource = scaled;
                if (scaled.Format != PixelFormats.Bgra32)
                {
                    var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
                    converted.Freeze();
                    scaledSource = converted;
                }
                scaledSource.CopyPixels(pixels, stride, 0);

                int aCellW = w / gridSize;
                int aCellH = h / gridSize;

                for (int gy = 0; gy < gridSize; gy++)
                {
                    for (int gx = 0; gx < gridSize; gx++)
                    {
                        int startX = gx * aCellW;
                        int startY = gy * aCellH;
                        int endX = Math.Min(startX + aCellW, w - 1);
                        int endY = Math.Min(startY + aCellH, h - 1);

                        double edgeCount = 0;
                        int pixelCount = 0;

                        for (int y = startY; y < endY; y++)
                        {
                            for (int x = startX; x < endX; x++)
                            {
                                int i = y * stride + x * 4;
                                int iRight = i + 4;
                                int iBelow = (y + 1) * stride + x * 4;

                                if (x + 1 >= w || y + 1 >= h) continue;

                                double lum = 0.299 * pixels[i + 2] + 0.587 * pixels[i + 1] + 0.114 * pixels[i];
                                double lumR = 0.299 * pixels[iRight + 2] + 0.587 * pixels[iRight + 1] + 0.114 * pixels[iRight];
                                double lumB = 0.299 * pixels[iBelow + 2] + 0.587 * pixels[iBelow + 1] + 0.114 * pixels[iBelow];

                                double grad = Math.Abs(lum - lumR) + Math.Abs(lum - lumB);
                                if (grad > 40) edgeCount++;
                                pixelCount++;
                            }
                        }

                        if (pixelCount == 0) continue;

                        double edgeRatio = edgeCount / pixelCount;

                        if (edgeRatio > 0.30)
                        {
                            float regionHeight = cellH;
                            float fontFactor = MathF.Sqrt(regionHeight);

                            regions.Add(new TextRegion
                            {
                                X1 = gx * cellW,
                                Y1 = gy * cellH,
                                X2 = (gx + 1) * cellW,
                                Y2 = (gy + 1) * cellH,
                                EdgeDensity = (float)edgeRatio,
                                FontFactor = fontFactor
                            });
                        }
                    }
                }

            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pixels);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SmartCrop] Text detection failed: {ex.Message}");
        }

        return regions;
    }

    private Int32Rect GetHybridCropRect(IReadOnlyList<Detection> detections, BitmapImage source, int imgWidth, int imgHeight, int targetSize)
    {
        int maxCropSize = Math.Min(imgWidth, imgHeight);
        int cropSize = Math.Min(targetSize, maxCropSize);

        var persons = new List<Detection>();
        var objects = new List<Detection>();

        foreach (var d in detections)
        {
            if (_personClasses.Contains(d.ClassId))
                persons.Add(d);
            else
                objects.Add(d);
        }

        VNotch.Services.RuntimeLog.Log("SMART-CROP",
            $"img={imgWidth}x{imgHeight} detections total={detections.Count} persons={persons.Count} objects={objects.Count}");

        if (persons.Count >= 1)
        {
            VNotch.Services.RuntimeLog.Log("SMART-CROP",
                $"person path: {persons.Count} person(s) → centered crop");
            return GetPersonCropRect(persons, imgWidth, imgHeight, targetSize);
        }

        if (objects.Count > 0)
        {
            var bestObj = objects
                .Select(d => new
                {
                    Detection = d,
                    Score = d.Confidence * GetCenterWeight(d, imgWidth, imgHeight) * GetClassPriority(d.ClassId)
                })
                .OrderByDescending(x => x.Score)
                .First()
                .Detection;

            float objCenterX = (bestObj.X1 + bestObj.X2) / 2f;
            float objCenterY = (bestObj.Y1 + bestObj.Y2) / 2f;
            float objWidth = bestObj.X2 - bestObj.X1;
            float objHeight = bestObj.Y2 - bestObj.Y1;

            int objCrop = ComputeAdaptiveCropSize(objWidth, objHeight, imgWidth, imgHeight, targetSize);
            var objRect = BuildCropRect(objCenterX, objCenterY, imgWidth, imgHeight, objCrop);

            VNotch.Services.RuntimeLog.Log("SMART-CROP",
                $"object path: classId={bestObj.ClassId} center=({objCenterX:F0},{objCenterY:F0}) size=({objWidth:F0}x{objHeight:F0}) rect=({objRect.X},{objRect.Y},{objRect.Width})");

            return objRect;
        }

        var textRegions = DetectTextRegions(source, imgWidth, imgHeight);
        if (textRegions.Count >= 2)
        {
            VNotch.Services.RuntimeLog.Log("SMART-CROP",
                $"text path: textRegions={textRegions.Count} → centered text crop");
            return GetTextFirstCropRect(textRegions, imgWidth, imgHeight, cropSize);
        }

        VNotch.Services.RuntimeLog.Log("SMART-CROP", "no person/object/text -> saliency fallback");
        var saliencyRect = GetSaliencyCropRect(source, imgWidth, imgHeight, targetSize);
        if (saliencyRect.HasValue)
        {
            VNotch.Services.RuntimeLog.Log("SMART-CROP",
                $"saliency rect=({saliencyRect.Value.X},{saliencyRect.Value.Y},{saliencyRect.Value.Width}x{saliencyRect.Value.Height})");
            return saliencyRect.Value;
        }

        VNotch.Services.RuntimeLog.Log("SMART-CROP", "saliency null -> exact center");
        return BuildCropRect(imgWidth / 2f, imgHeight / 2f, imgWidth, imgHeight, cropSize);
    }

    private Int32Rect GetPersonCropRect(List<Detection> persons, int imgWidth, int imgHeight, int targetSize)
    {
        if (persons.Count == 1)
        {
            var p = persons[0];
            float personWidth = p.X2 - p.X1;
            float personHeight = p.Y2 - p.Y1;

            float personCenterX = (p.X1 + p.X2) / 2f;
            float personCenterY = (p.Y1 + p.Y2) / 2f;

            int cropSize = ComputeAdaptiveCropSize(personWidth, personHeight, imgWidth, imgHeight, targetSize);
            var crop = BuildCropRect(personCenterX, personCenterY, imgWidth, imgHeight, cropSize);

            VNotch.Services.RuntimeLog.Log("SMART-CROP",
                $"single person: bbox=({p.X1:F0},{p.Y1:F0})-({p.X2:F0},{p.Y2:F0}) center=({personCenterX:F0},{personCenterY:F0}) cropSize={cropSize} rect=({crop.X},{crop.Y},{crop.Width})");

            return crop;
        }
        else
        {
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            foreach (var p in persons)
            {
                if (p.X1 < minX) minX = p.X1;
                if (p.Y1 < minY) minY = p.Y1;
                if (p.X2 > maxX) maxX = p.X2;
                if (p.Y2 > maxY) maxY = p.Y2;
            }

            float groupW = maxX - minX;
            float groupH = maxY - minY;

            float groupCenterX = (minX + maxX) / 2f;
            float groupCenterY = (minY + maxY) / 2f;

            int cropSize = ComputeAdaptiveCropSize(groupW, groupH, imgWidth, imgHeight, targetSize);
            var crop = BuildCropRect(groupCenterX, groupCenterY, imgWidth, imgHeight, cropSize);

            VNotch.Services.RuntimeLog.Log("SMART-CROP",
                $"group ({persons.Count} persons): union=({minX:F0},{minY:F0})-({maxX:F0},{maxY:F0}) center=({groupCenterX:F0},{groupCenterY:F0}) cropSize={cropSize} rect=({crop.X},{crop.Y},{crop.Width})");

            return crop;
        }
    }

    private Int32Rect GetTextFirstCropRect(List<TextRegion> textRegions, int imgWidth, int imgHeight, int cropSize)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;

        foreach (var t in textRegions)
        {
            if (t.X1 < minX) minX = t.X1;
            if (t.X2 > maxX) maxX = t.X2;
            if (t.Y1 < minY) minY = t.Y1;
            if (t.Y2 > maxY) maxY = t.Y2;
        }

        float textCenterX = (minX + maxX) / 2f;
        float textCenterY = (minY + maxY) / 2f;

        return BuildCropRect(textCenterX, textCenterY, imgWidth, imgHeight, cropSize);
    }

    private static int ComputeAdaptiveCropSize(float subjectW, float subjectH, int imgWidth, int imgHeight, int targetSize)
    {
        int maxCrop = Math.Min(imgWidth, imgHeight);

        float subjectExtent = Math.Max(subjectW, subjectH);
        float desired = subjectExtent * (1f + 2f * SubjectMarginRatio);

        float minCrop = maxCrop * MinCropRatio;
        float size = Math.Clamp(desired, minCrop, maxCrop);

        if (targetSize > 0)
            size = Math.Max(size, Math.Min(targetSize, maxCrop));

        return (int)MathF.Round(size);
    }

    private Int32Rect Stabilize(Int32Rect candidate, int imgWidth, int imgHeight)
    {
        if (_hasLastCrop && _lastCropImgWidth == imgWidth && _lastCropImgHeight == imgHeight)
        {
            float minEdge = Math.Min(imgWidth, imgHeight);
            float centerThreshold = minEdge * StabilizeCenterThreshold;
            float sizeThreshold = minEdge * StabilizeSizeThreshold;

            float oldCx = _lastCropRect.X + _lastCropRect.Width / 2f;
            float oldCy = _lastCropRect.Y + _lastCropRect.Height / 2f;
            float newCx = candidate.X + candidate.Width / 2f;
            float newCy = candidate.Y + candidate.Height / 2f;

            float centerShift = MathF.Sqrt((newCx - oldCx) * (newCx - oldCx) + (newCy - oldCy) * (newCy - oldCy));
            float sizeShift = Math.Abs(candidate.Width - _lastCropRect.Width);

            if (centerShift < centerThreshold && sizeShift < sizeThreshold)
            {
                VNotch.Services.RuntimeLog.Log("SMART-CROP",
                    $"stabilize: reuse previous crop (centerShift={centerShift:F1}<{centerThreshold:F1}, sizeShift={sizeShift:F1}<{sizeThreshold:F1})");
                return _lastCropRect;
            }
        }

        _lastCropRect = candidate;
        _lastCropImgWidth = imgWidth;
        _lastCropImgHeight = imgHeight;
        _hasLastCrop = true;
        return candidate;
    }

    private static Int32Rect BuildCropRect(float centerX, float centerY, int imgWidth, int imgHeight, int cropSize)
    {
        int cropX = (int)(centerX - cropSize / 2f);
        int cropY = (int)(centerY - cropSize / 2f);

        cropX = Math.Max(0, Math.Min(cropX, imgWidth - cropSize));
        cropY = Math.Max(0, Math.Min(cropY, imgHeight - cropSize));

        return new Int32Rect(cropX, cropY, cropSize, cropSize);
    }

    private Int32Rect? GetSaliencyCropRect(BitmapImage source, int imgWidth, int imgHeight, int targetSize)
    {
        try
        {
            int maxCropSize = Math.Min(imgWidth, imgHeight);
            int cropSize = Math.Min(targetSize, maxCropSize);

            const int analysisSize = 64;
            double scaleX = (double)analysisSize / imgWidth;
            double scaleY = (double)analysisSize / imgHeight;
            var scaled = new TransformedBitmap(source, new ScaleTransform(scaleX, scaleY));
            scaled.Freeze();

            int w = scaled.PixelWidth;
            int h = scaled.PixelHeight;
            if (w < 4 || h < 4) return null;

            int stride = w * 4;
            byte[] pixels = ArrayPool<byte>.Shared.Rent(h * stride);
            try
            {

                BitmapSource scaledSource = scaled;
                if (scaled.Format != PixelFormats.Bgra32)
                {
                    var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
                    converted.Freeze();
                    scaledSource = converted;
                }
                scaledSource.CopyPixels(pixels, stride, 0);

                const int gridSize = 8;
                int cellW = w / gridSize;
                int cellH = h / gridSize;
                float[,] saliencyMap = new float[gridSize, gridSize];

                for (int gy = 0; gy < gridSize; gy++)
                {
                    for (int gx = 0; gx < gridSize; gx++)
                    {
                        int startX = gx * cellW;
                        int startY = gy * cellH;
                        int endX = Math.Min(startX + cellW, w - 1);
                        int endY = Math.Min(startY + cellH, h - 1);

                        double totalContrast = 0;
                        double totalBrightness = 0;
                        int pixelCount = 0;

                        for (int y = startY; y < endY; y++)
                        {
                            for (int x = startX; x < endX; x++)
                            {
                                int i = y * stride + x * 4;
                                if (x + 1 >= w || y + 1 >= h) continue;

                                int iRight = i + 4;
                                int iBelow = (y + 1) * stride + x * 4;

                                double lum = 0.299 * pixels[i + 2] + 0.587 * pixels[i + 1] + 0.114 * pixels[i];
                                double lumR = 0.299 * pixels[iRight + 2] + 0.587 * pixels[iRight + 1] + 0.114 * pixels[iRight];
                                double lumB = 0.299 * pixels[iBelow + 2] + 0.587 * pixels[iBelow + 1] + 0.114 * pixels[iBelow];

                                totalContrast += Math.Abs(lum - lumR) + Math.Abs(lum - lumB);
                                totalBrightness += lum;
                                pixelCount++;
                            }
                        }

                        if (pixelCount == 0) continue;

                        double avgContrast = totalContrast / pixelCount;
                        double avgBrightness = totalBrightness / pixelCount;

                        double totalSat = 0;
                        for (int y = startY; y < endY; y++)
                        {
                            for (int x = startX; x < endX; x++)
                            {
                                int idx = y * stride + x * 4;
                                double r = pixels[idx + 2] / 255.0;
                                double g = pixels[idx + 1] / 255.0;
                                double b = pixels[idx] / 255.0;
                                double maxC = Math.Max(r, Math.Max(g, b));
                                double minC = Math.Min(r, Math.Min(g, b));
                                totalSat += maxC > 0 ? (maxC - minC) / maxC : 0;
                            }
                        }
                        double avgSat = totalSat / Math.Max(1, pixelCount);

                        double brightnessPenalty = (avgBrightness < 20 || avgBrightness > 240) ? 0.3 : 1.0;

                        double contrastFactor = avgContrast < 40 ? avgContrast / 40.0 : 1.0 - (avgContrast - 40) / 120.0;
                        contrastFactor = Math.Clamp(contrastFactor, 0.1, 1.0);

                        float cx = (gx + 0.5f) / gridSize;
                        float cy = (gy + 0.5f) / gridSize;
                        float centerDist = MathF.Sqrt((cx - 0.5f) * (cx - 0.5f) + (cy - 0.5f) * (cy - 0.5f));
                        float centerBias = MathF.Max(0.1f, 1.0f - centerDist * 1.6f);

                        saliencyMap[gy, gx] = (float)((avgSat * 60.0 + contrastFactor * 20.0) * brightnessPenalty * centerBias);
                    }
                }

                float bestScore = -1;
                int bestGx = gridSize / 2, bestGy = gridSize / 2;

                for (int gy = 0; gy < gridSize - 1; gy++)
                {
                    for (int gx = 0; gx < gridSize - 1; gx++)
                    {
                        float blockScore = saliencyMap[gy, gx] + saliencyMap[gy, gx + 1]
                                         + saliencyMap[gy + 1, gx] + saliencyMap[gy + 1, gx + 1];
                        if (blockScore > bestScore)
                        {
                            bestScore = blockScore;
                            bestGx = gx;
                            bestGy = gy;
                        }
                    }
                }

                float saliencyCenterX = (bestGx + 1.0f) / gridSize * imgWidth;
                float saliencyCenterY = (bestGy + 1.0f) / gridSize * imgHeight;

                return BuildCropRect(saliencyCenterX, saliencyCenterY, imgWidth, imgHeight, cropSize);

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
        return Path.Combine(appDir, "Models", "yolo11n.onnx");
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

    private struct Detection
    {
        public float X1, Y1, X2, Y2;
        public float Confidence;
        public int ClassId;
        public float Area => (X2 - X1) * (Y2 - Y1);
    }

    private struct TextRegion
    {
        public float X1, Y1, X2, Y2;
        public float EdgeDensity;
        public float FontFactor;
        public float Area => (X2 - X1) * (Y2 - Y1);
    }
}

public readonly record struct SubjectBounds(
    float CenterX,
    float CenterY,
    float Width,
    float Height,
    float Confidence,
    int ClassId);
