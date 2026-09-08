using System;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VNotch.Services;

internal sealed class ProgressSpringRenderer
{
    #region Tuning

    private const double SpringStiffness = 105.0;
    private const double SpringDamping = 28.0;
    private const double SpringSettleThreshold = 0.0012;
    private const double SpringTargetFollowSpeed = 30.0;
    private const double SpringMaxVelocity = 2.4;
    private const double SpringMaxStepPerFrame = 0.030;
    private const int SpringSettleFramesRequired = 3;
    private const int SpringTimeoutMs = 1400;

    #endregion

    #region Collaborators & state

    private readonly Action<double> _applyRatio;
    private readonly Func<bool> _shouldRender;
    private readonly Func<double> _getPlaybackRate;

    private readonly Stopwatch _stopwatch = new();
    private readonly TranslateTransform _fpsBoostTarget = new();
    private DoubleAnimation? _fpsBoostAnim;

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

    public double DisplayRatio { get; set; }

    public double TargetRatio { get; set; }

    public double SpringTargetRatio { get; set; }

    public double Velocity { get; set; }

    public int SettleFrames { get; set; }

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

        if (_fpsBoostAnim == null)
        {
            _fpsBoostAnim = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            Timeline.SetDesiredFrameRate(_fpsBoostAnim, VNotch.Services.AnimationConfig.TargetFps);
            _fpsBoostAnim.Freeze();
        }

        _fpsBoostTarget.BeginAnimation(TranslateTransform.XProperty, _fpsBoostAnim);
        CompositionTarget.Rendering += OnRendering;
    }
    public void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        _stopwatch.Stop();

        CompositionTarget.Rendering -= OnRendering;
        _fpsBoostTarget.BeginAnimation(TranslateTransform.XProperty, null);
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
            return;
        }

        RenderFrame();
    }

    private void RenderFrame()
    {
        double dt = _stopwatch.Elapsed.TotalSeconds;
        _stopwatch.Restart();
        dt = Math.Clamp(dt, 0.001, 0.033);

        double rate = _getPlaybackRate();
        if (double.IsNaN(rate) || double.IsInfinity(rate) || rate <= 0)
        {
            rate = 1.0;
        }
        rate = Math.Clamp(rate, 0.5, 3.0);

        double scaledDt = dt * rate;

        double targetFollow = 1.0 - Math.Exp(-SpringTargetFollowSpeed * scaledDt);
        SpringTargetRatio += (TargetRatio - SpringTargetRatio) * targetFollow;

        double error = SpringTargetRatio - DisplayRatio;

        double springForce = SpringStiffness * error - SpringDamping * Velocity;
        Velocity += springForce * scaledDt;
        Velocity = Math.Clamp(Velocity, -SpringMaxVelocity, SpringMaxVelocity);

        double prevDisplay = DisplayRatio;
        DisplayRatio += Velocity * scaledDt;

        double step = DisplayRatio - prevDisplay;
        if (Math.Abs(step) > SpringMaxStepPerFrame)
        {
            DisplayRatio = prevDisplay + Math.Sign(step) * SpringMaxStepPerFrame;
        }

        if ((prevDisplay - SpringTargetRatio) * (DisplayRatio - SpringTargetRatio) < 0)
        {
            DisplayRatio = SpringTargetRatio;
            Velocity = 0;
        }

        if (Math.Abs(TargetRatio - DisplayRatio) < SpringSettleThreshold &&
            Math.Abs(Velocity) < 0.004)
        {
            SettleFrames++;
        }
        else
        {
            SettleFrames = 0;
        }

        if (SettleFrames >= SpringSettleFramesRequired)
        {
            DisplayRatio = TargetRatio;
            Velocity = 0;
            SettleFrames = 0;
            _active = false;
            Unhook();
        }

        if ((DateTime.UtcNow - _startTimeUtc).TotalMilliseconds > SpringTimeoutMs)
        {
            DisplayRatio = TargetRatio;
            Velocity = 0;
            SettleFrames = 0;
            _active = false;
            Unhook();
        }

        DisplayRatio = Math.Clamp(DisplayRatio, 0, 1);
        _applyRatio(DisplayRatio);
    }

    #endregion
}
