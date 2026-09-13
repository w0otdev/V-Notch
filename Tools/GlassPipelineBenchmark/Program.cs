using System.Diagnostics;
using System.Runtime.InteropServices;
using VNotch.Controllers;

unsafe class Program
{
    static long sink;
    static void Main()
    {
        CheckPendingRows();
        Console.WriteLine($"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine("CPU upload + memory-copy transfer model. NOT actual D3D/GPU/FPS measurements. 7 alternating rounds x 200 frames; median us/frame.");
        Console.WriteLine("Size | motion | old us | new us | old transfer KiB | new transfer KiB | new alloc B/frame");
        foreach (var (w, h) in new[] { (515,280), (960,304), (1120,1020), (1520,1370) })
        foreach (var motion in new[] { "static", "8 rows", "full" }) Run(w, h, motion);
        Console.WriteLine($"Pixel/padding/coalescing checks passed; sink={sink}");
    }

    static void Run(int w, int h, string motion)
    {
        int rowBytes = w * 4, pitch = (rowBytes + 63) / 64 * 64, size = pitch * h;
        byte* src = (byte*)NativeMemory.AllocZeroed((nuint)size);
        byte* upload = (byte*)NativeMemory.AllocZeroed((nuint)size);
        byte* display = (byte*)NativeMemory.AllocZeroed((nuint)size);
        try
        {
            byte value = 0;
            long allocated = 0;
            double lastOldBytes = 0, lastNewBytes = 0;
            void Frame(bool optimized)
            {
                value ^= 255;
                if (motion == "full") new Span<byte>(src, size).Fill(value);
                else if (motion == "8 rows") new Span<byte>(src + pitch * (h / 2), pitch * 8).Fill(value);
                GlassDirtyRows range = optimized
                    ? GlassUploadDelta.CopyChangedRows((IntPtr)src, pitch, (IntPtr)upload, pitch, rowBytes, h)
                    : Baseline((IntPtr)src, pitch, (IntPtr)upload, pitch, rowBytes, h);
                if (range.IsEmpty) return;
                // Model the bytes submitted through UpdateSurface. Real D3D
                // transfer cost and WPF/DWM composition are deliberately excluded.
                int top = optimized ? range.Top : 0, bottom = optimized ? range.Bottom : h;
                for (int y = top; y < bottom; y++)
                    Buffer.MemoryCopy(upload + pitch * y, display + pitch * y, rowBytes, rowBytes);
                sink += bottom - top;
                if (optimized) lastNewBytes = (bottom - top) * rowBytes;
                else lastOldBytes = (bottom - top) * rowBytes;
            }
            var warmup = Stopwatch.StartNew();
            while (warmup.ElapsedMilliseconds < 500) { Frame(false); Frame(true); }
            double[] old = new double[7], current = new double[7];
            for (int i = 0; i < 7; i++)
            {
                if ((i & 1) == 0) { old[i] = Measure(false); current[i] = Measure(true); }
                else { current[i] = Measure(true); old[i] = Measure(false); }
            }
            Array.Sort(old); Array.Sort(current);
            Console.WriteLine($"{w}x{h} | {motion} | {old[3]:F2} | {current[3]:F2} | {lastOldBytes / 1024:F2} | {lastNewBytes / 1024:F2} | {allocated / 1400.0:F2}");
            double Measure(bool optimized)
            {
                long bytes = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                for (int n = 0; n < 200; n++) Frame(optimized);
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMicroseconds / 200;
                if (optimized) allocated += GC.GetAllocatedBytesForCurrentThread() - bytes;
                return elapsed;
            }
        }
        finally { NativeMemory.Free(src); NativeMemory.Free(upload); NativeMemory.Free(display); }
    }

    // Previous implementation: exact row comparisons/copies, but any change
    // subsequently causes a transfer of the entire captured rectangle.
    static GlassDirtyRows Baseline(IntPtr source, int sourceStride, IntPtr destination,
        int destinationStride, int rowBytes, int height)
    {
        bool changed = false;
        for (int y = 0; y < height; y++)
        {
            var src = new ReadOnlySpan<byte>((byte*)source + (long)y * sourceStride, rowBytes);
            var dst = new Span<byte>((byte*)destination + (long)y * destinationStride, rowBytes);
            if (src.SequenceEqual(dst)) continue;
            src.CopyTo(dst);
            changed = true;
        }
        return changed ? new(0, height) : default;
    }

    static void CheckPendingRows()
    {
        const int width = 7, height = 19, rowBytes = width * 4, pitch = 64;
        byte* src = stackalloc byte[pitch * height];
        byte* dst = stackalloc byte[pitch * height];
        new Span<byte>(src, pitch * height).Clear();
        new Span<byte>(dst, pitch * height).Fill(99);
        for (int y = 0; y < height; y++) new Span<byte>(dst + y * pitch, rowBytes).Clear();
        GlassDirtyRows Copy() => GlassUploadDelta.CopyChangedRows((IntPtr)src, pitch, (IntPtr)dst, pitch, rowBytes, height);
        if (!Copy().IsEmpty) throw new Exception("Padding must not mark a frame dirty");
        src[2 * pitch + rowBytes - 1] = 42;
        var first = Copy();
        src[15 * pitch] = 7;
        var pending = first.Union(Copy());
        src[2 * pitch + rowBytes - 1] = 0; // Revert before the UI presents.
        pending = pending.Union(Copy());
        if (first != new GlassDirtyRows(2, 3) || pending != new GlassDirtyRows(2, 16))
            throw new Exception("Pending damage must accumulate, including reverted pixels");
        for (int y = 0; y < height; y++)
        {
            if (!new ReadOnlySpan<byte>(src + y * pitch, rowBytes).SequenceEqual(new Span<byte>(dst + y * pitch, rowBytes)))
                throw new Exception("Uploaded pixels differ");
            if (dst[y * pitch + rowBytes] != 99) throw new Exception("Padding overwritten");
        }
        if (!Copy().IsEmpty) throw new Exception("Stable image must be suppressed");
    }
}
