using System.IO;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class MediaSourceCacheTests : IDisposable
{
    private readonly string _tempPath;
    private readonly MediaSourceCache _cache;

    public MediaSourceCacheTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"vnotch-test-{Guid.NewGuid()}.json");
        _cache = new MediaSourceCache(_tempPath);
    }

    public void Dispose()
    {
        if (File.Exists(_tempPath))
            File.Delete(_tempPath);
    }

    private static void Set(MediaSourceCache cache, string? key, string? value) =>
        cache.SetBoth(key ?? string.Empty, string.Empty, value ?? string.Empty);

    private static string? Get(MediaSourceCache cache, string key) =>
        cache.TryGet(key, out var value) ? value : null;

    [Fact]
    public void Set_And_Get_ReturnsValue()
    {
        Set(_cache, "track1|artist1", "YouTube");
        Assert.Equal("YouTube", Get(_cache, "track1|artist1"));
    }

    [Fact]
    public void Get_NonExistent_ReturnsNull()
    {
        Assert.Null(Get(_cache, "nonexistent"));
    }

    [Fact]
    public void TryGet_Existing_ReturnsTrueWithValue()
    {
        Set(_cache, "key", "Spotify");
        Assert.True(_cache.TryGet("key", out var value));
        Assert.Equal("Spotify", value);
    }

    [Fact]
    public void TryGet_NonExistent_ReturnsFalse()
    {
        Assert.False(_cache.TryGet("missing", out _));
    }

    [Fact]
    public void SetBoth_SetsFullAndTrackOnly()
    {
        _cache.SetBoth("track|artist", "track|", "SoundCloud");
        Assert.Equal("SoundCloud", Get(_cache, "track|artist"));
        Assert.Equal("SoundCloud", Get(_cache, "track|"));
    }

    [Fact]
    public void HasSource_MatchesFull()
    {
        Set(_cache, "track|artist", "YouTube");
        Assert.True(_cache.HasSource("track|artist", "", "YouTube"));
    }

    [Fact]
    public void HasSource_MatchesTrackOnly()
    {
        Set(_cache, "track|", "YouTube");
        Assert.True(_cache.HasSource("", "track|", "YouTube"));
    }

    [Fact]
    public void HasSource_CaseInsensitive()
    {
        Set(_cache, "key", "YouTube");
        Assert.True(_cache.HasSource("key", "", "youtube"));
    }

    [Fact]
    public void HasSource_WrongSource_ReturnsFalse()
    {
        Set(_cache, "key", "Spotify");
        Assert.False(_cache.HasSource("key", "", "YouTube"));
    }

    [Fact]
    public void Save_And_Load_Persists()
    {
        Set(_cache, "track1", "YouTube");
        Set(_cache, "track2", "SoundCloud");
        _cache.Save();

        var loaded = new MediaSourceCache(_tempPath);
        loaded.Load();

        Assert.Equal("YouTube", Get(loaded, "track1"));
        Assert.Equal("SoundCloud", Get(loaded, "track2"));
    }

    [Fact]
    public void Save_OnlyWhenDirty()
    {
        _cache.Save();
        Assert.False(File.Exists(_tempPath));

        Set(_cache, "key", "value");
        _cache.Save();
        Assert.True(File.Exists(_tempPath));
    }

    [Fact]
    public void Clear_RemovesAll()
    {
        Set(_cache, "a", "1");
        Set(_cache, "b", "2");
        _cache.Clear();

        Assert.Equal(0, _cache.Count);
        Assert.Null(Get(_cache, "a"));
    }

    [Fact]
    public void Set_EmptyKey_Ignored()
    {
        Set(_cache, "", "YouTube");
        Set(_cache, null, "YouTube");
        Assert.Equal(0, _cache.Count);
    }

    [Fact]
    public void Set_EmptyValue_Ignored()
    {
        Set(_cache, "key", "");
        Set(_cache, "key", null);
        Assert.Equal(0, _cache.Count);
    }

    [Fact]
    public void Set_SameValue_DoesNotMarkDirty()
    {
        Set(_cache, "key", "YouTube");
        _cache.Save();

        File.Delete(_tempPath);

        Set(_cache, "key", "YouTube");
        _cache.Save();
        Assert.False(File.Exists(_tempPath));
    }
}
