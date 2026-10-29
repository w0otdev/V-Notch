using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using VNotch.Contracts;
using VNotch.Services;

namespace VNotch.Modules;

/// <summary>
/// Module host. The initialize/start/stop/tick/dispose transition rules are the native lifecycle
/// state machine (Native/Modules/ModuleLifecycle.cpp); this class wires the UI timer and hooks.
/// </summary>
#pragma warning disable S3881
public abstract partial class NotchModuleBase : INotchModule
#pragma warning restore S3881
{
    private const string LifecycleDll = "VNotch.Native.dll";

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnModuleLifecycle
    {
        public byte Initialized;
        public byte Running;
        public byte Disposed;
    }

    [LibraryImport(LifecycleDll, EntryPoint = "vn_module_begin_initialize")]
    private static partial int NativeBeginInitialize(ref VnModuleLifecycle state, out byte proceed);

    [LibraryImport(LifecycleDll, EntryPoint = "vn_module_complete_initialize")]
    private static partial int NativeCompleteInitialize(ref VnModuleLifecycle state);

    [LibraryImport(LifecycleDll, EntryPoint = "vn_module_plan_start")]
    private static partial int NativePlanStart(ref VnModuleLifecycle state, out byte needsInitialize, out byte proceed);

    [LibraryImport(LifecycleDll, EntryPoint = "vn_module_commit_start")]
    private static partial int NativeCommitStart(ref VnModuleLifecycle state);

    [LibraryImport(LifecycleDll, EntryPoint = "vn_module_fail_start")]
    private static partial int NativeFailStart(ref VnModuleLifecycle state);

    [LibraryImport(LifecycleDll, EntryPoint = "vn_module_begin_stop")]
    private static partial int NativeBeginStop(ref VnModuleLifecycle state, out byte proceed);

    [LibraryImport(LifecycleDll, EntryPoint = "vn_module_complete_stop")]
    private static partial int NativeCompleteStop(ref VnModuleLifecycle state);

    [LibraryImport(LifecycleDll, EntryPoint = "vn_module_can_tick")]
    private static partial int NativeCanTick(ref VnModuleLifecycle state, out byte canTick);

    [LibraryImport(LifecycleDll, EntryPoint = "vn_module_begin_dispose")]
    private static partial int NativeBeginDispose(ref VnModuleLifecycle state, out byte proceed);

    [LibraryImport(LifecycleDll, EntryPoint = "vn_module_complete_dispose")]
    private static partial int NativeCompleteDispose(ref VnModuleLifecycle state);

    private UiScheduledTimer? _timer;
    private VnModuleLifecycle _state;
    private int _ticking;

    public abstract string ModuleName { get; }

    public abstract TimeSpan? TickInterval { get; }

    public bool IsRunning => _state.Running != 0;

    public void Initialize()
    {
        if (NativeBeginInitialize(ref _state, out byte proceed) != 0 || proceed == 0) return;

        try
        {
            OnInitialize();

            if (TickInterval is TimeSpan interval)
            {
                _timer = new UiScheduledTimer
                {
                    Interval = interval
                };
                _timer.Tick += TimerOnTick;
            }

            NativeCompleteInitialize(ref _state);
        }
        catch (Exception ex)
        {
            RuntimeLog.Log($"MODULE-{ModuleName}", $"Initialize failed: {ex}");
        }
    }

    public void Start()
    {
        if (NativePlanStart(ref _state, out byte needsInitialize, out byte proceed) != 0) return;
        if (needsInitialize != 0) Initialize();
        if (proceed == 0) return;

        NativeCommitStart(ref _state);
        try
        {
            OnStart();

            if (IsRunning)
                TickSafe();

            if (IsRunning)
                _timer?.Start();
        }
        catch (Exception ex)
        {
            NativeFailStart(ref _state);
            try { OnStop(); } catch { /* Best-effort cleanup after failed start. */ }
            RuntimeLog.Log($"MODULE-{ModuleName}", $"Start failed: {ex}");
        }
    }

    public void Stop()
    {
        if (NativeBeginStop(ref _state, out byte proceed) != 0 || proceed == 0) return;

        try
        {
            _timer?.Stop();
            OnStop();
        }
        catch (Exception ex)
        {
            RuntimeLog.Log($"MODULE-{ModuleName}", $"Stop failed: {ex}");
        }
        finally
        {
            NativeCompleteStop(ref _state);
        }
    }

    public void Tick()
    {
        if (NativeCanTick(ref _state, out byte canTick) != 0 || canTick == 0) return;
        TickSafe();
    }

    private void TimerOnTick(object? sender, EventArgs e) => Tick();

    private void TickSafe()
    {
        try
        {
            if (System.Threading.Interlocked.CompareExchange(ref _ticking, 1, 0) != 0) return;
            try { OnTick(); } finally { System.Threading.Interlocked.Exchange(ref _ticking, 0); }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log($"MODULE-{ModuleName}", $"Tick failed: {ex}");
        }
    }

    public void Dispose()
    {
        if (NativeBeginDispose(ref _state, out byte proceed) != 0 || proceed == 0) return;

        try
        {
            Stop();

            if (_timer != null)
            {
                _timer.Tick -= TimerOnTick;
                _timer.Dispose();
                _timer = null;
            }

            OnDispose();
        }
        catch (Exception ex)
        {
            RuntimeLog.Log($"MODULE-{ModuleName}", $"Dispose failed: {ex}");
        }
        finally
        {
            NativeCompleteDispose(ref _state);
            GC.SuppressFinalize(this);
        }
    }

    protected virtual void OnInitialize() { /* Optional lifecycle hook. */ }

    protected virtual void OnStart() { /* Optional lifecycle hook. */ }

    protected virtual void OnStop() { /* Optional lifecycle hook. */ }

    protected abstract void OnTick();

    protected virtual void OnDispose() { /* Optional lifecycle hook. */ }
}
