using System.Diagnostics;

namespace NativePipelineBench;

internal sealed record Measurement(string Area, string Workload, string Variant, int Samples, int Batch,
    double MedianUs, double P95Us, double MeanUs, double MinUs, double MaxUs, double ManagedBytesPerOperation,
    int Gen0Collections, int Gen1Collections, int Gen2Collections, double[] RawUs);

internal static class Statistics
{
    internal static Measurement Summarize(string area, string workload, string variant, int batch,
        double[] times, long allocation, int[] collections)
    {
        var sorted = times.Order().ToArray();
        double median = sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2];
        return new(area, workload, variant, times.Length, batch, median, sorted[(int)Math.Ceiling(.95 * sorted.Length) - 1],
            times.Average(), sorted[0], sorted[^1], allocation / (double)(batch * times.Length), collections[0], collections[1], collections[2], times);
    }

    internal static Measurement[] Compare(string area, string workload, (string Name, Action Run)[] cases,
        int samples = 41, int? fixedBatch = null, int warmup = 16)
    {
        foreach (var item in cases) for (int i = 0; i < warmup; ++i) item.Run();
        int batch = fixedBatch ?? 1;
        if (fixedBatch == null)
        {
            double slowestUs = 0;
            foreach (var item in cases)
            {
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < 8; ++i) item.Run();
                slowestUs = Math.Max(slowestUs, Stopwatch.GetElapsedTime(start).TotalMicroseconds / 8);
            }
            batch = Math.Clamp((int)(8000 / Math.Max(slowestUs, .1)), 1, 8192);
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var times = cases.Select(_ => new double[samples]).ToArray();
        var allocated = new long[cases.Length];
        var collections = cases.Select(_ => new int[3]).ToArray();
        for (int sample = 0; sample < samples; ++sample)
        for (int order = 0; order < cases.Length; ++order)
        {
            int index = (order + sample) % cases.Length;
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
            for (int iteration = 0; iteration < batch; ++iteration) cases[index].Run();
            long elapsed = Stopwatch.GetTimestamp() - start;
            allocated[index] += GC.GetAllocatedBytesForCurrentThread() - before;
            collections[index][0] += GC.CollectionCount(0) - g0;
            collections[index][1] += GC.CollectionCount(1) - g1;
            collections[index][2] += GC.CollectionCount(2) - g2;
            times[index][sample] = elapsed * (1_000_000d / Stopwatch.Frequency) / batch;
        }
        return cases.Select((item, i) => Summarize(area, workload, item.Name, batch, times[i], allocated[i], collections[i])).ToArray();
    }
}
