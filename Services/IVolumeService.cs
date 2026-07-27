namespace VNotch.Services;

public interface IVolumeService : IDisposable
{

    bool IsAvailable { get; }

    bool RefreshDefaultDevice();

    float GetVolume();

    bool SetVolume(float volume);

    bool GetMute();

    void SetMute(bool mute);

    void ToggleMute();
}
