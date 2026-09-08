using System.Runtime.InteropServices;

namespace VNotch.Services;

public sealed class VolumeService : IVolumeService
{
    private const string LogCategory = "VOLUME";
    private readonly object _sync = new();
    private IAudioEndpointVolume? _endpointVolume;
    private bool _isInitialized;
    private bool _disposed;

    public bool IsAvailable
    {
        get
        {
            lock (_sync)
            {
                return EnsureInitializedLocked();
            }
        }
    }

    public VolumeService()
    {
        RefreshDefaultDevice();
    }

    public bool RefreshDefaultDevice()
    {
        lock (_sync)
        {
            return InitializeLocked();
        }
    }

    private bool InitializeLocked()
    {
        if (_disposed) return false;

        ReleaseEndpointLocked();
        IMMDeviceEnumerator? deviceEnumerator = null;
        IMMDevice? device = null;

        try
        {
            var deviceEnumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            if (deviceEnumeratorType == null) return false;

            deviceEnumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(deviceEnumeratorType);
            if (deviceEnumerator == null) return false;

            int hr = deviceEnumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device);
            if (hr != 0 || device == null) return false;

            var iidAudioEndpointVolume = typeof(IAudioEndpointVolume).GUID;
            hr = device.Activate(ref iidAudioEndpointVolume, (uint)ClsContexts.All, IntPtr.Zero, out var endpointVolume);
            if (hr != 0 || endpointVolume == null) return false;

            _endpointVolume = (IAudioEndpointVolume)endpointVolume;
            _isInitialized = true;

            RuntimeLog.Log(LogCategory, "Initialized successfully");
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(LogCategory, $"Init error: {ex.Message}");
            _isInitialized = false;
            return false;
        }
        finally
        {
            ReleaseComObject(device);
            ReleaseComObject(deviceEnumerator);
        }
    }

    public float GetVolume()
    {
        lock (_sync)
        {
            if (!EnsureInitializedLocked()) return 0.5f;

            try
            {
                int hr = _endpointVolume!.GetMasterVolumeLevelScalar(out float level);
                if (hr == 0)
                {
                    return level;
                }

                RuntimeLog.Log(LogCategory, $"GetVolume failed with HRESULT: 0x{hr:X8}");
            }
            catch (Exception ex)
            {
                RuntimeLog.Log(LogCategory, $"GetVolume error: {ex.Message}");
            }

            _isInitialized = false;
            return 0.5f;
        }
    }

    public bool SetVolume(float volume)
    {
        lock (_sync)
        {
            if (!EnsureInitializedLocked()) return false;

            try
            {
                volume = Math.Clamp(volume, 0f, 1f);
                int hr = _endpointVolume!.SetMasterVolumeLevelScalar(volume, Guid.Empty);

                if (hr == 0)
                {
                    return true;
                }

                RuntimeLog.Log(LogCategory, $"SetVolume failed with HRESULT: 0x{hr:X8}");
            }
            catch (Exception ex)
            {
                RuntimeLog.Log(LogCategory, $"SetVolume error: {ex.Message}");
            }

            _isInitialized = false;
            return false;
        }
    }

    public bool GetMute()
    {
        lock (_sync)
        {
            if (!EnsureInitializedLocked()) return false;

            try
            {
                int hr = _endpointVolume!.GetMute(out bool mute);
                if (hr == 0) return mute;
                RuntimeLog.Log(LogCategory, $"GetMute failed with HRESULT: 0x{hr:X8}");
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("VOLUME-GETMUTE", ex.ToString());
            }

            _isInitialized = false;
            return false;
        }
    }

    public void SetMute(bool mute)
    {
        lock (_sync)
        {
            if (!EnsureInitializedLocked()) return;

            try
            {
                int hr = _endpointVolume!.SetMute(mute, Guid.Empty);
                if (hr != 0)
                {
                    RuntimeLog.Log(LogCategory, $"SetMute failed with HRESULT: 0x{hr:X8}");
                    _isInitialized = false;
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("VOLUME-SETMUTE", ex.ToString());
                _isInitialized = false;
            }
        }
    }

    public void ToggleMute()
    {
        SetMute(!GetMute());
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseEndpointLocked();
        }
    }

    private bool EnsureInitializedLocked()
    {
        return (!_disposed && _isInitialized && _endpointVolume != null)
            || InitializeLocked();
    }

    private void ReleaseEndpointLocked()
    {
        ReleaseComObject(_endpointVolume);
        _endpointVolume = null;
        _isInitialized = false;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value == null || !Marshal.IsComObject(value)) return;
        try
        {
            Marshal.ReleaseComObject(value);
        }
        catch (Exception)
        {
            // COM object may already be released or invalid; safe to ignore during cleanup.
        }
    }

    #region COM Enums

    private enum EDataFlow
    {
        eRender = 0,
        eCapture = 1,
        eAll = 2
    }

    private enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }

    [Flags]
    private enum ClsContexts : uint
    {
        InprocServer = 0x1,
        InprocHandler = 0x2,
        LocalServer = 0x4,
        RemoteServer = 0x10,
        All = InprocServer | InprocHandler | LocalServer | RemoteServer
    }

    #endregion

    #region COM Interfaces

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IntPtr ppDevices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? ppDevice);

        [PreserveSig]
        int GetDevice(string pwstrId, out IntPtr ppDevice);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr pClient);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? ppInterface);

        [PreserveSig]
        int OpenPropertyStore(uint stgmAccess, out IntPtr ppProperties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);

        [PreserveSig]
        int GetState(out uint pdwState);
    }

    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IntPtr pNotify);

        [PreserveSig]
        int UnregisterControlChangeNotify(IntPtr pNotify);

        [PreserveSig]
        int GetChannelCount(out uint pnChannelCount);

        [PreserveSig]
        int SetMasterVolumeLevel(float fLevelDB, Guid pguidEventContext);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float fLevel, Guid pguidEventContext);

        [PreserveSig]
        int GetMasterVolumeLevel(out float pfLevelDB);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float pfLevel);

        [PreserveSig]
        int SetChannelVolumeLevel(uint nChannel, float fLevelDB, Guid pguidEventContext);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, Guid pguidEventContext);

        [PreserveSig]
        int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, Guid pguidEventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);

        [PreserveSig]
        int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);

        [PreserveSig]
        int VolumeStepUp(Guid pguidEventContext);

        [PreserveSig]
        int VolumeStepDown(Guid pguidEventContext);

        [PreserveSig]
        int QueryHardwareSupport(out uint pdwHardwareSupportMask);

        [PreserveSig]
        int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
    }

    #endregion
}
