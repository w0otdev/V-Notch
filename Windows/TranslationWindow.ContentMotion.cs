namespace VNotch;

public partial class TranslationWindow
{
    private bool ContentIsMorphing => ResultHeightHost.IsMorphing || StatusHeightHost.IsMorphing;

    private void InitializeContentMotion()
    {
        ResultHeightHost.TargetHeightChanged += ScheduleContentPlacement;
        StatusHeightHost.TargetHeightChanged += ScheduleContentPlacement;
        ResultHeightHost.MorphCompleted += ScheduleContentPlacement;
        StatusHeightHost.MorphCompleted += ScheduleContentPlacement;
    }

    private void ScheduleContentPlacement()
    {
        if (IsVisible && !_dismissing) SchedulePresentation();
    }

    private void SetContentMorphing(bool enabled)
    {
        ResultHeightHost.MotionEnabled = StatusHeightHost.MotionEnabled = enabled;
        if (enabled) return;
        ResultHeightHost.Settle();
        StatusHeightHost.Settle();
    }
}
