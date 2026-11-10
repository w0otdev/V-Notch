using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    internal Func<bool>? ScreenCaptureActivityProbe { get; set; }
    private readonly ScreenCaptureActivity _screenCaptureActivity = new();
    private bool IsScreenCaptureActive => ScreenCaptureActivityProbe?.Invoke() ?? _screenCaptureActivity.IsActive();

    protected override void OnSourceInitialized(EventArgs e)
    {
        // Apply the user's Invisible mode before the first visible frame.
        WindowAnimationPolicy.Attach(this);
        base.OnSourceInitialized(e);
    }
}
