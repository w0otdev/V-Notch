using System;
using System.Runtime.InteropServices;

namespace VNotch.Services;

/// <summary>One FFT bin. Layout matches VnSpectrumComplex in Native/Services/VisualizerDsp.cpp.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SpectrumComplex
{
    public float X;
    public float Y;
}

/// <summary>Persistent AGC, beat, role-peak, and RMS state for the music visualizer DSP pipeline.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct VisualizerDspState
{
    public double AgcPeak;
    public double PrevKickEnergy;
    public double PrevSnareEnergy;
    public double KickAccent;
    public double SnareAccent;
    public double PrevRmsForBeat;
    public double BeatAccent;
    public fixed double RolePeaks[5];
    public double RmsSumSquares;
    public int RmsSampleCount;
}

/// <summary>Sample decoding, Hamming-windowed FFT, spectral/beat shaping, and animation math for the music visualizer (native).</summary>
internal static unsafe partial class VisualizerDsp
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll)]
    private static partial int vn_visualizer_read_sample(byte* buffer, int length, int offset, byte isFloat, int bitsPerSample, out float sample);

    [LibraryImport(NativeDll)]
    private static partial int vn_visualizer_mix_frames_to_mono(
        byte* buffer,
        int length,
        int bytesRecorded,
        int channelCount,
        int bitsPerSample,
        byte isFloat,
        float* outMono,
        int monoCap,
        out int outFramesWritten);

    [LibraryImport(NativeDll)]
    private static partial int vn_visualizer_windowed_fft(float* samples, int length, SpectrumComplex* output);

    [LibraryImport(NativeDll)]
    private static partial int vn_visualizer_dsp_reset(ref VisualizerDspState state);

    [LibraryImport(NativeDll)]
    private static partial int vn_visualizer_normalize_amplitude(double amplitude, out float normalized);

    [LibraryImport(NativeDll)]
    private static partial int vn_visualizer_compute_display_targets(
        double* energies,
        int energyCount,
        float rmsNormalized,
        ref VisualizerDspState state,
        float* outTargets);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_visualizer_compute_hashes(
        ReadOnlySpan<char> sid,
        int sidLen,
        uint* outNoAudioHash,
        uint* outFloorHash);

    [LibraryImport(NativeDll)]
    private static partial int vn_visualizer_step_spring(
        ref double value,
        ref double velocity,
        double target,
        double dt);

    [LibraryImport(NativeDll)]
    private static partial int vn_visualizer_update_animation(
        int state,
        double dt,
        double totalSec,
        byte hasFreshAudio,
        double beatAccent,
        float* levels,
        uint* noAudioHash,
        uint* floorHash,
        ref double currentOpacity,
        double* currentHeights,
        out byte isSettled);

    [LibraryImport(NativeDll)]
    private static partial int vn_visualizer_prepare_draw_heights(
        double lastDtMs,
        double* currentHeights,
        double* smoothedHeights,
        double* sortedHeights,
        double* drawHeights);

    /// <summary>Decodes one PCM or float sample; unsupported widths and out-of-range reads give 0.</summary>
    internal static float ReadSample(byte[] buffer, int offset, bool isFloat, int bitsPerSample)
    {
        byte floatFlag = isFloat ? (byte)1 : (byte)0;
        fixed (byte* pinned = buffer)
            return vn_visualizer_read_sample(pinned, buffer.Length, offset, floatFlag, bitsPerSample, out float sample) == 0
                ? sample
                : 0f;
    }

    internal static int MixFramesToMono(
        byte[] buffer,
        int bytesRecorded,
        int channelCount,
        int bitsPerSample,
        bool isFloat,
        Span<float> outMono)
    {
        if (buffer.Length == 0 || outMono.IsEmpty) return 0;
        byte floatFlag = isFloat ? (byte)1 : (byte)0;
        fixed (byte* pBuf = buffer)
        fixed (float* pMono = outMono)
        {
            return vn_visualizer_mix_frames_to_mono(
                pBuf,
                buffer.Length,
                bytesRecorded,
                channelCount,
                bitsPerSample,
                floatFlag,
                pMono,
                outMono.Length,
                out int written) == 0
                ? written
                : 0;
        }
    }

    /// <summary>Windows <paramref name="samples"/> and writes the forward FFT (scaled by 1/N) into <paramref name="output"/>.</summary>
    internal static bool WindowedFft(float[] samples, SpectrumComplex[] output)
    {
        if (output.Length < samples.Length) return false;
        fixed (float* input = samples)
        fixed (SpectrumComplex* result = output)
            return vn_visualizer_windowed_fft(input, samples.Length, result) == 0;
    }

    internal static void ResetState(ref VisualizerDspState state) =>
        _ = vn_visualizer_dsp_reset(ref state);

    internal static float NormalizeAmplitude(double amplitude) =>
        vn_visualizer_normalize_amplitude(amplitude, out float normalized) == 0 ? normalized : 0f;

    internal static void ComputeDisplayTargets(
        ReadOnlySpan<double> energies,
        float rmsNormalized,
        ref VisualizerDspState state,
        float[] targets)
    {
        if (targets == null || targets.Length < 5) return;
        fixed (double* pEnergies = energies)
        fixed (float* pTargets = targets)
            _ = vn_visualizer_compute_display_targets(pEnergies, energies.Length, rmsNormalized, ref state, pTargets);
    }

    internal static void ComputeHashes(string sid, uint[] noAudioHash, uint[] floorHash)
    {
        if (noAudioHash == null || noAudioHash.Length < 5 || floorHash == null || floorHash.Length < 5) return;
        ReadOnlySpan<char> span = (sid ?? string.Empty).AsSpan();
        fixed (uint* pNoAudio = noAudioHash, pFloor = floorHash)
            _ = vn_visualizer_compute_hashes(span, span.Length, pNoAudio, pFloor);
    }

    internal static void StepSpring(ref double value, ref double velocity, double target, double dt) =>
        _ = vn_visualizer_step_spring(ref value, ref velocity, target, dt);

    internal static bool UpdateAnimation(
        int state,
        double dt,
        double totalSec,
        bool hasFreshAudio,
        double beatAccent,
        float[] levels,
        uint[] noAudioHash,
        uint[] floorHash,
        ref double currentOpacity,
        double[] currentHeights)
    {
        if (levels == null || levels.Length < 5 ||
            noAudioHash == null || noAudioHash.Length < 5 ||
            floorHash == null || floorHash.Length < 5 ||
            currentHeights == null || currentHeights.Length < 5)
            return true;
        fixed (float* pLevels = levels)
        fixed (uint* pNoAudio = noAudioHash, pFloor = floorHash)
        fixed (double* pHeights = currentHeights)
        {
            _ = vn_visualizer_update_animation(
                state,
                dt,
                totalSec,
                hasFreshAudio ? (byte)1 : (byte)0,
                beatAccent,
                pLevels,
                pNoAudio,
                pFloor,
                ref currentOpacity,
                pHeights,
                out byte isSettled);
            return isSettled != 0;
        }
    }

    internal static void PrepareDrawHeights(
        double lastDtMs,
        double[] currentHeights,
        double[] smoothedHeights,
        double[] sortedHeights,
        double[] drawHeights)
    {
        if (currentHeights == null || currentHeights.Length < 5 ||
            smoothedHeights == null || smoothedHeights.Length < 5 ||
            sortedHeights == null || sortedHeights.Length < 5 ||
            drawHeights == null || drawHeights.Length < 5)
            return;
        fixed (double* pCurrent = currentHeights, pSmoothed = smoothedHeights, pSorted = sortedHeights, pDraw = drawHeights)
            _ = vn_visualizer_prepare_draw_heights(lastDtMs, pCurrent, pSmoothed, pSorted, pDraw);
    }
}
