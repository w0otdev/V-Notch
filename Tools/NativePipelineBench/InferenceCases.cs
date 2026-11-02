using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace NativePipelineBench;

internal sealed record Artwork(string Name, BitmapSource Bitmap, int Width, int Height)
{
    internal static Artwork Load(string path, int decodeWidth = 0)
    {
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
        image.UriSource = new Uri(Path.GetFullPath(path)); image.EndInit(); image.Freeze();
        BitmapSource bitmap = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        bitmap.Freeze(); return new(Path.GetFileName(path) + (decodeWidth > 0 ? $"@decoded{decodeWidth}" : ""), bitmap, bitmap.PixelWidth, bitmap.PixelHeight);
    }
}

internal interface IInferenceCase : IDisposable
{
    void Run();
    int Count { get; }
    string ActualBackend { get; }
}

internal sealed unsafe class NativeInference : IInferenceCase
{
    private readonly NativeApi _api;
    private readonly Artwork _art;
    private readonly byte[] _pixels;
    private readonly Detection[] _detections = new Detection[3549];
    private long _handle;
    public int Count { get; private set; }
    public string ActualBackend { get; private set; } = "";
    internal NativeInference(NativeApi api, Artwork art, string model, string runtime, bool directMl)
    {
        _api = api; _art = art; _pixels = new byte[art.Width * art.Height * 4];
        long handle; byte backend;
        fixed (char* m = model, r = runtime) NativeApi.Check(api.CreateCrop(m, model.Length, r, runtime.Length, directMl ? (byte)1 : (byte)0, &handle, &backend));
        _handle = handle; ActualBackend = backend != 0 ? "DirectML" : "CPU";
        if (directMl && backend == 0) { Dispose(); throw new InvalidOperationException("DirectML unavailable; refusing to label CPU fallback as a GPU benchmark."); }
    }
    public void Run()
    {
        _art.Bitmap.CopyPixels(_pixels, _art.Width * 4, 0);
        int count; byte backend;
        fixed (byte* p = _pixels) fixed (Detection* d = _detections)
            NativeApi.Check(_api.Infer(_handle, p, _pixels.Length, _art.Width, _art.Height, _art.Width * 4, d, 3549, &count, &backend));
        Count = count; ActualBackend = backend != 0 ? "DirectML" : "CPU";
    }
    public void Dispose() { if (_handle != 0) _api.DestroyCrop(_handle); _handle = 0; }
}

internal sealed unsafe class ManagedInference : IInferenceCase
{
    private readonly NativeApi _baseline;
    private readonly Artwork _art;
    private readonly InferenceSession _session;
    private readonly Detection[] _detections = new Detection[3549];
    public int Count { get; private set; }
    public string ActualBackend => "CPU";
    internal ManagedInference(NativeApi baseline, Artwork art, string model)
    {
        _baseline = baseline; _art = art;
        using var options = new SessionOptions {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            InterOpNumThreads = 1, IntraOpNumThreads = 2, EnableMemoryPattern = false, EnableCpuMemArena = false
        };
        _session = new InferenceSession(model, options);
    }
    internal static float Preprocess(NativeApi baseline, Artwork art, float[] tensor)
    {
        float scale = Math.Min(416f / art.Width, 416f / art.Height);
        int width = Math.Max(1, (int)(art.Width * scale)), height = Math.Max(1, (int)(art.Height * scale));
        var scaled = new TransformedBitmap(art.Bitmap, new ScaleTransform((double)width / art.Width, (double)height / art.Height));
        scaled.Freeze();
        int stride = scaled.PixelWidth * 4;
        byte[] pixels = ArrayPool<byte>.Shared.Rent(scaled.PixelHeight * stride);
        try
        {
            scaled.CopyPixels(pixels, stride, 0);
            fixed (byte* p = pixels) fixed (float* t = tensor) NativeApi.Check(baseline.Fill(p, scaled.PixelWidth, scaled.PixelHeight, stride, t));
        }
        finally { ArrayPool<byte>.Shared.Return(pixels); }
        return scale;
    }
    public void Run()
    {
        float[] buffer = ArrayPool<float>.Shared.Rent(3 * 416 * 416);
        try
        {
            float scale = Preprocess(_baseline, _art, buffer);
            var tensor = new DenseTensor<float>(new Memory<float>(buffer, 0, 3 * 416 * 416), new[] { 1, 3, 416, 416 });
            var inputs = new List<NamedOnnxValue>(1) { NamedOnnxValue.CreateFromTensor(_session.InputNames[0], tensor) };
            using var results = _session.Run(inputs);
            var output = (DenseTensor<float>)results[0].AsTensor<float>();
            int raw, count;
            fixed (float* p = output.Buffer.Span) fixed (Detection* d = _detections)
                NativeApi.Check(_baseline.Parse(p, _art.Width, _art.Height, scale, d, 3549, &raw, &count));
            Count = count;
        }
        finally { ArrayPool<float>.Shared.Return(buffer); }
    }
    public void Dispose() => _session.Dispose();
}

// Mirrors the former managed PCM coordination, using its original native kernels.
internal sealed unsafe class LegacyAudio
{
    private readonly NativeApi _api;
    private readonly byte[] _pcmCopy = new byte[512 * 2 * 4];
    private readonly float[] _mono = new float[512], _samples = new float[512];
    private readonly Complex[] _fft = new Complex[512];
    private readonly double[] _powers = new double[256], _energies = new double[11];
    private readonly int[] _starts = new int[11], _ends = new int[11];
    private readonly float[] _levels = new float[5];
    private DspState _state;
    private int _lastBin, _position, _rmsCount;
    private double _rmsSum;
    private float _rms;
    internal LegacyAudio(NativeApi api)
    {
        _api = api;
        fixed (DspState* state = &_state) NativeApi.Check(api.Reset(state));
        int last;
        fixed (int* a = _starts, b = _ends) NativeApi.Check(api.Ranges(44100, 512, 256, a, b, &last));
        _lastBin = last;
    }
    internal void Run(byte[] pcm)
    {
        pcm.CopyTo(_pcmCopy, 0);
        int frames;
        fixed (byte* p = _pcmCopy) fixed (float* m = _mono)
            NativeApi.Check(_api.Mix(p, pcm.Length, pcm.Length, 2, 32, 1, m, 512, &frames));
        for (int i = 0; i < frames; ++i)
        {
            float sample = _mono[i]; _samples[_position++] = sample;
            _rmsSum += sample * sample;
            if (++_rmsCount >= 882)
            {
                float rms; NativeApi.Check(_api.Normalize(Math.Sqrt(_rmsSum / _rmsCount), &rms));
                _rms = rms; _rmsSum = 0; _rmsCount = 0;
            }
            if (_position == 512)
            {
                _position = 0;
                fixed (float* input = _samples, levels = _levels) fixed (Complex* fft = _fft)
                fixed (int* starts = _starts, ends = _ends) fixed (double* powers = _powers, energies = _energies)
                fixed (DspState* state = &_state)
                {
                    NativeApi.Check(_api.Fft(input, 512, fft));
                    NativeApi.Check(_api.Bands(fft, 512, starts, ends, _lastBin, 4d / (512 * 512), powers, energies));
                    NativeApi.Check(_api.Targets(energies, 11, _rms, state, levels));
                }
            }
        }
    }
}
