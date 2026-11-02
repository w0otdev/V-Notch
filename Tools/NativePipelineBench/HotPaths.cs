using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace NativePipelineBench;

internal static unsafe partial class Program
{
    private static void HotPaths(string previousDll)
    {
        using var previous = new NativeApi(previousDll);
        Details.Add(new { Kind = "PreviousNative", Path = Path.GetFullPath(previousDll),
            Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(previousDll))) });
        int checkedPalettes = 0;
        foreach (int width in new[] { 5, 7, 8, 13, 36, 64, 127, 256 })
        for (int seed = 0; seed < 20; ++seed)
        {
            int height = Math.Max(5, width - 3), stride = width * 4 + 12;
            byte[] pixels = new byte[stride * height]; new Random(seed).NextBytes(pixels);
            ComparePalette(previous, pixels, width, height, stride, $"{width}x{height}, seed {seed}");
            ++checkedPalettes;
        }
        foreach (byte[] color in new[] { new byte[]{0,0,255,255}, new byte[]{0,255,0,255}, new byte[]{255,0,0,255},
                                        new byte[]{128,128,128,255}, new byte[]{0,0,0,255}, new byte[]{255,255,255,255}, new byte[]{20,140,140,79} })
        {
            byte[] pixels = new byte[64 * 64 * 4];
            for (int i = 0; i < pixels.Length; i += 4) color.CopyTo(pixels, i);
            ComparePalette(previous, pixels, 64, 64, 256, $"solid {Convert.ToHexString(color)}");
            ++checkedPalettes;
        }
        Details.Add(new { Kind = "PaletteValidation", Palettes = checkedPalettes, Identical = true,
            Scope = "Previous Oklab versus optimized; random alpha, scalar tails, padded rows, solids and gray. HSV intentionally differs." });
        foreach (int side in new[] { 36, 64, 256 })
        {
            byte[] pixels = new byte[side * side * 4]; new Random(19).NextBytes(pixels);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            byte[] palette = new byte[18];
            Action Run(NativeApi api) => () => { fixed (byte* p = pixels, o = palette) NativeApi.Check(api.Palette(p, side, side, side * 4, o)); };
            Add(Statistics.Compare("Palette", $"{side}x{side} opaque seeded pixels",
                [("HEAD HSV", Run(_baseline)), ("previous Oklab", Run(previous)), ("optimized Oklab", Run(_current))], 61));
        }
        HotStorage(previous);
    }

    private static void ComparePalette(NativeApi previous, byte[] pixels, int width, int height, int stride, string label)
    {
        byte[] before = new byte[18], after = new byte[18];
        fixed (byte* p = pixels, b = before, a = after) {
            NativeApi.Check(previous.Palette(p, width, height, stride, b));
            NativeApi.Check(_current.Palette(p, width, height, stride, a));
        }
        if (!before.SequenceEqual(after)) throw new InvalidOperationException($"Palette changed: {label}; {Convert.ToHexString(before)} -> {Convert.ToHexString(after)}");
    }

    private static void HotStorage(NativeApi previous)
    {
        string root = Path.Combine(_work, "hotpath-scratch"); Directory.CreateDirectory(root);
        long createdOld, createdNew;
        fixed (char* r = root) {
            NativeApi.Check(previous.OpenRecords(r, root.Length, &createdOld));
            NativeApi.Check(_current.OpenRecords(r, root.Length, &createdNew));
        }
        long oldHandle = createdOld, newHandle = createdNew;
        try
        {
            foreach (int size in new[] { 1024, 65536, 300000 })
            {
                var expected = new Document("benchmark-entry", "Synthetic document", new string('v', size), 0, false, "2026-11-02T12:00:00+07:00");
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(expected);
                string path = Path.Combine(root, $"encrypted-{size}.json"), plainPath = Path.Combine(root, $"plain-{size}.json");
                File.WriteAllBytes(plainPath, json);
                fixed (char* p = path) fixed (byte* b = json) NativeApi.Check(_current.Append(newHandle, p, path.Length, b, json.Length));
                Document? actual = null;
                Action legacy = () => { using var stream = File.OpenRead(plainPath); actual = JsonSerializer.Deserialize<Document>(stream); };
                Action old = () => {
                    int length; fixed (char* p = path) { if (previous.Read(oldHandle, p, path.Length, null, 0, &length) != -2) throw new IOException("Size query failed"); }
                    byte[] buffer = new byte[length];
                    try { fixed (char* p = path) fixed (byte* b = buffer) NativeApi.Check(previous.Read(oldHandle, p, path.Length, b, length, &length)); actual = JsonSerializer.Deserialize<Document>(buffer); }
                    finally { CryptographicOperations.ZeroMemory(buffer); }
                };
                Action oneCall = () => { actual = ReadJson(previous, oldHandle, path); };
                Action optimized = () => { actual = ReadJson(_current, newHandle, path); };
                foreach (var read in new[] { legacy, old, oneCall, optimized }) {
                    read(); if (actual != expected) throw new InvalidOperationException("Clipboard round-trip changed");
                }
                Details.Add(new { Kind = "StorageValidation", PayloadBytes = json.Length, Equal = true });
                Add(Statistics.Compare("Clipboard read + deserialize", $"{json.Length} byte JSON document",
                    [("HEAD plaintext FileStream", legacy), ("previous encrypted two-call + array", old),
                     ("previous native + stack/pool JSON", oneCall), ("optimized native + stack/pool JSON", optimized)], 61));
            }
        }
        finally { previous.CloseRecords(oldHandle); _current.CloseRecords(newHandle); }
    }

    // Mirrors NativeClipboardRecordStore.TryReadJson, including wiping and pool fallback.
    private static Document? ReadJson(NativeApi api, long handle, string path)
    {
        Span<byte> buffer = stackalloc byte[4096]; byte[]? rented = null;
        try
        {
            for (int attempt = 0; attempt < 3; ++attempt)
            {
                int length, status;
                fixed (char* p = path) fixed (byte* b = buffer) status = api.Read(handle, p, path.Length, b, buffer.Length, &length);
                if (status == 0) return JsonSerializer.Deserialize<Document>(buffer[..length]);
                if (status != -2 || length <= buffer.Length || length > 256 * 1024 * 1024) throw new IOException($"Read failed: {status}");
                CryptographicOperations.ZeroMemory(buffer);
                if (rented != null) ArrayPool<byte>.Shared.Return(rented);
                rented = null; buffer = [];
                rented = ArrayPool<byte>.Shared.Rent(length); buffer = rented;
            }
            throw new IOException("Changing document size");
        }
        finally { CryptographicOperations.ZeroMemory(buffer); if (rented != null) ArrayPool<byte>.Shared.Return(rented); }
    }
}
