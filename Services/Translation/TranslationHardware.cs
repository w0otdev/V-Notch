using System.Runtime.InteropServices;

namespace VNotch.Services.Translation;

internal sealed record TranslationHardware(double RamGiB, double VramGiB, int LogicalProcessors, string GpuName)
{
    internal static TranslationHardware Detect()
    {
        var memory = new Win32Interop.MEMORYSTATUSEX_METRICS { dwLength = (uint)Marshal.SizeOf<Win32Interop.MEMORYSTATUSEX_METRICS>() };
        double ram = Win32Interop.GlobalMemoryStatusEx(ref memory) ? memory.ullTotalPhys / 1073741824d : 0;
        var gpu = GpuMonitorService.Instance.GetGpuInfo();
        return new(ram, gpu.DedicatedVramBytes / 1073741824d, Environment.ProcessorCount, gpu.GpuName);
    }
    // Capacity estimates for quantized weights + context + OS headroom, not speed benchmarks.
    internal bool Fits(TranslationModelProfile model) => RamGiB + .5 >= model.MinimumRamGiB;
    internal TranslationModelProfile? Recommended()
    {
        if (RamGiB < 7.5) return null;
        string id = RamGiB >= 47.5 && VramGiB >= 23.5 ? "gemma4-31b" :
            RamGiB >= 31.5 && VramGiB >= 19.5 ? "gemma4-26b" :
            RamGiB >= 23.5 && VramGiB >= 11.5 && LogicalProcessors >= 8 ? "translategemma-12b" :
            RamGiB >= 15.5 && VramGiB >= 7.5 && LogicalProcessors >= 8 ? "hunyuan-mt-7b" : "translategemma-4b";
        return TranslationModelCatalog.Find(id);
    }
}
