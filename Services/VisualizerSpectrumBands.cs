using NAudio.Dsp;

namespace VNotch.Services;

// Owned by the capture callback under MusicVisualizer's capture lock.
// Reuse each bin's exact original power across overlapping instrument bands.
internal sealed class VisualizerSpectrumBands
{
    private static readonly (int From, int To)[] Bands =
    [
        (20, 60), (60, 250), (250, 500), (500, 2000), (2000, 4000),
        (4000, 8000), (45, 130), (1400, 5000), (350, 2400),
        (6500, 11000), (9000, 16000)
    ];

    private readonly int _fftLength;
    private readonly double _normalizationScale;
    private readonly double[] _powers;
    private readonly double[] _energies = new double[Bands.Length];
    private readonly int[] _starts = new int[Bands.Length];
    private readonly int[] _ends = new int[Bands.Length];
    private int _sampleRate;
    private int _lastBin;

    internal VisualizerSpectrumBands(int fftLength)
    {
        if (fftLength < 4 || !System.Numerics.BitOperations.IsPow2((uint)fftLength))
            throw new ArgumentOutOfRangeException(nameof(fftLength));
        _fftLength = fftLength;
        _normalizationScale = 2.0 / fftLength;
        _powers = new double[fftLength / 2];
    }

    internal ReadOnlySpan<double> Compute(Complex[] fft, int sampleRate)
    {
        if (fft.Length < _fftLength) throw new ArgumentException("Incomplete FFT frame", nameof(fft));
        if (sampleRate <= 0 || _powers.Length <= 2)
        {
            Array.Clear(_energies);
            return _energies;
        }
        if (_sampleRate != sampleRate) UpdateRanges(sampleRate);

        for (int i = 1; i <= _lastBin; i++)
        {
            // Keep sqrt, normalization and squaring in their original order;
            // algebraic cancellation would change floating-point rounding.
            double mag = Math.Sqrt((fft[i].X * fft[i].X) + (fft[i].Y * fft[i].Y));
            // FFT length is a power of two: reciprocal multiplication is the
            // same exact binary scaling as the original constant division.
            double normalized = mag * _normalizationScale;
            _powers[i] = normalized * normalized;
        }
        for (int band = 0; band < Bands.Length; band++)
        {
            double sum = 0;
            for (int i = _starts[band]; i <= _ends[band]; i++) sum += _powers[i];
            _energies[band] = Math.Sqrt(sum / (_ends[band] - _starts[band] + 1));
        }
        return _energies;
    }

    private void UpdateRanges(int sampleRate)
    {
        _sampleRate = sampleRate;
        _lastBin = 0;
        int maximum = _powers.Length - 1;
        for (int band = 0; band < Bands.Length; band++)
        {
            int start = (int)Math.Round(Bands[band].From / (double)sampleRate * _fftLength);
            int end = (int)Math.Round(Bands[band].To / (double)sampleRate * _fftLength);
            if (end < start) (start, end) = (end, start);
            _starts[band] = Math.Clamp(start, 1, maximum);
            _ends[band] = Math.Clamp(end, _starts[band], maximum);
            _lastBin = Math.Max(_lastBin, _ends[band]);
        }
    }
}
