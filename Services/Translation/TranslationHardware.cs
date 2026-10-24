using System.Runtime.InteropServices;

namespace VNotch.Services.Translation;

internal sealed partial record TranslationHardware(double RamGiB, double VramGiB, int LogicalProcessors, string GpuName)
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_transhardware_detect_ram_gib(out double outRamGiB);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_transhardware_fits(
        double ramGiB, int minRamGiB, out byte outFits);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_transhardware_recommended_id(
        double ramGiB, double vramGiB, int logicalProcessors,
        Span<ushort> outBuf, int outCap, out int outLen);

    internal static TranslationHardware Detect()
    {
        vn_transhardware_detect_ram_gib(out double ram);
        var gpu = GpuMonitorService.Instance.GetGpuInfo();
        return new(ram, gpu.DedicatedVramBytes / 1073741824d, Environment.ProcessorCount, gpu.GpuName);
    }

    // Capacity estimates for quantized weights + context + OS headroom, not speed benchmarks.
    internal bool Fits(TranslationModelProfile model) =>
        vn_transhardware_fits(RamGiB, model.MinimumRamGiB, out byte fits) == 0 && fits != 0;

    internal TranslationModelProfile? Recommended()
    {
        Span<char> buf = stackalloc char[32];
        if (vn_transhardware_recommended_id(RamGiB, VramGiB, LogicalProcessors, MemoryMarshal.Cast<char, ushort>(buf), buf.Length, out int len) != 0 || len <= 0)
            return null;
        return TranslationModelCatalog.Find(new string(buf[..len]));
    }
}
