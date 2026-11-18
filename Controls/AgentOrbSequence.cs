namespace VNotch.Controls;

internal readonly record struct AgentOrbTransition(ThinkingOrbState State, ThinkingOrbState? Previous, double Progress);

/// <summary>A shuffled bag avoids repeats and gives every agent motion a turn.</summary>
internal sealed class AgentOrbSequence(Random? random = null)
{
    internal const double HoldSeconds = 4;
    internal const double TransitionSeconds = .48;
    private readonly Random _random = random ?? Random.Shared;
    private readonly ThinkingOrbState[] _bag =
        [ThinkingOrbState.Searching, ThinkingOrbState.Shaping, ThinkingOrbState.Listening, ThinkingOrbState.Solving];
    private int _index = 4;
    private ThinkingOrbState? _last;

    internal ThinkingOrbState Next()
    {
        if (_index == _bag.Length)
        {
            _random.Shuffle(_bag);
            if (_bag[0] == _last)
            {
                int other = _random.Next(1, _bag.Length);
                (_bag[0], _bag[other]) = (_bag[other], _bag[0]);
            }
            _index = 0;
        }
        var state = _bag[_index++];
        _last = state;
        return state;
    }
}
