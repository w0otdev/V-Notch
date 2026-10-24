using System;
using System.Runtime.InteropServices;
using NAudio.Dsp;

namespace VNotch.Services;

// Owned by the capture callback under MusicVisualizer's capture lock.
// Reuse each bin's exact original power across overlapping instrument bands.
internal sealed partial class VisualizerSpectrumBands
{
    private static readonly (int From, int To)[] Bands =
    [
        (20, 60), (60, 250), (250, 500), (500, 2000), (2000, 4000),
        (4000, 8000), (45, 130), (1400, 5000), (350, 2400),
        (6500, 11000), (9000, 16000)
    ];

    private readonly int _fftLength;
    private readonly double _powerScale;
    private readonly double[] _powers;
    private readonly double[] _energies = new double[Bands.Length];
    private readonly int[] _starts = new int[Bands.Length];
    private readonly int[] _ends = new int[Bands.Length];
    private int _sampleRate;
    private int _lastBin;

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_visualizer_spectrum_compute(
        Complex* fft,
        int fftLength,
        int* starts,
        int* ends,
        int lastBin,
        double powerScale,
        double* powersScratch,
        double* outEnergies);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_visualizer_spectrum_update_ranges(
        int sampleRate,
        int fftLength,
        int powersLength,
        int* outStarts,
        int* outEnds,
        out int outLastBin);

    internal VisualizerSpectrumBands(int fftLength)
    {
        if (fftLength < 4 || !System.Numerics.BitOperations.IsPow2((uint)fftLength))
            throw new ArgumentOutOfRangeException(nameof(fftLength));
        _fftLength = fftLength;
        double normalizationScale = 2.0 / fftLength;
        _powerScale = normalizationScale * normalizationScale;
        _powers = new double[fftLength / 2];
    }

    internal unsafe ReadOnlySpan<double> Compute(Complex[] fft, int sampleRate)
    {
        if (fft.Length < _fftLength) throw new ArgumentException("Incomplete FFT frame", nameof(fft));
        if (sampleRate <= 0 || _powers.Length <= 2)
        {
            Array.Clear(_energies);
            return _energies;
        }
        if (_sampleRate != sampleRate) UpdateRanges(sampleRate);

        fixed (Complex* pFft = fft)
        fixed (int* pStarts = _starts, pEnds = _ends)
        fixed (double* pPowers = _powers, pEnergies = _energies)
        {
            _ = vn_visualizer_spectrum_compute(
                pFft, _fftLength, pStarts, pEnds, _lastBin, _powerScale, pPowers, pEnergies);
        }
        return _energies;
    }

    private unsafe void UpdateRanges(int sampleRate)
    {
        _sampleRate = sampleRate;
        fixed (int* pStarts = _starts, pEnds = _ends)
        {
            _ = vn_visualizer_spectrum_update_ranges(
                sampleRate, _fftLength, _powers.Length, pStarts, pEnds, out _lastBin);
        }
    }
}
