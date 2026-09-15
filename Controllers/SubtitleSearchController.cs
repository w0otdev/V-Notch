using VNotch.Services;

namespace VNotch.Controllers;

internal sealed class SubtitleSearchController
{
    private int _generation;

    public void Invalidate() => ++_generation;

    public async Task SearchAsync(
        Func<Task<string>> resolveVideoId,
        Func<string, Task<List<LyricLine>?>> fetchSubtitles,
        Action<string> onSearchStarted,
        Action<List<LyricLine>?> onCompleted)
    {
        int generation = ++_generation;
        string videoId = await resolveVideoId();
        if (generation != _generation) return;

        // Waiting for browser metadata is not yet a subtitle search. Keep the
        // current widget until we have a video to fetch, including late IDs.
        if (string.IsNullOrEmpty(videoId))
        {
            onCompleted(null);
            return;
        }

        onSearchStarted(videoId);
        var subtitles = await fetchSubtitles(videoId);
        if (generation != _generation) return;

        onCompleted(subtitles);
    }
}
