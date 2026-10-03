using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Running;

namespace VNotch.Benchmarks;

internal static class Program
{
    private static void Main(string[] args)
    {
        var config = ManualConfig.Create(DefaultConfig.Instance);
        if (args.Contains("--counters"))
            config.AddHardwareCounters(HardwareCounter.CacheMisses, HardwareCounter.BranchMispredictions);
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
            .Run(args.Where(a => a != "--counters").ToArray(), config);
    }
}
