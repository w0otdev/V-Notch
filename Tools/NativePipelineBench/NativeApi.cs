using System.Runtime.InteropServices;

namespace NativePipelineBench;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct Complex { public float X, Y; }
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct Detection { public float X1, Y1, X2, Y2, Confidence; public int Class; }
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct DspState
{
    public double AgcPeak, PrevKick, PrevSnare, Kick, Snare, PrevRms, Beat;
    public fixed double Peaks[5];
    public double RmsSum;
    public int RmsCount;
}

internal sealed unsafe class NativeApi : IDisposable
{
    private readonly nint _module;
    private nint Get(string name) => NativeLibrary.TryGetExport(_module, name, out var address) ? address : 0;
    internal readonly delegate* unmanaged[Stdcall]<float*, int, Complex*, int> Fft;
    internal readonly delegate* unmanaged[Stdcall]<byte*, int, int, int, byte*, int> Palette;
    internal readonly delegate* unmanaged[Stdcall]<char*, int, char*, int, int*, byte*, int> Scrub;
    internal readonly delegate* unmanaged[Stdcall]<byte*, int, int, int, int, float*, int, float*, int> Resize;
    internal readonly delegate* unmanaged[Stdcall]<float*, int, int, float, Detection*, int, int*, int*, int> Parse;
    internal readonly delegate* unmanaged[Stdcall]<char*, int, char*, int, byte, long*, byte*, int> CreateCrop;
    internal readonly delegate* unmanaged[Stdcall]<long, byte*, int, int, int, int, Detection*, int, int*, byte*, int> Infer;
    internal readonly delegate* unmanaged[Stdcall]<long, int> DestroyCrop;
    internal readonly delegate* unmanaged[Stdcall]<byte*, int, int, int, int, byte, float*, int, int*, int> Mix;
    internal readonly delegate* unmanaged[Stdcall]<DspState*, int> Reset;
    internal readonly delegate* unmanaged[Stdcall]<double, float*, int> Normalize;
    internal readonly delegate* unmanaged[Stdcall]<int, int, int, int*, int*, int*, int> Ranges;
    internal readonly delegate* unmanaged[Stdcall]<Complex*, int, int*, int*, int, double, double*, double*, int> Bands;
    internal readonly delegate* unmanaged[Stdcall]<double*, int, float, DspState*, float*, int> Targets;
    internal readonly delegate* unmanaged[Stdcall]<char*, int, long*, int> OpenRecords;
    internal readonly delegate* unmanaged[Stdcall]<long, char*, int, byte*, int, int> Append;
    internal readonly delegate* unmanaged[Stdcall]<long, char*, int, byte*, int, int*, int> Read;
    internal readonly delegate* unmanaged[Stdcall]<long, int> CloseRecords;

    internal NativeApi(string path)
    {
        _module = NativeLibrary.Load(path);
        Fft = (delegate* unmanaged[Stdcall]<float*, int, Complex*, int>)Get("vn_visualizer_windowed_fft");
        Palette = (delegate* unmanaged[Stdcall]<byte*, int, int, int, byte*, int>)Get("vn_dicolor_extract_advanced_palette");
        Scrub = (delegate* unmanaged[Stdcall]<char*, int, char*, int, int*, byte*, int>)Get("vn_sensitive_data_scrub");
        Resize = (delegate* unmanaged[Stdcall]<byte*, int, int, int, int, float*, int, float*, int>)Get("vn_smartcrop_preprocess_bgra32");
        Parse = (delegate* unmanaged[Stdcall]<float*, int, int, float, Detection*, int, int*, int*, int>)Get("vn_smartcrop_parse_yolox_and_nms");
        CreateCrop = (delegate* unmanaged[Stdcall]<char*, int, char*, int, byte, long*, byte*, int>)Get("vn_smartcrop_session_create");
        Infer = (delegate* unmanaged[Stdcall]<long, byte*, int, int, int, int, Detection*, int, int*, byte*, int>)Get("vn_smartcrop_session_infer");
        DestroyCrop = (delegate* unmanaged[Stdcall]<long, int>)Get("vn_smartcrop_session_destroy");
        Mix = (delegate* unmanaged[Stdcall]<byte*, int, int, int, int, byte, float*, int, int*, int>)Get("vn_visualizer_mix_frames_to_mono");
        Reset = (delegate* unmanaged[Stdcall]<DspState*, int>)Get("vn_visualizer_dsp_reset");
        Normalize = (delegate* unmanaged[Stdcall]<double, float*, int>)Get("vn_visualizer_normalize_amplitude");
        Ranges = (delegate* unmanaged[Stdcall]<int, int, int, int*, int*, int*, int>)Get("vn_visualizer_spectrum_update_ranges");
        Bands = (delegate* unmanaged[Stdcall]<Complex*, int, int*, int*, int, double, double*, double*, int>)Get("vn_visualizer_spectrum_compute");
        Targets = (delegate* unmanaged[Stdcall]<double*, int, float, DspState*, float*, int>)Get("vn_visualizer_compute_display_targets");
        OpenRecords = (delegate* unmanaged[Stdcall]<char*, int, long*, int>)Get("vn_clipboard_record_open");
        Append = (delegate* unmanaged[Stdcall]<long, char*, int, byte*, int, int>)Get("vn_clipboard_record_append");
        Read = (delegate* unmanaged[Stdcall]<long, char*, int, byte*, int, int*, int>)Get("vn_clipboard_record_read");
        CloseRecords = (delegate* unmanaged[Stdcall]<long, int>)Get("vn_clipboard_record_close");
        if (Fft == null || Palette == null || Scrub == null || Parse == null) throw new InvalidOperationException("Incomplete native benchmark library.");
    }
    public void Dispose() => NativeLibrary.Free(_module);
    internal static void Check(int status) { if (status != 0) throw new InvalidOperationException($"Native status {status}"); }
}
