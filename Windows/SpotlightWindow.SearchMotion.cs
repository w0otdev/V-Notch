using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch;

public partial class SpotlightWindow
{
    private readonly Controls.SpotlightSearchActivityMotion _searchMotion;
    private bool _searchInputPending;

    private void OnSearchMotionPreferenceChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(OnSearchMotionPreferenceChanged);
            return;
        }
        if (AnimationConfig.ReduceMotion)
        {
            ResetAiPromptReveal();
            StatusPanel.BeginAnimation(OpacityProperty, null);
            StatusPanel.Opacity = 1;
        }
        _searchMotion.Refresh();
    }
}
