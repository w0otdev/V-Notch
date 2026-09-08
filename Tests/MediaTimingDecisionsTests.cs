using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class MediaTimingDecisionsTests
{
    private static readonly DateTime Base = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    #region EvaluateEmptyMetadataHold

    [Fact]
    public void EmptyMetadataHold_NonEmptyTrack_ResetsAndStoresSignature()
    {
        var (hold, emptyStart, stable) = MediaTimingDecisions.EvaluateEmptyMetadataHold(
            currentTrack: "Song", isAnyMediaPlaying: true, currentSignature: "sig",
            lastSource: "Spotify", emptyStart: Base.AddSeconds(-10), stableSignature: "old", now: Base);

        Assert.False(hold);
        Assert.Equal(DateTime.MinValue, emptyStart);
        Assert.Equal("sig", stable);
    }

    [Fact]
    public void EmptyMetadataHold_EmptyTrack_NotPlaying_ClearsState()
    {
        var (hold, emptyStart, stable) = MediaTimingDecisions.EvaluateEmptyMetadataHold(
            currentTrack: "", isAnyMediaPlaying: false, currentSignature: "sig",
            lastSource: "Spotify", emptyStart: Base, stableSignature: "old", now: Base);

        Assert.False(hold);
        Assert.Equal(DateTime.MinValue, emptyStart);
        Assert.Equal("", stable);
    }

    [Fact]
    public void EmptyMetadataHold_EmptyPlaying_WithinWindow_Holds()
    {
        var (hold, emptyStart, _) = MediaTimingDecisions.EvaluateEmptyMetadataHold(
            currentTrack: "", isAnyMediaPlaying: true, currentSignature: "sig",
            lastSource: "Spotify", emptyStart: DateTime.MinValue, stableSignature: "sig", now: Base);

        Assert.True(hold);
        Assert.Equal(Base, emptyStart);
    }

    [Fact]
    public void EmptyMetadataHold_EmptyPlaying_NoStableSignature_DoesNotHold()
    {
        var (hold, _, _) = MediaTimingDecisions.EvaluateEmptyMetadataHold(
            currentTrack: "", isAnyMediaPlaying: true, currentSignature: "sig",
            lastSource: "Spotify", emptyStart: DateTime.MinValue, stableSignature: "", now: Base);

        Assert.False(hold);
    }

    [Fact]
    public void EmptyMetadataHold_NonVideo_ExpiresAfter2Point5s()
    {
        var (hold, _, _) = MediaTimingDecisions.EvaluateEmptyMetadataHold(
            currentTrack: "", isAnyMediaPlaying: true, currentSignature: "sig",
            lastSource: "Spotify", emptyStart: Base.AddSeconds(-3), stableSignature: "sig", now: Base);

        Assert.False(hold);
    }

    [Fact]
    public void EmptyMetadataHold_VideoSource_GetsLongerWindow()
    {
        var (holdYouTube, _, _) = MediaTimingDecisions.EvaluateEmptyMetadataHold(
            currentTrack: "", isAnyMediaPlaying: true, currentSignature: "sig",
            lastSource: "YouTube", emptyStart: Base.AddSeconds(-3), stableSignature: "sig", now: Base);
        Assert.True(holdYouTube);

        var (holdBrowser, _, _) = MediaTimingDecisions.EvaluateEmptyMetadataHold(
            currentTrack: "", isAnyMediaPlaying: true, currentSignature: "sig",
            lastSource: "Browser", emptyStart: Base.AddSeconds(-3), stableSignature: "sig", now: Base);
        Assert.True(holdBrowser);
    }

    #endregion

    #region EvaluateNewTrackDebounce

    private static NewTrackDebounceParams CreateDebounceParams() => new(
        CurrentTrack: "New Song",
        CurrentArtist: "Artist",
        IsPlaying: false,
        ForceRefresh: false,
        LastPublishedTrackIdentity: "old|artist",
        PendingKey: "",
        PendingSince: Base,
        NowUtc: Base,
        DebounceMs: 600);

    [Fact]
    public void NewTrackDebounce_NotANewTrack_NoDebounce()
    {
        var (debounce, pendingKey, _) = MediaTimingDecisions.EvaluateNewTrackDebounce(CreateDebounceParams() with
        {
            CurrentTrack = "Song",
            LastPublishedTrackIdentity = "song|artist"
        });

        Assert.False(debounce);
        Assert.Equal("", pendingKey);
    }

    [Fact]
    public void NewTrackDebounce_NewTrackButPlaying_NoDebounce()
    {
        var (debounce, _, _) = MediaTimingDecisions.EvaluateNewTrackDebounce(CreateDebounceParams() with
        {
            CurrentTrack = "New",
            IsPlaying = true
        });

        Assert.False(debounce);
    }

    [Fact]
    public void NewTrackDebounce_FirstObservation_DebouncesAndAnchorsTime()
    {
        var (debounce, pendingKey, pendingSince) = MediaTimingDecisions.EvaluateNewTrackDebounce(CreateDebounceParams() with
        {
            CurrentTrack = "New Song",
            PendingSince = DateTime.MinValue
        });

        Assert.True(debounce);
        Assert.Equal("new song|artist", pendingKey);
        Assert.Equal(Base, pendingSince);
    }

    [Fact]
    public void NewTrackDebounce_WindowElapsed_StopsDebouncing()
    {
        var (debounce, _, _) = MediaTimingDecisions.EvaluateNewTrackDebounce(CreateDebounceParams() with
        {
            CurrentTrack = "New Song",
            PendingKey = "new song|artist",
            PendingSince = Base.AddMilliseconds(-700)
        });

        Assert.False(debounce);
    }

    [Fact]
    public void NewTrackDebounce_ForceRefresh_Bypasses()
    {
        var (debounce, _, _) = MediaTimingDecisions.EvaluateNewTrackDebounce(CreateDebounceParams() with
        {
            CurrentTrack = "New",
            ForceRefresh = true
        });

        Assert.False(debounce);
    }

    #endregion

    #region EvaluateArtistStabilization

    [Fact]
    public void ArtistStabilization_GenericArtist_WithinWindow_SubstitutesStable()
    {
        var (artist, stable) = MediaTimingDecisions.EvaluateArtistStabilization(
            currentArtist: "YouTube", stableArtist: "Real Artist",
            lastSourceConfirmedTime: Base.AddSeconds(-5), now: Base);

        Assert.Equal("Real Artist", artist);
        Assert.Equal("Real Artist", stable);
    }

    [Fact]
    public void ArtistStabilization_GenericArtist_WindowExpired_KeepsGeneric()
    {
        var (artist, stable) = MediaTimingDecisions.EvaluateArtistStabilization(
            currentArtist: "YouTube", stableArtist: "Real Artist",
            lastSourceConfirmedTime: Base.AddSeconds(-20), now: Base);

        Assert.Equal("YouTube", artist);
        Assert.Equal("Real Artist", stable);
    }

    [Fact]
    public void ArtistStabilization_RealArtist_RemembersAsStable()
    {
        var (artist, stable) = MediaTimingDecisions.EvaluateArtistStabilization(
            currentArtist: "Adele", stableArtist: "old",
            lastSourceConfirmedTime: Base.AddSeconds(-5), now: Base);

        Assert.Equal("Adele", artist);
        Assert.Equal("Adele", stable);
    }

    [Fact]
    public void ArtistStabilization_GenericArtist_NoStable_LeavesUnchanged()
    {
        var (artist, stable) = MediaTimingDecisions.EvaluateArtistStabilization(
            currentArtist: "YouTube", stableArtist: "",
            lastSourceConfirmedTime: Base.AddSeconds(-5), now: Base);

        Assert.Equal("YouTube", artist);
        Assert.Equal("", stable);
    }

    #endregion

    #region ShouldPreserveSoundCloud

    private static SoundCloudPreserveParams CreateSoundCloudParams() => new(
        MediaSource: "Browser",
        CurrentTrack: "Some Song",
        CurrentArtist: "Some Artist",
        SourceAppId: "Chrome",
        SessionInstanceKey: "sess-1",
        LastSource: "SoundCloud",
        LastPublishedSessionInstanceKey: "sess-1",
        LastMetadataChangeTime: Base.AddSeconds(-1.0),
        Now: Base,
        HasSessionOverride: false,
        SessionOverride: "",
        FreshnessSeconds: 3.0);

    [Fact]
    public void ShouldPreserveSoundCloud_AllGatesPass_True()
    {
        Assert.True(MediaTimingDecisions.ShouldPreserveSoundCloud(CreateSoundCloudParams()));
    }

    [Fact]
    public void ShouldPreserveSoundCloud_SourceNotBrowser_False()
    {
        Assert.False(MediaTimingDecisions.ShouldPreserveSoundCloud(CreateSoundCloudParams() with { MediaSource = "Spotify" }));
    }

    [Fact]
    public void ShouldPreserveSoundCloud_LastSourceNotSoundCloud_False()
    {
        Assert.False(MediaTimingDecisions.ShouldPreserveSoundCloud(CreateSoundCloudParams() with { LastSource = "YouTube" }));
    }

    [Fact]
    public void ShouldPreserveSoundCloud_EmptyTrack_False()
    {
        Assert.False(MediaTimingDecisions.ShouldPreserveSoundCloud(CreateSoundCloudParams() with { CurrentTrack = "  " }));
    }

    [Fact]
    public void ShouldPreserveSoundCloud_NonBrowserApp_False()
    {
        Assert.False(MediaTimingDecisions.ShouldPreserveSoundCloud(CreateSoundCloudParams() with { SourceAppId = "Spotify.exe" }));
    }

    [Fact]
    public void ShouldPreserveSoundCloud_SessionKeyMismatch_False()
    {
        Assert.False(MediaTimingDecisions.ShouldPreserveSoundCloud(CreateSoundCloudParams() with { SessionInstanceKey = "sess-2" }));
    }

    [Fact]
    public void ShouldPreserveSoundCloud_StaleMetadata_False()
    {
        Assert.False(MediaTimingDecisions.ShouldPreserveSoundCloud(CreateSoundCloudParams() with { LastMetadataChangeTime = Base.AddSeconds(-5.0) }));
    }

    [Fact]
    public void ShouldPreserveSoundCloud_YouTubeHintInTrack_False()
    {
        Assert.False(MediaTimingDecisions.ShouldPreserveSoundCloud(CreateSoundCloudParams() with { CurrentTrack = "watch on youtube" }));
    }

    [Fact]
    public void ShouldPreserveSoundCloud_SessionOverridePointsElsewhere_False()
    {
        Assert.False(MediaTimingDecisions.ShouldPreserveSoundCloud(CreateSoundCloudParams() with { HasSessionOverride = true, SessionOverride = "YouTube" }));
    }

    [Fact]
    public void ShouldPreserveSoundCloud_SessionOverrideIsSoundCloud_True()
    {
        Assert.True(MediaTimingDecisions.ShouldPreserveSoundCloud(CreateSoundCloudParams() with { HasSessionOverride = true, SessionOverride = "SoundCloud" }));
    }

    #endregion
}
