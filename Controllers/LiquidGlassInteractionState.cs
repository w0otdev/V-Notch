using System;
using System.Runtime.InteropServices;

namespace VNotch.Controllers;

internal readonly record struct GlassInteractionFrame(
    double PointerX, double PointerY, double Active, double Press, double LightX, double LightY);

internal sealed partial class LiquidGlassInteractionState
{
    private const double RestLightX = 0.5;
    private const double RestLightY = -0.08;
    private const double PositionTolerance = 0.0001;
    private const double VelocityTolerance = 0.001;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct NativeState
    {
        public double TargetX;
        public double TargetY;
        public double X;
        public double Y;
        public double VelocityX;
        public double VelocityY;
        public double Active;
        public double ActiveTarget;
        public double ActiveVelocity;
        public double Press;
        public double PressTarget;
        public double PressVelocity;
        public double LightX;
        public double LightY;
        public double LightVelocityX;
        public double LightVelocityY;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_liquid_glass_interaction_advance(
        ref NativeState state, double deltaTime, out byte outIsSettled);

    private NativeState _state = new()
    {
        TargetX = 0.5,
        TargetY = 0.5,
        X = 0.5,
        Y = 0.5,
        LightX = RestLightX,
        LightY = RestLightY
    };

    public GlassInteractionFrame Frame => new(_state.X, _state.Y, _state.Active, _state.Press, _state.LightX, _state.LightY);
    public bool IsSettled =>
        Settled(_state.X, _state.VelocityX, _state.TargetX) && Settled(_state.Y, _state.VelocityY, _state.TargetY) &&
        Settled(_state.Active, _state.ActiveVelocity, _state.ActiveTarget) && Settled(_state.Press, _state.PressVelocity, _state.PressTarget) &&
        Settled(_state.LightX, _state.LightVelocityX, LightTargetX) && Settled(_state.LightY, _state.LightVelocityY, LightTargetY);
    private double LightTargetX => RestLightX + (_state.X - RestLightX) * _state.Press;
    private double LightTargetY => RestLightY + (_state.Y - RestLightY) * _state.Press;

    public void MovePointer(double x, double y)
    {
        _state.TargetX = Math.Clamp(x, 0, 1);
        _state.TargetY = Math.Clamp(y, 0, 1);
        _state.ActiveTarget = 1;
    }

    public void SetActive(bool active) => _state.ActiveTarget = active ? 1 : 0;

    public void SetPressed(bool pressed)
    {
        _state.PressTarget = pressed ? 1 : 0;
        if (pressed) _state.Press = 0.7;
    }

    public void Advance(double deltaTime)
    {
        _ = vn_liquid_glass_interaction_advance(ref _state, deltaTime, out _);
    }

    public void SnapToTargets()
    {
        _state.X = _state.TargetX;
        _state.Y = _state.TargetY;
        _state.Active = _state.ActiveTarget;
        _state.Press = _state.PressTarget;
        _state.LightX = LightTargetX;
        _state.LightY = LightTargetY;
        _state.VelocityX = _state.VelocityY = _state.ActiveVelocity = _state.PressVelocity = _state.LightVelocityX = _state.LightVelocityY = 0;
    }

    private static bool Settled(double value, double velocity, double target) =>
        Math.Abs(value - target) <= PositionTolerance && Math.Abs(velocity) <= VelocityTolerance;
}
