using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Presenters;

public sealed class NotchShellViewRefs
{
    public required Border NotchBorder { get; init; }
    public FrameworkElement? NotchContainer { get; init; }
    public Action<double, TimeSpan>? AnimateCornerRadius { get; init; }
    public Func<double, CornerRadius>? CornerRadiusBuilder { get; init; }
    public Func<long, bool>? IsSessionCurrent { get; init; }
}

/// <summary>
/// Quản lý hình học, kích thước và bo góc của Notch Shell.
/// Độc quyền sở hữu Width, Height, CornerRadius của NotchBorder (Workstream D - implement.md).
/// </summary>
public sealed class NotchShellPresenter : IDisposable
{
    private const string LogTag = "NOTCH-SHELL-PRESENTER";

    private readonly NotchShellViewRefs _refs;
    private long _activeSessionId;
    private long _animationVersion;
    private bool _disposed;

    public NotchShellPresenter(NotchShellViewRefs refs)
    {
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
    }

    public void AnimateShell(TransitionPlan plan, Action<TransitionExecutionResult> onCompleted)
    {
        if (_disposed)
        {
            onCompleted(new TransitionExecutionResult(plan.SessionId, TransitionExecutionStatus.Canceled, "Presenter disposed"));
            return;
        }

        long sessionId = plan.SessionId;
        _activeSessionId = sessionId;
        long version = ++_animationVersion;
        if (!IsCurrent(sessionId, version)) return;

        var border = _refs.NotchBorder;

        // Quy tắc ngắt animation D3 (implement.md):
        // 1. Ghi nhận giá trị hình ảnh hiện tại
        double currentWidth = ReadExtent(border.Width, border.ActualWidth, plan.TargetWidth);
        double currentHeight = ReadExtent(border.Height, border.ActualHeight, plan.TargetHeight);

        // 2 & 3. Dừng animation clock mà phiên cũ sở hữu
        border.BeginAnimation(FrameworkElement.WidthProperty, null);
        border.BeginAnimation(FrameworkElement.HeightProperty, null);

        // 4. Đặt base value cần thiết để không bật về giá trị cũ
        border.Width = currentWidth;
        border.Height = currentHeight;

        // Xử lý bo góc thông qua delegate hoặc builder
        if (_refs.AnimateCornerRadius != null)
        {
            _refs.AnimateCornerRadius(plan.TargetCornerRadius, plan.Motion.Duration.TimeSpan);
        }
        else if (_refs.CornerRadiusBuilder != null)
        {
            border.CornerRadius = _refs.CornerRadiusBuilder(plan.TargetCornerRadius);
        }
        else
        {
            border.CornerRadius = new CornerRadius(plan.TargetCornerRadius);
        }

        // ReduceMotion hoặc duration = 0: hoàn tất tức thì
        if (plan.Motion.ReduceMotion || plan.Motion.Duration.TimeSpan <= TimeSpan.Zero)
        {
            border.Width = plan.TargetWidth;
            border.Height = plan.TargetHeight;
            if (_refs.CornerRadiusBuilder != null)
                border.CornerRadius = _refs.CornerRadiusBuilder(plan.TargetCornerRadius);
            else
                border.CornerRadius = new CornerRadius(plan.TargetCornerRadius);
            onCompleted(new TransitionExecutionResult(sessionId, TransitionExecutionStatus.Completed));
            return;
        }

        // 5. Bắt đầu animation mới từ hình ảnh hiện tại
        int fps = plan.Motion.TargetFps > 0 ? plan.Motion.TargetFps : AnimationConfig.TargetFps;

        var widthAnim = new DoubleAnimation(currentWidth, plan.TargetWidth, plan.Motion.Duration)
        {
            EasingFunction = plan.Motion.Easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        Timeline.SetDesiredFrameRate(widthAnim, fps);

        var heightAnim = new DoubleAnimation(currentHeight, plan.TargetHeight, plan.Motion.Duration)
        {
            EasingFunction = plan.Motion.Easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        Timeline.SetDesiredFrameRate(heightAnim, fps);

        int completedCount = 0;
        void OnAnimCompleted()
        {
            completedCount++;
            if (completedCount < 2) return;

            // 6. Chỉ phiên mới nhất được xác nhận hoàn tất
            if (!IsCurrent(sessionId, version))
            {
                RuntimeLog.Debug(LogTag, () => $"Shell animation #{sessionId} completed but was superseded by #{_activeSessionId}");
                return;
            }

            border.BeginAnimation(FrameworkElement.WidthProperty, null);
            border.BeginAnimation(FrameworkElement.HeightProperty, null);
            border.Width = plan.TargetWidth;
            border.Height = plan.TargetHeight;
            if (_refs.CornerRadiusBuilder != null)
                border.CornerRadius = _refs.CornerRadiusBuilder(plan.TargetCornerRadius);
            else
                border.CornerRadius = new CornerRadius(plan.TargetCornerRadius);

            onCompleted(new TransitionExecutionResult(sessionId, TransitionExecutionStatus.Completed));
        }

        widthAnim.Completed += (_, _) => OnAnimCompleted();
        heightAnim.Completed += (_, _) => OnAnimCompleted();

        border.BeginAnimation(FrameworkElement.WidthProperty, widthAnim);
        border.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);
    }

    public void SnapTo(double width, double height, double cornerRadius)
    {
        if (_disposed) return;
        ++_animationVersion;
        _activeSessionId++;
        var border = _refs.NotchBorder;
        border.BeginAnimation(FrameworkElement.WidthProperty, null);
        border.BeginAnimation(FrameworkElement.HeightProperty, null);
        border.Width = width;
        border.Height = height;
        if (_refs.CornerRadiusBuilder != null)
            border.CornerRadius = _refs.CornerRadiusBuilder(cornerRadius);
        else
            border.CornerRadius = new CornerRadius(cornerRadius);
    }

    public void CancelCurrentAnimation()
    {
        ++_animationVersion;
        _activeSessionId++;
        var border = _refs.NotchBorder;
        double currentWidth = ReadExtent(border.Width, border.ActualWidth, 0);
        double currentHeight = ReadExtent(border.Height, border.ActualHeight, 0);

        border.BeginAnimation(FrameworkElement.WidthProperty, null);
        border.BeginAnimation(FrameworkElement.HeightProperty, null);
        border.Width = currentWidth;
        border.Height = currentHeight;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelCurrentAnimation();
    }

    private bool IsCurrent(long sessionId, long version) =>
        !_disposed && version == _animationVersion && sessionId == _activeSessionId &&
        (_refs.IsSessionCurrent?.Invoke(sessionId) ?? true);

    private static double ReadExtent(double effective, double actual, double fallback) =>
        double.IsFinite(effective) && effective >= 0 ? effective :
        double.IsFinite(actual) && actual > 0 ? actual : fallback;
}
