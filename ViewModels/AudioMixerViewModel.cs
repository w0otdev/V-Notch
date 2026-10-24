using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VNotch.Services;

namespace VNotch.ViewModels;

public partial class AudioMixerViewModel : ObservableObject
{
    private readonly IVolumeService _volumeService;
    [ObservableProperty]
    private float _currentVolume = 0.5f;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private string _iconText = "\uE995";

    [ObservableProperty]
    private bool _isSystemExpanded = true;

    [ObservableProperty]
    private bool _isApplicationsExpanded = true;

    [ObservableProperty]
    private AudioMixerSnapshot? _snapshot;

    public AudioMixerViewModel(IVolumeService volumeService) => _volumeService = volumeService;

    [RelayCommand]
    private void ToggleMute()
    {
        if (!_volumeService.IsAvailable) return;
        _volumeService.ToggleMute();
        SyncFromSystem();
    }

    public void SetVolume(float ratio)
    {
        CurrentVolume = Math.Clamp(ratio, 0, 1);
        UpdateIcon(CurrentVolume, false);
        if (_volumeService.IsAvailable) _volumeService.SetVolume(CurrentVolume);
    }

    public void SyncFromSystem()
    {
        if (!_volumeService.IsAvailable) return;
        UpdateState(_volumeService.GetVolume(), _volumeService.GetMute());
    }

    public void UpdateState(float volume, bool muted)
    {
        CurrentVolume = Math.Clamp(volume, 0, 1);
        IsMuted = muted;
        UpdateIcon(CurrentVolume, IsMuted);
    }

    private void UpdateIcon(float volume, bool muted)
    {
        vn_audio_mixer_select_icon_glyph(volume, muted ? (byte)1 : (byte)0, out int codepoint);
        IconText = ((char)codepoint).ToString();
    }

    [System.Runtime.InteropServices.LibraryImport("VNotch.Native.dll")]
    private static partial int vn_audio_mixer_select_icon_glyph(float volume, byte muted, out int out_codepoint);

    public void CacheSessionVolume(uint processId, float volume)
    {
        var session = Snapshot?.Sessions.Find(x => x.ProcessId == processId);
        if (session == null) return;
        session.Volume = Math.Clamp(volume, 0, 1);
        if (session.Volume > 0.0001f) session.IsMuted = false;
    }
}

public sealed class AudioMixerSnapshot
{
    public List<AudioDeviceInfo> Output { get; init; } = new();
    public List<AudioDeviceInfo> Input { get; init; } = new();
    public List<AudioSessionInfo> Sessions { get; init; } = new();
    public float Master { get; set; } = 0.5f;
    public float Capture { get; set; } = 0.5f;

    public string StructureKey
    {
        get
        {
            var apps = string.Join(",", Sessions.Where(x => !x.IsSystemSounds)
                .Select(x => x.ProcessId).OrderBy(x => x));
            string outputId = Output.Find(x => x.IsDefault)?.Id ?? "";
            string inputId = Input.Find(x => x.IsDefault)?.Id ?? "";
            return $"{apps}|{outputId}|{inputId}|{Output.Count}|{Input.Count}";
        }
    }

    public string GetDefaultOutputName(string? fallback = null) =>
        GetDefaultName(Output, fallback ?? Loc.Get("audio.speakers"));

    public string GetDefaultInputName(string? fallback = null) =>
        GetDefaultName(Input, fallback ?? Loc.Get("audio.microphone"));

    private static string GetDefaultName(List<AudioDeviceInfo> devices, string fallback) =>
        devices.Find(x => x.IsDefault)?.FriendlyName ??
        (devices.Count > 0 ? devices[0].FriendlyName : fallback);
}
