namespace VNotch.Models;

public sealed class PerformanceDebugSnapshot
{
    public double Fps { get; init; }
    public int RefreshRateHz { get; init; }
    public string GpuName { get; init; } = "DirectX Display Adapter";
    public ulong DedicatedVramBytes { get; init; }

    // CPU
    public double ProcessCpuPercent { get; init; }
    public double GlobalCpuPercent { get; init; }

    // RAM
    public ulong ProcessRamBytes { get; init; }
    public ulong GlobalRamUsedBytes { get; init; }
    public ulong GlobalRamTotalBytes { get; init; }
    public double GlobalRamPercent { get; init; }

    // GPU
    public double ProcessGpuPercent { get; init; }
    public double GlobalGpuPercent { get; init; }

    // Network
    public double NetDownBytesPerSec { get; init; }
    public double NetUpBytesPerSec { get; init; }
}
