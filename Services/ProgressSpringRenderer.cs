using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VNotch.Services;

/// <summary>
/// Frame scheduling for the progress bar spring. The integration step and its tuning live in
/// Native/Services/AnimationMath.cpp; this class owns the Rendering hook and applies the ratio.
/// </summary>
internal sealed partial class ProgressSpringRenderer
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnProgressSpringState
    {
        public double DisplayRatio;
        public double TargetRatio;
        public double SpringTargetRatio;
        public double Velocity;
        public int SettleFrames;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_progress_spring_step(
        ref VnProgressSpringState state, double frameSeconds, double playbackRate, double elapsedMs, out byte finished);

    private VnProgressSpringState _state;

    #region Collaborators & state

    private readonly Action<double> _applyRatio;
    private readonly Func<bool> _shouldRender;
    private readonly Func<double> _getPlaybackRate;

    private readonly Stopwatch _stopwatch = new();

    private bool _hooked;
    private bool _active;
    private DateTime _startTimeUtc = DateTime.MinValue;

    #endregion

    public ProgressSpringRenderer(
        Action<double> applyRatio,
        Func<bool> shouldRender,
        Func<double> getPlaybackRate)
    {
        _applyRatio = applyRatio ?? throw new ArgumentNullException(nameof(applyRatio));
        _shouldRender = shouldRender ?? throw new ArgumentNullException(nameof(shouldRender));
        _getPlaybackRate = getPlaybackRate ?? throw new ArgumentNullException(nameof(getPlaybackRate));
    }

    #region Public state

    public bool IsActive => _active;

    public bool IsHooked => _hooked;

    public double DisplayRatio { get => _state.DisplayRatio; set => _state.DisplayRatio = value; }

    public double TargetRatio { get => _state.TargetRatio; set => _state.TargetRatio = value; }

    public double SpringTargetRatio { get => _state.SpringTargetRatio; set => _state.SpringTargetRatio = value; }

    public double Velocity { get => _state.Velocity; set => _state.Velocity = value; }

    public int SettleFrames { get => _state.SettleFrames; set => _state.SettleFrames = value; }

    #endregion

    #region Public API
    public void Start()
    {
        _active = true;
        _startTimeUtc = DateTime.UtcNow;
        Hook();
    }
    public void Stop()
    {
        _active = false;
        SettleFrames = 0;
        Velocity = 0;
        Unhook();
    }
    public void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        _stopwatch.Restart();

        CompositionTarget.Rendering += OnRendering;
    }
    public void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        _stopwatch.Stop();

        CompositionTarget.Rendering -= OnRendering;
    }

    #endregion

    #region Render loop

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_active)
        {
            Stop();
            return;
        }

        if (!_shouldRender())
        {
            DisplayRatio = TargetRatio;
            Stop();
            return;
        }

        RenderFrame();
    }

    private void RenderFrame()
    {
        double frameSeconds = _stopwatch.Elapsed.TotalSeconds;
        _stopwatch.Restart();

        double elapsedMs = (DateTime.UtcNow - _startTimeUtc).TotalMilliseconds;
        if (vn_progress_spring_step(ref _state, frameSeconds, _getPlaybackRate(), elapsedMs, out byte finished) != 0)
            return;

        if (finished != 0)
        {
            _active = false;
            Unhook();
        }

        _applyRatio(_state.DisplayRatio);
    }
    #endregion
}
