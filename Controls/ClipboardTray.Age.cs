using System.Windows;
using VNotch.ViewModels;

namespace VNotch.Controls;

public partial class ClipboardTray
{
    private void UpdateAgeTimer()
    {
        if (_disposed || !IsLoaded || !IsVisible) { _ageTimer.Stop(); return; }
        RefreshVisibleCardAges();
        _ageTimer.Start();
    }

    private void AgeTimer_Tick(object? sender, EventArgs e) => RefreshVisibleCardAges();

    private void RefreshVisibleCardAges()
    {
        DateTime now = DateTime.UtcNow;
        foreach (var element in _realizedCardElements)
            if (element.IsLoaded && element.IsVisible && element.DataContext is ClipboardCardViewModel card) card.RefreshAge(now);
    }

    private static void RefreshCardAge(FrameworkElement element)
    {
        if (element.IsLoaded && element.IsVisible && element.DataContext is ClipboardCardViewModel card)
            card.RefreshAge(DateTime.UtcNow);
    }
}
