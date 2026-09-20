using NAudio.Dsp;
namespace VNotch.Benchmarks;
// Frozen pre-optimization band evaluation (same operation and accumulation order).
internal sealed class BaselineSpectrumBands
{
    private const int FftLength = 512;
    private Complex[] _fftData = Array.Empty<Complex>();
    private int _sampleRate;
    private readonly double[] _results = new double[11];
    internal ReadOnlySpan<double> Compute(Complex[] fft, int sampleRate)
    {
        _fftData = fft;
        _sampleRate = sampleRate;
        _results[0] = ComputeBandEnergy(20, 60);
        _results[1] = ComputeBandEnergy(60, 250);
        _results[2] = ComputeBandEnergy(250, 500);
        _results[3] = ComputeBandEnergy(500, 2000);
        _results[4] = ComputeBandEnergy(2000, 4000);
        _results[5] = ComputeBandEnergy(4000, 8000);
        _results[6] = ComputeBandEnergy(45, 130);
        _results[7] = ComputeBandEnergy(1400, 5000);
        _results[8] = ComputeBandEnergy(350, 2400);
        _results[9] = ComputeBandEnergy(6500, 11000);
        _results[10] = ComputeBandEnergy(9000, 16000);
        return _results;
    }
    private double ComputeBandEnergy(int fromHz, int toHz)
    {
        int maxBin = (FftLength / 2) - 1;
        if (_sampleRate <= 0 || maxBin <= 1) return 0;

        int start = FrequencyToBin(fromHz);
        int end = FrequencyToBin(toHz);
        if (end < start) (start, end) = (end, start);

        start = Math.Clamp(start, 1, maxBin);
        end = Math.Clamp(end, start, maxBin);

        double sumSquares = 0;
        int count = 0;
        for (int i = start; i <= end; i++)
        {
            double mag = Math.Sqrt((_fftData[i].X * _fftData[i].X) + (_fftData[i].Y * _fftData[i].Y));
            double normalizedMag = mag / (FftLength * 0.5);
            sumSquares += normalizedMag * normalizedMag;
            count++;
        }

        return count > 0 ? Math.Sqrt(sumSquares / count) : 0;
    }

    private int FrequencyToBin(int frequencyHz)
    {
        if (_sampleRate <= 0) return 1;
        return (int)Math.Round((frequencyHz / (double)_sampleRate) * FftLength);
    }

}
