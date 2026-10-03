using System.Windows;
using System.Windows.Controls;
using VNotch.Services;
namespace VNotch;

public partial class SpotlightWindow
{
    private static void SetAiToolLabel(Button button, string key)
    {
        button.ToolTip = Loc.Get(key);
        System.Windows.Automation.AutomationProperties.SetName(button, Loc.Get(key));
    }
    private void UpdateAiMetadata()
    {
        AiMetadata.Text = Loc.Get("spotlight.ai.metadata", _aiHistory.Count, _aiHistory.Sum(m => m.Content.Length));
        AiCopyButton.IsEnabled = _aiHistory.Count > 0;
    }
    private async void AiBack_Click(object sender, RoutedEventArgs e)
    {
        if (AiHistoryPanel.Visibility == Visibility.Visible)
        {
            await TransitionAiPageAsync(() => { CloseAiHistory(); RefreshAiPanel(); });
        }
        else await TransitionAiPageAsync(ToggleAiMode);
    }
    private async void AiCopy_Click(object sender, RoutedEventArgs e)
    {
        string text = string.Join("\n\n", _aiHistory.Select(m => m.Content));
        if (TryCopyToClipboard(text))
        {
            SetAiStatus("spotlight.ai.copied");
            var originalContent = AiCopyButton.Content;
            AiCopyButton.Content = new TextBlock
            {
                Text = "✓",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = UiPalette.PrimaryBrush
            };
            if (!AnimationConfig.ReduceMotion)
            {
                var pop = new System.Windows.Media.Animation.DoubleAnimation(1.3, 1.0, System.TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new System.Windows.Media.Animation.QuarticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };
                AiCopyButton.RenderTransform = new System.Windows.Media.ScaleTransform();
                AiCopyButton.RenderTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, pop);
                AiCopyButton.RenderTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, pop);
            }
            await System.Threading.Tasks.Task.Delay(1800);
            AiCopyButton.Content = originalContent;
        }
    }
}
