using System.Diagnostics.Tracing;

internal static class BenchmarkChecks
{
    internal static string[] Cadence(int target, double fps, double intervalP95Ms, double latePercent)
    {
        if (target <= 0 || !double.IsFinite(fps) || !double.IsFinite(intervalP95Ms) ||
            !double.IsFinite(latePercent) || fps < 0 || intervalP95Ms < 0 || latePercent is < 0 or > 100)
            return ["Invalid cadence sample"];
        var failures = new List<string>();
        if (fps < target * .95) failures.Add("Mean FPS below 95% of target");
        if (fps > target * 1.02) failures.Add("FPS exceeds target cap by more than 2%");
        if (intervalP95Ms > 1500.0 / target) failures.Add("Interval p95 exceeds 1.5 frame budgets");
        if (latePercent > 1) failures.Add("More than 1% of intervals exceed 1.5 frame budgets");
        return failures.ToArray();
    }

    internal static void SelfCheck()
    {
        static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Benchmark gate self-check failed"); }
        Require(Cadence(120, 117, 10, .5).Length == 0);
        Require(Cadence(120, 114, 12.5, 1).Length == 0);
        Require(Cadence(120, 90, 10, 0).Length == 1);
        Require(Cadence(120, 130, 10, 0).Length == 1);
        Require(Cadence(120, 117, 18, 5).Length == 2);
        Require(Cadence(120, double.NaN, 10, 0).Length == 1);
        Require(Cadence(0, 0, 0, 0).Length == 1);
        Console.WriteLine("PASS: cadence gates reject low throughput, cap bypass, long intervals, invalid samples and excess jitter.");
    }
}

[EventSource(Name = "VNotch-GlassBenchmark")]
internal sealed class BenchmarkEvents : EventSource
{
    internal static readonly BenchmarkEvents Log = new();
    [Event(1)] public void PhaseStart(string name, int repeat) => WriteEvent(1, name, repeat);
    [Event(2)] public void PhaseStop(string name, int repeat) => WriteEvent(2, name, repeat);
}
