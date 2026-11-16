using System.Windows;
using System.Windows.Controls;
using VNotch.Services;
namespace VNotch;

public partial class SpotlightWindow
{
    private object? _aiCopyOriginalContent;
    private int _aiCopyFeedbackVersion;

    private static void SetAiToolLabel(Button button, string key)
    {
        string label = Loc.Get(key);
        button.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(button, label);
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
        string text;
        try
        {
            string id = _chatId;
            var parts = new List<string> { string.Join("\n\n", _aiHistory.Select(m => m.Content)) };
            int before = _messageOffset;
            await _chatSession.FlushAsync();
            while (before > 0)
            {
                var page = await _chatStore.LoadAsync(id, before, 100);
                if (page == null || page.MessageOffset >= before) throw new System.IO.IOException("Chat page is unavailable.");
                parts.Add(string.Join("\n\n", page.Messages.Select(m => m.Content)));
                before = page.MessageOffset;
            }
            if (_allowClose || _chatId != id) return;
            parts.Reverse();
            text = string.Join("\n\n", parts);
        }
        catch (Exception ex) { ReportChatFailure(ex); return; }
        if (TryCopyToClipboard(text))
        {
            SetAiStatus("spotlight.ai.copied");
            _aiCopyOriginalContent ??= AiCopyButton.Content;
            int version = ++_aiCopyFeedbackVersion;
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
            await System.Threading.Tasks.Task.Delay(1800, CancellationToken.None);
            if (version == _aiCopyFeedbackVersion)
            {
                AiCopyButton.Content = _aiCopyOriginalContent;
                _aiCopyOriginalContent = null;
            }
        }
    }
}
