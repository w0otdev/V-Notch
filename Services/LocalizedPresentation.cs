using System.Windows;
using System.Windows.Markup;

namespace VNotch.Services;

internal static class LocalizedPresentation
{
    private static bool _initialized;

    internal static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Apply((Window)sender)));
        Loc.LanguageChanged += RefreshWindows;
    }

    private static void RefreshWindows()
    {
        var app = Application.Current;
        if (app == null || app.Dispatcher.HasShutdownStarted) return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(RefreshWindows));
            return;
        }
        foreach (Window window in app.Windows) Apply(window);
    }

    internal static void Apply(FrameworkElement element)
    {
        var culture = Loc.GetCulture();
        element.SetCurrentValue(FrameworkElement.LanguageProperty, XmlLanguage.GetLanguage(culture.IetfLanguageTag));
        element.SetCurrentValue(FrameworkElement.FlowDirectionProperty,
            culture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight);
    }
}
