using System;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using VNotch.Models;
using VNotch.Services;
using Windows.Storage.Streams;

namespace VNotch.Tests.Fakes;

public sealed class FakeDispatcherService : IDispatcherService
{
    public void BeginInvoke(Action action) => action();
    public void Invoke(Action action) => action();
    public bool CheckAccess() => true;
}

public sealed class FakeMediaDetectionService : IMediaDetectionService
{
    public IMediaArtworkService ArtworkService { get; set; } = new FakeMediaArtworkService();
    public bool KeepPinnedOnTrackChange { get; set; }
    public string? PinnedSessionKey { get; private set; }
    public bool IsSessionPinned(string? sessionKey) => !string.IsNullOrEmpty(sessionKey) && sessionKey == PinnedSessionKey;
    public bool ToggleSessionPin(MediaInfo info)
    {
        if (string.IsNullOrEmpty(info.SessionInstanceKey)) return false;
        PinnedSessionKey = IsSessionPinned(info.SessionInstanceKey) ? null : info.SessionInstanceKey;
        return true;
    }
    public Task<YouTubeLookupResult?> TryGetYouTubeVideoIdWithInfoAsync(string title, string artist = "", CancellationToken ct = default) =>
        Task.FromResult<YouTubeLookupResult?>(null);
    public event EventHandler<MediaInfo>? MediaChanged;

    public int StartCount { get; private set; }
    public int DisposeCount { get; private set; }
    public TimeSpan? LastSeekAbsolute { get; private set; }
    public TimeSpan? LastSeek { get; private set; }
    public int PlayPauseCount { get; private set; }
    public int NextTrackCount { get; private set; }
    public int PreviousTrackCount { get; private set; }
    public Func<(bool Success, float Volume, bool Muted)>? ReadSessionVolume { get; set; }
    public float? LastSessionVolume { get; private set; }
    public bool SessionMuteToggleSucceeded { get; set; }
    public int SessionMuteToggleCount { get; private set; }

    public void RaiseMediaChanged(MediaInfo info) => MediaChanged?.Invoke(this, info);

    public void Start() => StartCount++;
    public void Stop() { }

    public Task PlayPauseAsync() { PlayPauseCount++; return Task.CompletedTask; }
    public Task NextTrackAsync() { NextTrackCount++; return Task.CompletedTask; }
    public Task PreviousTrackAsync() { PreviousTrackCount++; return Task.CompletedTask; }

    public Task SeekAsync(TimeSpan position)
    {
        LastSeek = position;
        return Task.CompletedTask;
    }

    public Task SeekRelativeAsync(double seconds) => Task.CompletedTask;

    public Task SeekToAbsoluteAsync(TimeSpan position)
    {
        LastSeekAbsolute = position;
        return Task.CompletedTask;
    }

    public bool TryGetCurrentSessionVolume(out float volume, out bool isMuted)
    {
        if (ReadSessionVolume != null)
        {
            var result = ReadSessionVolume();
            volume = result.Volume;
            isMuted = result.Muted;
            return result.Success;
        }
        volume = 0f;
        isMuted = false;
        return false;
    }

    public bool TrySetCurrentSessionVolume(float volume) { LastSessionVolume = volume; return ReadSessionVolume != null; }
    public bool TryToggleCurrentSessionMute() { SessionMuteToggleCount++; return SessionMuteToggleSucceeded; }
    public void InvalidateVolumeSessionCache() { }

    public void Dispose() => DisposeCount++;
}

public sealed class FakeMediaArtworkService : IMediaArtworkService
{
    public bool SmartCropEnabled { get; private set; }
    public Task<BitmapImage?> DownloadImageAsync(string url, CancellationToken ct = default) => Task.FromResult<BitmapImage?>(null);
    public BitmapSource? CropToSquare(BitmapSource source, string mediaSource, bool forceCenterCrop = false) => source;
    public Task<BitmapImage?> ConvertToWpfBitmapAsync(IRandomAccessStreamWithContentType stream, CancellationToken ct = default) => Task.FromResult<BitmapImage?>(null);
    public void ConfigureSmartCrop(bool enabled) => SmartCropEnabled = enabled;
    public SubjectBounds? GetDominantSubjectBounds(BitmapSource source) => null;
}

public sealed class FakeSettingsService : ISettingsService
{
    private NotchSettings _settings;

    public FakeSettingsService(NotchSettings? settings = null) => _settings = settings ?? new NotchSettings();

    public NotchSettings? LastSaved { get; private set; }

    public NotchSettings Load() => _settings;

    public void Save(NotchSettings settings)
    {
        _settings = settings;
        LastSaved = settings;
    }

    public Task SaveAsync(NotchSettings settings)
    {
        Save(settings);
        return Task.CompletedTask;
    }

    public void ExportSettingsToFile(string filePath, NotchSettings settings)
    {
        _settings = settings;
        LastSaved = settings;
    }

    public (NotchSettings Settings, bool RequiresRestart) ImportSettingsFromFile(string filePath, NotchSettings? currentSettings = null)
    {
        return (_settings, false);
    }
}

public sealed class FakeVolumeService : IVolumeService
{
    private float _volume;
    private bool _muted;

    public FakeVolumeService(bool isAvailable = true, float initialVolume = 0.5f, bool muted = false)
    {
        IsAvailable = isAvailable;
        _volume = initialVolume;
        _muted = muted;
    }

    public bool IsAvailable { get; }
    public float? LastSetVolume { get; private set; }
    public int ToggleMuteCount { get; private set; }

    public bool RefreshDefaultDevice() => IsAvailable;

    public float GetVolume() => _volume;

    public bool SetVolume(float volume)
    {
        _volume = volume;
        LastSetVolume = volume;
        return true;
    }

    public bool GetMute() => _muted;
    public void SetMute(bool mute) => _muted = mute;

    public void ToggleMute()
    {
        _muted = !_muted;
        ToggleMuteCount++;
    }

    public void Dispose() { }
}

public sealed class FakeBatteryService : IBatteryService
{
    private readonly BatteryInfo _info;

    public FakeBatteryService(BatteryInfo? info = null) => _info = info ?? new BatteryInfo();

    public BatteryInfo GetBatteryInfo() => _info;
}
