using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    internal Func<bool>? ScreenCaptureActivityProbe { get; set; }
    private readonly ScreenCaptureActivity _screenCaptureActivity = new();
    private bool IsScreenCaptureActive => ScreenCaptureActivityProbe?.Invoke() ?? _screenCaptureActivity.IsActive();

    protected override void OnSourceInitialized(EventArgs e)
    {
        // Apply exclusion before the first visible frame, including the solid
        // theme and the entrance animation, before any glass worker has started.
        WindowAnimationPolicy.Attach(this);
        base.OnSourceInitialized(e);
    }
}
