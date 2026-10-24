using System.Runtime.InteropServices;

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

internal static partial class SessionScorer
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnSessionScoreInputs
    {
        public byte HasTitle;
        public byte HasArtist;
        public byte HasThumbnail;
        public byte ArtistIsNonGeneric;
        public byte IsSpotify;
        public byte IsMusic;
        public byte IsYouTube;
        public byte IsBrowser;
        public byte IsOsCurrent;
        public byte DedicatedMusicAppPlaying;
        public byte IsActive;
        public byte HasPlayStartAge;
        public double PlayStartAgeSeconds;
        public byte HasLatestPlayingAge;
        public double LatestPlayingAgeSeconds;
        public byte HasLastPlayingIdle;
        public double LastPlayingIdleSeconds;
        public byte HasTimelineAge;
        public double TimelineAgeSeconds;
        public byte TimelineBoost;
        public byte TimelinePenalty;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_session_scorer_score(in VnSessionScoreInputs x, out int outScore);

    public static int Score(in SessionScoreInputs x)
    {
        var native = new VnSessionScoreInputs
        {
            HasTitle = x.HasTitle ? (byte)1 : (byte)0,
            HasArtist = x.HasArtist ? (byte)1 : (byte)0,
            HasThumbnail = x.HasThumbnail ? (byte)1 : (byte)0,
            ArtistIsNonGeneric = x.ArtistIsNonGeneric ? (byte)1 : (byte)0,
            IsSpotify = x.IsSpotify ? (byte)1 : (byte)0,
            IsMusic = x.IsMusic ? (byte)1 : (byte)0,
            IsYouTube = x.IsYouTube ? (byte)1 : (byte)0,
            IsBrowser = x.IsBrowser ? (byte)1 : (byte)0,
            IsOsCurrent = x.IsOsCurrent ? (byte)1 : (byte)0,
            DedicatedMusicAppPlaying = x.DedicatedMusicAppPlaying ? (byte)1 : (byte)0,
            IsActive = x.IsActive ? (byte)1 : (byte)0,
            HasPlayStartAge = x.PlayStartAgeSeconds.HasValue ? (byte)1 : (byte)0,
            PlayStartAgeSeconds = x.PlayStartAgeSeconds ?? 0.0,
            HasLatestPlayingAge = x.LatestPlayingAgeSeconds.HasValue ? (byte)1 : (byte)0,
            LatestPlayingAgeSeconds = x.LatestPlayingAgeSeconds ?? 0.0,
            HasLastPlayingIdle = x.LastPlayingIdleSeconds.HasValue ? (byte)1 : (byte)0,
            LastPlayingIdleSeconds = x.LastPlayingIdleSeconds ?? 0.0,
            HasTimelineAge = x.TimelineAgeSeconds.HasValue ? (byte)1 : (byte)0,
            TimelineAgeSeconds = x.TimelineAgeSeconds ?? 0.0,
            TimelineBoost = x.TimelineBoost ? (byte)1 : (byte)0,
            TimelinePenalty = x.TimelinePenalty ? (byte)1 : (byte)0,
        };
        _ = vn_session_scorer_score(in native, out int score);
        return score;
    }
}
