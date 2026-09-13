using System.Diagnostics;
using System.Runtime.InteropServices;
using VNotch.Controllers;

Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine("Sampled backdrop fingerprint; Release; 9 alternating rounds x 3000 frames; median and worst round (us/frame). No capture/WPF/GPU timing.");
Console.WriteLine("Region | Before median / worst | After median / worst | Speedup | Alloc B/frame");
ulong sink = 0;
foreach (var (width, height) in new[] { (515, 280), (960, 304), (1120, 1020), (1520, 1370) })
{
    byte[] data = new byte[width * height * 4];
    new Random(42).NextBytes(data);
    IntPtr pixels = Marshal.AllocHGlobal(data.Length);
    try
    {
        Marshal.Copy(data, 0, pixels, data.Length);
        ulong original = GlassFrameFingerprint.Compute(pixels, width, height);
        if (original != GlassFrameFingerprint.Compute(pixels, width, height))
            throw new Exception("Fingerprint is not deterministic.");
        Marshal.WriteByte(pixels, (byte)(data[0] ^ 255));
        if (original == GlassFrameFingerprint.Compute(pixels, width, height))
            throw new Exception("Sample change was missed.");
        Marshal.WriteByte(pixels, data[0]);
        if ((width & 1) != 0)
        {
            int tail = (width - 1) * 4;
            Marshal.WriteByte(pixels, tail, (byte)(data[tail] ^ 255));
            if (original == GlassFrameFingerprint.Compute(pixels, width, height))
                throw new Exception("Odd-width tail change was missed.");
            Marshal.WriteByte(pixels, tail, data[tail]);
        }
        Func<ulong> before = () => BaselineHash.ComputeSourceHash(pixels, width, height);
        Func<ulong> after = () => GlassFrameFingerprint.Compute(pixels, width, height);
        var warmup = Stopwatch.StartNew();
        while (warmup.ElapsedMilliseconds < 1500) { sink ^= before(); sink ^= after(); }
        double[] oldTimes = new double[9], newTimes = new double[9];
        long allocated = 0;
        for (int round = 0; round < 9; round++)
        {
            if ((round & 1) == 0) { oldTimes[round] = Measure(before); newTimes[round] = Measure(after); }
            else { newTimes[round] = Measure(after); oldTimes[round] = Measure(before); }
        }
        Array.Sort(oldTimes); Array.Sort(newTimes);
        Console.WriteLine($"{width}x{height} | {oldTimes[4]:F3} / {oldTimes[8]:F3} | {newTimes[4]:F3} / {newTimes[8]:F3} | {oldTimes[4] / newTimes[4]:F2}x | {allocated / 54000.0:F2}");

        double Measure(Func<ulong> operation)
        {
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < 3000; i++) sink ^= operation();
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMicroseconds / 3000;
            allocated += GC.GetAllocatedBytesForCurrentThread() - bytes;
            return elapsed;
        }
    }
    finally { Marshal.FreeHGlobal(pixels); }
}
Console.WriteLine($"Correctness checks passed; checksum={sink:X16}");
