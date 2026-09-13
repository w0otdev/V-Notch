using System.Diagnostics;
using System.Runtime.InteropServices;
using VNotch.Controllers;

Console.WriteLine($"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine("Detection microbenchmark, not desktop-to-photon latency. 7 alternating rounds x 500 frames after 1s warmup.");
Console.WriteLine("Size | scenario | before us | after us | before misses / 240 | after misses / 240 | retained MiB | after alloc B/frame");
int sink = 0;
foreach (var (w, h) in new[] { (515, 280), (960, 304), (1120, 1020), (1520, 1370) })
{
    byte[] initial = new byte[w * h * 4];
    new Random(42).NextBytes(initial);
    IntPtr pixels = Marshal.AllocHGlobal(initial.Length);
    try
    {
        foreach (string scenario in new[] { "static", "small motion", "late-frame motion" })
        {
            Marshal.Copy(initial, 0, pixels, initial.Length);
            // Row 1 is between sampled rows for these sizes; the second case
            // puts the change near the end to exercise full comparison cost.
            int offset = scenario == "late-frame motion" ? (h - 2) * w * 4 : w * 4;
            bool motion = scenario != "static";
            var history = new GlassFrameHistory();
            history.Commit(pixels, w, h);
            ulong previous = GlassFrameFingerprint.Compute(pixels, w, h);
            byte value = Marshal.ReadByte(pixels, offset);
            int oldMisses = 0, newMisses = 0;
            for (int i = 0; i < 240; i++)
            {
                if (motion) Marshal.WriteByte(pixels, offset, value ^= 255);
                ulong next = GlassFrameFingerprint.Compute(pixels, w, h);
                bool oldSame = next == previous;
                bool newSame = history.IsUnchanged(pixels, w, h);
                if (motion && oldSame) oldMisses++;
                if (motion && newSame) newMisses++;
                if (!motion && (!oldSame || !newSame)) throw new Exception("Static frame mismatch");
                previous = next;
                if (!newSame) history.Commit(pixels, w, h);
            }
            if (newMisses != 0) throw new Exception("Exact detector missed a changed frame");
            Action old = () =>
            {
                if (motion) Marshal.WriteByte(pixels, offset, value ^= 255);
                ulong next = GlassFrameFingerprint.Compute(pixels, w, h);
                if (next != previous) sink++;
                previous = next;
            };
            Action current = () =>
            {
                if (motion) Marshal.WriteByte(pixels, offset, value ^= 255);
                if (!history.IsUnchanged(pixels, w, h)) { history.Commit(pixels, w, h); sink++; }
            };
            var warmup = Stopwatch.StartNew();
            while (warmup.ElapsedMilliseconds < 1000) { old(); current(); }
            double[] before = new double[7], after = new double[7];
            long allocated = 0;
            for (int round = 0; round < 7; round++)
            {
                if ((round & 1) == 0) { before[round] = Measure(old); after[round] = Measure(current); }
                else { after[round] = Measure(current); before[round] = Measure(old); }
            }
            Array.Sort(before); Array.Sort(after);
            Console.WriteLine($"{w}x{h} | {scenario} | {before[3]:F3} | {after[3]:F3} | {oldMisses} | {newMisses} | {initial.Length / 1048576.0:F2} | {allocated / 3500.0:F2}");
            double Measure(Action operation)
            {
                long bytes = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < 500; i++) operation();
                double us = Stopwatch.GetElapsedTime(start).TotalMicroseconds / 500;
                if (ReferenceEquals(operation, current)) allocated += GC.GetAllocatedBytesForCurrentThread() - bytes;
                return us;
            }
        }
    }
    finally { Marshal.FreeHGlobal(pixels); }
}
Console.WriteLine($"Checks passed; sink={sink}");
