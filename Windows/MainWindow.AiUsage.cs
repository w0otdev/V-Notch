using System.Windows;
using System.Windows.Threading;

namespace VNotch;

public partial class MainWindow
{
    private bool _aiUsageLayoutQueued;

    private double MeasureAiUsageViewHeight()
    {
        AiUsageViewControl.PrepareForDisplay();
        double contentWidth = Math.Max(0, AiUsageViewWidth - AiUsageContent.Margin.Left - AiUsageContent.Margin.Right);
        AiUsageViewControl.Measure(new Size(contentWidth, double.PositiveInfinity));
        return Math.Ceiling(Math.Max(_expandedHeight,
            AiUsageViewControl.DesiredSize.Height + AiUsageContent.Margin.Top + AiUsageContent.Margin.Bottom));
    }

    private void FitAiUsageView()
    {
        double height = MeasureAiUsageViewHeight();
        AiUsageContent.Width = AiUsageViewWidth - AiUsageContent.Margin.Left - AiUsageContent.Margin.Right;
        AiUsageContent.Height = Math.Max(0, height - AiUsageContent.Margin.Top - AiUsageContent.Margin.Bottom);
        ResizeHostWindowHeight(height);
        NotchBorder.BeginAnimation(HeightProperty, null);
        NotchBorder.Height = height;
    }

    private void ScheduleAiUsageLayout()
    {
        if (_aiUsageLayoutQueued || _cleanedUp) return;
        _aiUsageLayoutQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _aiUsageLayoutQueued = false;
            if (!_cleanedUp && _isExpanded && _isAiUsageView && !_isAnimating) FitAiUsageView();
        });
    }
}
