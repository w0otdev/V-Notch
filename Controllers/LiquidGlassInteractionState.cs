using System;

namespace VNotch.Controllers;

internal readonly record struct GlassInteractionFrame(
    double PointerX, double PointerY, double Active, double Press, double LightX, double LightY);

internal sealed class LiquidGlassInteractionState
{
    private const double RestLightX = 0.5;
    private const double RestLightY = -0.08;
    private const double PositionTolerance = 0.0001;
    private const double VelocityTolerance = 0.001;
    private double _targetX = 0.5, _targetY = 0.5;
    private double _x = 0.5, _y = 0.5, _velocityX, _velocityY;
    private double _active, _activeTarget, _activeVelocity;
    private double _press, _pressTarget, _pressVelocity;
    private double _lightX = RestLightX, _lightY = RestLightY, _lightVelocityX, _lightVelocityY;

    public GlassInteractionFrame Frame => new(_x, _y, _active, _press, _lightX, _lightY);
    public bool IsSettled =>
        Settled(_x, _velocityX, _targetX) && Settled(_y, _velocityY, _targetY) &&
        Settled(_active, _activeVelocity, _activeTarget) && Settled(_press, _pressVelocity, _pressTarget) &&
        Settled(_lightX, _lightVelocityX, LightTargetX) && Settled(_lightY, _lightVelocityY, LightTargetY);
    private double LightTargetX => RestLightX + (_x - RestLightX) * _press;
    private double LightTargetY => RestLightY + (_y - RestLightY) * _press;

    public void MovePointer(double x, double y)
    {
        _targetX = Math.Clamp(x, 0, 1);
        _targetY = Math.Clamp(y, 0, 1);
        _activeTarget = 1;
    }

    public void SetActive(bool active) => _activeTarget = active ? 1 : 0;

    public void SetPressed(bool pressed)
    {
        _pressTarget = pressed ? 1 : 0;
        if (pressed) _press = 0.7;
    }

    public void Advance(double deltaTime)
    {
        deltaTime = Math.Clamp(deltaTime, 0, 1.0 / 20);
        Spring(ref _x, ref _velocityX, _targetX, 150, 18, deltaTime);
        Spring(ref _y, ref _velocityY, _targetY, 150, 18, deltaTime);
        Spring(ref _active, ref _activeVelocity, _activeTarget, 95, 15, deltaTime);
        Spring(ref _press, ref _pressVelocity, _pressTarget, 210, 18, deltaTime);
        Spring(ref _lightX, ref _lightVelocityX, LightTargetX, 75, 14, deltaTime);
        Spring(ref _lightY, ref _lightVelocityY, LightTargetY, 75, 14, deltaTime);
        if (IsSettled) SnapToTargets();
    }

    public void SnapToTargets()
    {
        _x = _targetX;
        _y = _targetY;
        _active = _activeTarget;
        _press = _pressTarget;
        _lightX = LightTargetX;
        _lightY = LightTargetY;
        _velocityX = _velocityY = _activeVelocity = _pressVelocity = _lightVelocityX = _lightVelocityY = 0;
    }

    private static bool Settled(double value, double velocity, double target) =>
        Math.Abs(value - target) <= PositionTolerance && Math.Abs(velocity) <= VelocityTolerance;

    private static void Spring(ref double value, ref double velocity, double target,
        double stiffness, double damping, double deltaTime)
    {
        velocity += (target - value) * stiffness * deltaTime;
        velocity *= Math.Exp(-damping * deltaTime);
        value += velocity * deltaTime;
    }
}
