using System;

namespace VNotch.Services;

internal readonly struct SessionScoreInputs
{
    public bool HasTitle { get; init; }
    public bool HasArtist { get; init; }
    public bool HasThumbnail { get; init; }
    public bool ArtistIsNonGeneric { get; init; }

    public bool IsSpotify { get; init; }
    public bool IsMusic { get; init; }
    public bool IsYouTube { get; init; }
    public bool IsBrowser { get; init; }

    public bool IsOsCurrent { get; init; }
    public bool DedicatedMusicAppPlaying { get; init; }
    public bool IsActive { get; init; }

    public double? PlayStartAgeSeconds { get; init; }
    public double? LatestPlayingAgeSeconds { get; init; }
    public double? LastPlayingIdleSeconds { get; init; }
    public double? TimelineAgeSeconds { get; init; }

    public bool TimelineBoost { get; init; }
    public bool TimelinePenalty { get; init; }
}

internal static class SessionScorer
{
    public static int Score(in SessionScoreInputs x)
    {
        return ScoreMetadata(in x)
             + ScoreSourceKind(in x)
             + ScoreOsAndActive(in x)
             + ScoreTimeDecay(in x)
             + ScoreTimelineModifiers(in x);
    }

    private static int ScoreMetadata(in SessionScoreInputs x)
    {
        if (!x.HasTitle) return 0;

        int score = 1550;
        if (x.HasArtist) score += 20;
        if (x.HasThumbnail) score += 10;
        if (x.ArtistIsNonGeneric) score += 200;
        return score;
    }

    private static int ScoreSourceKind(in SessionScoreInputs x)
    {
        if (x.IsSpotify) return 400;
        if (x.IsMusic) return 400;
        if (x.IsYouTube) return 350;
        if (x.IsBrowser) return 100;
        return 0;
    }

    private static int ScoreOsAndActive(in SessionScoreInputs x)
    {
        int score = 0;

        if (x.IsOsCurrent)
        {
            score += x.DedicatedMusicAppPlaying ? 200 : 1000;
        }

        if (x.IsActive)
        {
            score += 500;
            if (x.IsOsCurrent && !(x.IsBrowser || x.IsYouTube))
            {
                score += 1000;
            }
        }

        return score;
    }

    private static int ScoreTimeDecay(in SessionScoreInputs x)
    {
        int score = 0;

        if (x.PlayStartAgeSeconds is double playStartAge && playStartAge >= 0 && playStartAge < 45)
        {
            score += (int)Math.Max(0, 2600 - (playStartAge * 45));
        }

        if (x.LatestPlayingAgeSeconds is double latestAge && latestAge >= 0 && latestAge < 30)
        {
            score += (int)Math.Max(0, 2200 - (latestAge * 60));
        }

        if (x.LastPlayingIdleSeconds is double idleSeconds && idleSeconds < 30)
        {
            score += (int)((30 - idleSeconds) * 10);
        }

        if (x.TimelineAgeSeconds is double timelineAge && timelineAge >= 0 && timelineAge < 20)
        {
            score += (int)Math.Max(0, 200 - (timelineAge * 8));
        }

        return score;
    }

    private static int ScoreTimelineModifiers(in SessionScoreInputs x)
    {
        if (x.TimelineBoost) return 3000;
        if (x.TimelinePenalty) return -3000;
        return 0;
    }
}
