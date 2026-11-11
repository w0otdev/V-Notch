using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;

namespace NativePipelineBench;

internal static unsafe partial class Program
{
    private static readonly List<Measurement> Results = new();
    private static readonly List<object> Details = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string _work = "", _repo = "", _runtime = "";
    private static string _resultFile = "results.json";
    private static NativeApi _baseline = null!, _current = null!;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint AddDllDirectory(string path);
    [DllImport("kernel32.dll")] private static extern bool RemoveDllDirectory(nint cookie);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 4 || (args[0] == "cold" && args.Length < 6) || (args[0] == "hotpaths" && args.Length < 5)) { Console.Error.WriteLine("run|cold|verify|inference512|hotpaths repo output runtime [backend cold-output|previous-dll]"); return 2; }
        _repo = Path.GetFullPath(args[1]); _work = Path.GetFullPath(args[2]); _runtime = Path.GetFullPath(args[3]);
        nint cookie = AddDllDirectory(_runtime);
        NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, (name, _, _) =>
            name.StartsWith("onnxruntime", StringComparison.OrdinalIgnoreCase) ? NativeLibrary.Load(Path.Combine(_runtime, "onnxruntime.dll")) : 0);
        try
        {
            using var baseline = new NativeApi(Path.Combine(_work, "native", "baseline.dll"));
            using var current = new NativeApi(Path.Combine(_work, "native", "current.dll"));
            _baseline = baseline; _current = current;
            if (args[0] == "hotpaths") {
                _resultFile = args.Length > 5 && args[5] == "repeat" ? "hotpath-repeat-results.json" : "hotpath-results.json";
                HotPaths(args[4]); Save(); return 0;
            }
            if (args[0] == "cold") { Cold(args[4], args[5]); return 0; }
            if (args[0] == "verify") {
                _resultFile = "verification-results.json";
                Kernels(); Scrubbers(); DecodeNms(); Storage(); Save(); return 0;
            }
            if (args[0] == "inference512") {
                _resultFile = "inference512-results.json";
                Inference(decodedOnly: true); Save(); return 0;
            }
            if (args[0] != "run") throw new ArgumentException($"Unknown benchmark mode: {args[0]}");
            Kernels(); Scrubbers(); DecodeNms(); Audio(); Storage(); Inference();
            Save();
            Console.WriteLine($"FINISHED {Results.Count} measurements -> {Path.Combine(_work, "results.json")}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (cookie != 0) RemoveDllDirectory(cookie); }
    }
    private static void Add(params Measurement[] values)
    {
        Results.AddRange(values);
        foreach (var value in values) Console.WriteLine($"{value.Area} | {value.Workload} | {value.Variant} | median {value.MedianUs:F3} us | p95 {value.P95Us:F3} us | GC {value.ManagedBytesPerOperation:F1} B/op");
        Save();
    }
    private static void Save()
    {
        var environment = JsonDocument.Parse(File.ReadAllText(Path.Combine(_work, "environment.json"))).RootElement.Clone();
        File.WriteAllText(Path.Combine(_work, _resultFile), JsonSerializer.Serialize(new {
            Timestamp = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).ToString("O"), Environment = environment,
            Method = "Release .NET8; tiered JIT disabled; interleaved rotated variant order; preallocated native buffers; microkernel batches target 8 ms, 41 samples; inference uses 3 rotated rounds x 21 samples with independent sessions; disk writes include Flush(true) or native durability barriers.",
            NativeAvx2 = System.Runtime.Intrinsics.X86.Avx2.IsSupported, Measurements = Results, Details
        }, Json));
    }

    private static void DecodeNms()
    {
        foreach (int candidates in new[] { 32, 300 })
        {
            float[] predictions = new float[3549 * 85];
            for (int i = 0; i < candidates; ++i) {
                int row = ((i * 43) % 1508) * 85;
                predictions[row] = .5f; predictions[row + 1] = .5f;
                predictions[row + 2] = MathF.Log(8); predictions[row + 3] = MathF.Log(12);
                predictions[row + 4] = .9f; predictions[row + 5 + i % 80] = .85f;
            }
            Detection[] a = new Detection[3549], b = new Detection[3549]; int countA = 0, countB = 0, rawA = 0, rawB = 0;
            Action old = () => { int count, raw; fixed (float* p = predictions) fixed (Detection* d = a) NativeApi.Check(_baseline.Parse(p, 1280, 720, .325f, d, 3549, &raw, &count)); countA = count; rawA = raw; };
            Action current = () => { int count, raw; fixed (float* p = predictions) fixed (Detection* d = b) NativeApi.Check(_current.Parse(p, 1280, 720, .325f, d, 3549, &raw, &count)); countB = count; rawB = raw; };
            old(); current();
            if (countA == 0 || countA != countB || rawA != rawB || !MemoryMarshal.AsBytes(a.AsSpan(0, countA)).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan(0, countB)))) throw new InvalidOperationException("YOLOX decode/NMS mismatch");
            Details.Add(new { Kind = "DecodeValidation", Candidates = candidates, RawDetections = rawA, NmsDetections = countA, Identical = true });
            Add(Statistics.Compare("YOLOX decode + NMS", $"3549x85 output, {candidates} candidates", [("HEAD scalar class reduction", old), ("AVX2 class reduction", current)]));
        }
    }

    private static void Kernels()
    {
        foreach (int length in new[] { 512, 2048, 4096 })
        {
            float[] input = new float[length]; new Random(42).NextBytes(MemoryMarshal.AsBytes(input.AsSpan()));
            for (int i = 0; i < length; ++i) input[i] = .4f * MathF.Sin(i * .119f) + .2f * MathF.Cos(i * .31f);
            Complex[] oldOutput = new Complex[length], newOutput = new Complex[length];
            Action old = () => { fixed (float* p = input) fixed (Complex* o = oldOutput) NativeApi.Check(_baseline.Fft(p, length, o)); };
            Action current = () => { fixed (float* p = input) fixed (Complex* o = newOutput) NativeApi.Check(_current.Fft(p, length, o)); };
            old(); current();
            double error = oldOutput.Zip(newOutput).Max(v => Math.Max(Math.Abs(v.First.X - v.Second.X), Math.Abs(v.First.Y - v.Second.Y)));
            if (error > 1e-6) throw new InvalidOperationException($"FFT error {error}");
            Details.Add(new { Kind = "FFTValidation", Length = length, MaxAbsoluteError = error });
            Add(Statistics.Compare("FFT", $"{length} samples + Hamming", [("HEAD radix-2/AVX2", old), ("current radix-4/FMA", current)]));
        }
        foreach (int side in new[] { 64, 256 })
        {
            byte[] pixels = new byte[side * side * 4]; new Random(19).NextBytes(pixels);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            byte[] oldPalette = new byte[18], newPalette = new byte[18];
            Action old = () => { fixed (byte* p = pixels, o = oldPalette) NativeApi.Check(_baseline.Palette(p, side, side, side * 4, o)); };
            Action current = () => { fixed (byte* p = pixels, o = newPalette) NativeApi.Check(_current.Palette(p, side, side, side * 4, o)); };
            Add(Statistics.Compare("Palette", $"{side}x{side} opaque seeded pixels", [("HEAD HSV palette", old), ("current Oklab palette", current)]));
            Details.Add(new { Kind = "PaletteValidation", Side = side, PaletteOutputsDiffer = !oldPalette.SequenceEqual(newPalette) });
        }
        var art = Artwork.Load(Path.Combine(_repo, "Tests", "Fixtures", "white-mat-artwork.png"));
        float[] oldTensor = new float[3 * 416 * 416], newTensor = new float[oldTensor.Length];
        byte[] source = new byte[art.Width * art.Height * 4];
        Action managed = () => { ManagedInference.Preprocess(_baseline, art, oldTensor); };
        Action native = () => {
            art.Bitmap.CopyPixels(source, art.Width * 4, 0); float scale;
            fixed (byte* p = source) fixed (float* o = newTensor)
                NativeApi.Check(_current.Resize(p, source.Length, art.Width, art.Height, art.Width * 4, o, newTensor.Length, &scale));
        };
        Add(Statistics.Compare("Preprocess", $"{art.Name} {art.Width}x{art.Height}", [("HEAD WPF resize + scalar planar", managed), ("native bilinear/AVX2 + bitmap copy", native)]));
    }

    private static void Scrubbers()
    {
        var inputs = new[] {
            ("ordinary log 1 KiB", string.Concat(Enumerable.Repeat("INFO request=42 completed status=200 elapsed=7ms; ", 22))),
            ("credential fields ~64 KiB", string.Concat(Enumerable.Repeat("account_access_token=example-secret-value; client_secret=other-secret-value; safe=42\n", 800))),
            ("sparse 20 MiB ASCII / 40 MiB UTF16", "password='secret with spaces'\n" + new string('x', 20 * 1024 * 1024) + "\nAuthorization: Basic YWRtaW46MTIz\n"),
            ("dense ~20 MiB ASCII / 40 MiB UTF16", string.Concat(Enumerable.Repeat("api_key=private-key; ", 1_048_576)))
        };
        foreach (var (name, input) in inputs)
        {
            char[] before = new char[input.Length + 128], after = new char[before.Length];
            int oldLength = 0, newLength = 0; byte oldChanged = 0, newChanged = 0;
            Action old = () => { int n; byte c; fixed (char* p = input, o = before) NativeApi.Check(_baseline.Scrub(p, input.Length, o, before.Length, &n, &c)); oldLength = n; oldChanged = c; };
            Action current = () => { int n; byte c; fixed (char* p = input, o = after) NativeApi.Check(_current.Scrub(p, input.Length, o, after.Length, &n, &c)); newLength = n; newChanged = c; };
            old(); current();
            if (oldChanged != newChanged || oldLength != newLength || !before.AsSpan(0, oldLength).SequenceEqual(after.AsSpan(0, newLength)))
                throw new InvalidOperationException("Scrubber output mismatch");
            Add(Statistics.Compare("Scrubber native", name, [("HEAD linear keyword lists", old), ("consteval field tries", current)],
                samples: input.Length > 1_000_000 ? 15 : 41, fixedBatch: input.Length > 1_000_000 ? 1 : null, warmup: input.Length > 1_000_000 ? 2 : 16));
            Details.Add(new { Kind = "ScrubberValidation", Name = name, InputCharacters = input.Length, OutputCharacters = oldLength, Identical = true });
        }
    }

    private static void Audio()
    {
        float[] pcmFloats = new float[512 * 2];
        for (int i = 0; i < 512; ++i) pcmFloats[2*i] = pcmFloats[2*i+1] = .3f * MathF.Sin(2 * MathF.PI * 16 * i / 512) + .1f * MathF.Sin(2 * MathF.PI * 70 * i / 512);
        byte[] pcm = MemoryMarshal.AsBytes(pcmFloats.AsSpan()).ToArray();
        var legacy = new LegacyAudio(_baseline);
        var currentDsp = new LegacyAudio(_current);
        Add(Statistics.Compare("Audio DSP", "512 stereo float32 frames / 11.61 ms audio", [("HEAD DSP kernels", () => legacy.Run(pcm)), ("current DSP kernels", () => currentDsp.Run(pcm))]));
    }

    private sealed record Document(string Id, string Title, string Text, int Kind, bool IsPinned, string CopiedUtc);
    private static void Storage()
    {
        string root = Path.Combine(_work, "scratch-records"); Directory.CreateDirectory(root);
        bool keyExisted = File.Exists(Path.Combine(root, ".history-key.dpapi"));
        long created; fixed (char* r = root) NativeApi.Check(_current.OpenRecords(r, root.Length, &created)); long handle = created;
        try
        {
            foreach (int size in new[] { 1024, 65536 })
            {
                var document = new Document("benchmark-entry", "Synthetic document", new string('v', size), 0, false, "2026-11-02T12:00:00+07:00");
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(document);
                string plainPath = Path.Combine(root, $"plain-{size}.json"), encryptedPath = Path.Combine(root, $"encrypted-{size}.json");
                string temp = plainPath + ".tmp";
                Action writePlain = () => {
                    using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(stream, document); stream.Flush(true); }
                    File.Move(temp, plainPath, overwrite: true);
                };
                Action writeEncrypted = () => {
                    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document);
                    try { fixed (char* p = encryptedPath) fixed (byte* b = bytes) NativeApi.Check(_current.Append(handle, p, encryptedPath.Length, b, bytes.Length)); }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                };
                long start = Stopwatch.GetTimestamp(); writeEncrypted();
                Details.Add(new { Kind = "FirstEncryptedWrite", PayloadBytes = payload.Length, Milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds, IncludesDpapiKeyCreation = size == 1024 && !keyExisted });
                writePlain();
                Add(Statistics.Compare("Clipboard durable write", $"{payload.Length} byte JSON document", [("HEAD JSON serialize + Flush + replace", writePlain), ("JSON serialize + native AES-GCM append", writeEncrypted)], 81, 1, 4));
                Document? readBack = null;
                Action readPlain = () => { using var stream = File.OpenRead(plainPath); readBack = JsonSerializer.Deserialize<Document>(stream); };
                Action readEncrypted = () => { readBack = ReadJson(_current, handle, encryptedPath); };
                readEncrypted(); if (readBack != document) throw new InvalidOperationException("Encrypted storage round-trip mismatch");
                readPlain(); if (readBack != document) throw new InvalidOperationException("Plain storage round-trip mismatch");
                Add(Statistics.Compare("Clipboard read + deserialize", $"{payload.Length} byte JSON document", [("HEAD JSON FileStream", readPlain), ("native authenticated record + stack/pool JSON", readEncrypted)], 41));
                Details.Add(new { Kind = "StorageFootprint", PayloadBytes = payload.Length, PlainFileBytes = new FileInfo(plainPath).Length, EncryptedLogBytes = new FileInfo(encryptedPath).Length });
            }
        }
        finally { _current.CloseRecords(handle); }
    }

    private static IInferenceCase Create(string backend, Artwork art) => backend switch {
        "managed-cpu" => new ManagedInference(_baseline, art, Path.Combine(_repo, "Models", "yolox_nano.onnx")),
        "native-cpu" => new NativeInference(_current, art, Path.Combine(_repo, "Models", "yolox_nano.onnx"), _runtime, false),
        "directml" => new NativeInference(_current, art, Path.Combine(_repo, "Models", "yolox_nano.onnx"), _runtime, true),
        _ => throw new ArgumentException(backend)
    };
    private static void Inference(bool decodedOnly = false)
    {
        string[] backends = ["managed-cpu", "native-cpu", "directml"];
        int[] decodeWidths = decodedOnly ? [512] : [0, 512];
        foreach (int decodeWidth in decodeWidths)
        foreach (string fixture in new[] { "white-mat-artwork.png", "dark-textured-artwork.png" })
        {
            var art = Artwork.Load(Path.Combine(_repo, "Tests", "Fixtures", fixture), decodeWidth);
            var rounds = backends.ToDictionary(name => name, _ => new List<Measurement>());
            for (int round = 0; round < 3; ++round)
            for (int order = 0; order < backends.Length; ++order)
            {
                string backend = backends[(round + order) % backends.Length];
                long started = Stopwatch.GetTimestamp();
                using var inference = Create(backend, art);
                double createMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var value = Statistics.Compare("YOLOX complete inference", $"{art.Name} {art.Width}x{art.Height}", [(backend, inference.Run)], 21, 1, 12)[0];
                rounds[backend].Add(value);
                Details.Add(new { Kind = "InferenceRound", Fixture = art.Name, Round = round, Backend = backend, inference.ActualBackend, SessionCreationMs = createMs, Detections = inference.Count });
                Console.WriteLine($"YOLOX round {round+1}/3 | {art.Name} | {backend} | {value.MedianUs/1000:F3} ms, {value.ManagedBytesPerOperation:F0} B/op");
            }
            foreach (string backend in backends)
            {
                var data = rounds[backend]; double[] raw = data.SelectMany(v => v.RawUs).ToArray();
                Add(Statistics.Summarize("YOLOX complete inference", $"{art.Name} {art.Width}x{art.Height}", backend, 1, raw,
                    (long)Math.Round(data.Sum(v => v.ManagedBytesPerOperation * v.Samples)),
                    [data.Sum(v => v.Gen0Collections), data.Sum(v => v.Gen1Collections), data.Sum(v => v.Gen2Collections)]));
            }
        }
    }
    private static void Cold(string backend, string output)
    {
        var art = Artwork.Load(Path.Combine(_repo, "Tests", "Fixtures", "white-mat-artwork.png"));
        var process = Process.GetCurrentProcess(); process.Refresh();
        long privateBefore = process.PrivateMemorySize64, workingBefore = process.WorkingSet64;
        long started = Stopwatch.GetTimestamp();
        using var inference = Create(backend, art);
        double createMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = Stopwatch.GetTimestamp(); inference.Run();
        double firstRunMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        process.Refresh();
        File.WriteAllText(output, JsonSerializer.Serialize(new {
            Backend = backend, inference.ActualBackend, CreationMs = createMs, FirstInferenceMs = firstRunMs,
            PrivateBytesBefore = privateBefore, PrivateBytesAfterFirstRun = process.PrivateMemorySize64,
            WorkingSetBefore = workingBefore, WorkingSetAfterFirstRun = process.WorkingSet64, inference.Count,
            Note = "Fresh process; artwork decode and benchmark DLL load excluded; runtime initialization and model creation included in CreationMs. Windows file caches are not flushed."
        }, Json));
        Console.WriteLine($"cold {backend}: create {createMs:F2} ms, first inference {firstRunMs:F2} ms");
    }
}
