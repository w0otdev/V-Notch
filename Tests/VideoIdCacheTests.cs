using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class VideoIdCacheTests
{
    private readonly VideoIdCache _cache = new();

    #region Set / Get

    [Fact]
    public void Get_ExactTrackKey_ReturnsId()
    {
        _cache.Set("Bohemian Rhapsody", "abc123");
        Assert.Equal("abc123", _cache.Get("Bohemian Rhapsody"));
    }

    [Fact]
    public void Get_ByTrackOnlyIdentity_WhenStoredUnderIdentityKey()
    {
        _cache.Set("bohemian rhapsody|", "id-via-identity");
        Assert.Equal("id-via-identity", _cache.Get("Bohemian Rhapsody"));
    }

    [Fact]
    public void Get_Missing_ReturnsNull()
    {
        Assert.Null(_cache.Get("nothing here"));
    }

    [Fact]
    public void Get_NullOrEmptyTrack_ReturnsNull()
    {
        _cache.Set("song", "id");
        Assert.Null(_cache.Get(null));
        Assert.Null(_cache.Get(""));
    }

    [Fact]
    public void Set_NullOrEmptyKey_Ignored()
    {
        _cache.Set(null, "id");
        _cache.Set("", "id");
        Assert.Equal(0, _cache.Count);
    }

    [Fact]
    public void Set_IsCaseInsensitiveOnKey()
    {
        _cache.Set("Song", "id1");
        _cache.Set("song", "id2");
        Assert.Equal(1, _cache.Count);
        Assert.Equal("id2", _cache.Get("SONG"));
    }

    #endregion

    #region Bounded eviction

    [Fact]
    public void Set_EvictsWhenExceedingCapacity()
    {
        for (int i = 0; i < 60; i++)
        {
            _cache.Set($"track-{i}", $"id-{i}");
        }

        Assert.Equal(50, _cache.Count);
    }

    #endregion

    #region ForgetExcept

    [Fact]
    public void ForgetExcept_KeepsOnlyMatchingIdentity()
    {
        _cache.Set("keep", "k");
        _cache.Set("drop1", "d1");
        _cache.Set("drop2", "d2");

        _cache.ForgetExcept("keep");

        Assert.Equal(1, _cache.Count);
        Assert.Equal("k", _cache.Get("keep"));
        Assert.Null(_cache.Get("drop1"));
    }

    [Fact]
    public void ForgetExcept_NullOrEmpty_ClearsAll()
    {
        _cache.Set("a", "1");
        _cache.Set("b", "2");

        _cache.ForgetExcept(null);

        Assert.Equal(0, _cache.Count);
    }

    #endregion

    #region Evict

    [Fact]
    public void Evict_RemovesEntry_WhenIdMatchesStale()
    {
        _cache.Set("song", "stale");
        _cache.Evict("song", "stale");
        Assert.Null(_cache.Get("song"));
    }

    [Fact]
    public void Evict_KeepsEntry_WhenIdDiffersFromStale()
    {
        _cache.Set("song", "fresh");
        _cache.Evict("song", "stale");
        Assert.Equal("fresh", _cache.Get("song"));
    }

    [Fact]
    public void Evict_NullOrEmptyArgs_NoThrow_NoChange()
    {
        _cache.Set("song", "id");
        _cache.Evict(null, "id");
        _cache.Evict("song", "");
        Assert.Equal("id", _cache.Get("song"));
    }

    [Fact]
    public void Evict_ThenReAdd_DoesNotPrematurelyEvictReAddedTrackAtCapacity()
    {
        _cache.Set("songA", "v1");
        _cache.Set("other-0", "id-0");
        _cache.Evict("songA", "v1");
        _cache.Set("songA", "v2");

        for (int i = 1; i < 50; i++)
        {
            _cache.Set($"other-{i}", $"id-{i}");
        }

        Assert.Equal(50, _cache.Count);
        Assert.Equal("v2", _cache.Get("songA"));
        Assert.Null(_cache.Get("other-0"));
    }

    #endregion

    #region Mismatch blocklist

    [Fact]
    public void Mismatch_MarkAndQuery()
    {
        Assert.False(_cache.IsMismatch("vid"));
        _cache.MarkMismatch("vid");
        Assert.True(_cache.IsMismatch("vid"));
    }

    [Fact]
    public void Mismatch_IsCaseSensitive()
    {
        _cache.MarkMismatch("Vid");
        Assert.False(_cache.IsMismatch("vid"));
    }

    [Fact]
    public void ClearMismatches_RemovesAll()
    {
        _cache.MarkMismatch("a");
        _cache.MarkMismatch("b");
        _cache.ClearMismatches();
        Assert.False(_cache.IsMismatch("a"));
        Assert.False(_cache.IsMismatch("b"));
    }

    #endregion
}
