using Windows.Media.Control;

namespace VNotch.Services;

internal readonly record struct MediaTransportControls(bool IsNextEnabled, bool IsPreviousEnabled);
internal sealed record MediaTransportTimeline(TimeSpan StartTime, TimeSpan EndTime, TimeSpan Position);

internal interface IMediaTransportSession
{
    object Identity { get; }
    string SourceAppUserModelId { get; }
    MediaTransportControls? GetControls();
    MediaTransportTimeline? GetTimelineProperties();
    Task<bool> TryTogglePlayPauseAsync();
    Task<bool> TrySkipNextAsync();
    Task<bool> TrySkipPreviousAsync();
    Task<bool> TryChangePlaybackPositionAsync(long ticks);
    Task<string?> GetTitleAsync();
}

internal sealed class WindowsMediaTransportSession(GlobalSystemMediaTransportControlsSession session) : IMediaTransportSession
{
    public object Identity => session;
    public string SourceAppUserModelId => session.SourceAppUserModelId;
    public MediaTransportControls? GetControls()
    {
        var controls = session.GetPlaybackInfo()?.Controls;
        return controls == null ? null : new MediaTransportControls(controls.IsNextEnabled, controls.IsPreviousEnabled);
    }
    public MediaTransportTimeline? GetTimelineProperties()
    {
        var timeline = session.GetTimelineProperties();
        return timeline == null ? null : new MediaTransportTimeline(timeline.StartTime, timeline.EndTime, timeline.Position);
    }
    public async Task<bool> TryTogglePlayPauseAsync() => await session.TryTogglePlayPauseAsync();
    public async Task<bool> TrySkipNextAsync() => await session.TrySkipNextAsync();
    public async Task<bool> TrySkipPreviousAsync() => await session.TrySkipPreviousAsync();
    public async Task<bool> TryChangePlaybackPositionAsync(long ticks) => await session.TryChangePlaybackPositionAsync(ticks);
    public async Task<string?> GetTitleAsync() => (await session.TryGetMediaPropertiesAsync())?.Title;
}
