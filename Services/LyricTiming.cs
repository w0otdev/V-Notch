namespace VNotch.Services;

internal readonly record struct LyricWord(TimeSpan Start, TimeSpan End, int Offset, int Length);

