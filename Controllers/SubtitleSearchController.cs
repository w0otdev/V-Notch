using VNotch.Services;

namespace VNotch.Controllers;

internal sealed class SubtitleSearchController
{
    private int _generation;
    private string? _activeSearchKey;

    public void Invalidate()
    {
        ++_generation;
        _activeSearchKey = null;
    }

    public async Task SearchAsync(
        Func<Task<string>> resolveVideoId,
        Func<string, Task<List<LyricLine>?>> fetchSubtitles,
        Action<string> onSearchStarted,
        Action<List<LyricLine>?> onCompleted,
        string? searchKey = null)
    {
        if (searchKey != null && searchKey == _activeSearchKey) return;
        int generation = ++_generation;
        _activeSearchKey = searchKey;
        try
        {
            string videoId = await resolveVideoId();
            if (generation != _generation) return;

            // Waiting for browser metadata is not yet a subtitle search. Keep the
            // current widget until we have a video to fetch, including late IDs.
            if (string.IsNullOrEmpty(videoId))
            {
                onCompleted(null);
                return;
            }

            // Use the resolved identity for subsequent media updates with a late video ID.
            if (searchKey != null) _activeSearchKey = $"yt:{videoId}";
            onSearchStarted(videoId);
            var subtitles = await fetchSubtitles(videoId);
            if (generation != _generation) return;

            onCompleted(subtitles);
        }
        finally
        {
            if (generation == _generation) _activeSearchKey = null;
        }
    }
}
