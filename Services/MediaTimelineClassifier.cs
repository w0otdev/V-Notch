namespace VNotch.Services;

// SMTC does not expose a live flag. Confirm finite DVR timelines from a moving
// seek window or repeated end-time growth at wall-clock speed, independently of position.
internal sealed class MediaTimelineClassifier
{
    private string _identity = "";
    private TimeSpan _lastEnd;
    private DateTimeOffset _lastEndObservedUtc;
    private int _growthSamples;
    private bool _isLive;
    private bool _wasPlaying;

    public bool Observe(string identity, TimeSpan endTime, TimeSpan seekWindowStart,
        DateTimeOffset nowUtc, bool isPlaying)
    {
        if (_identity != identity || endTime < _lastEnd || nowUtc < _lastEndObservedUtc)
        {
            Reset();
            _identity = identity;
        }

        if (seekWindowStart > TimeSpan.Zero)
            _isLive = true;

        bool resumed = isPlaying && !_wasPlaying && _lastEndObservedUtc != default;
        if (resumed)
        {
            _lastEnd = endTime;
            _lastEndObservedUtc = nowUtc;
        }
        else if (_lastEndObservedUtc != default)
        {
            double elapsed = (nowUtc - _lastEndObservedUtc).TotalSeconds;
            double growth = (endTime - _lastEnd).TotalSeconds;
            if (growth > 0)
            {
                bool growsWithClock = elapsed >= 0.5 && elapsed <= 15 && growth >= 0.5 &&
                    Math.Abs(growth - elapsed) <= Math.Max(0.75, elapsed * 0.25);
                _growthSamples = growsWithClock ? _growthSamples + 1 : 0;
                if (_growthSamples >= 2) _isLive = true;
                else if (!growsWithClock && seekWindowStart <= TimeSpan.Zero) _isLive = false;
            }
            else if (isPlaying && elapsed > 15 && seekWindowStart <= TimeSpan.Zero)
            {
                // The same title can become a finite replay after the broadcast ends.
                _isLive = false;
                _growthSamples = 0;
            }
        }

        if (endTime != _lastEnd || _lastEndObservedUtc == default)
        {
            _lastEnd = endTime;
            _lastEndObservedUtc = nowUtc;
        }
        _wasPlaying = isPlaying;
        return _isLive;
    }

    public void Reset()
    {
        _identity = "";
        _lastEnd = TimeSpan.Zero;
        _lastEndObservedUtc = default;
        _growthSamples = 0;
        _isLive = false;
        _wasPlaying = false;
    }
}
