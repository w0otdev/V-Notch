using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class GlobalizationAccessibilityTests
{
    [Fact]
    public void ArabicMirrorsTheLayoutAndSwitchingToGermanRestoresDirection() => SharedStaTestRunner.Run(() =>
    {
        var label = new TextBlock { Text = "مرحبًا" };
        var physicalTimeline = new Slider { FlowDirection = FlowDirection.LeftToRight };
        var root = new StackPanel { Children = { label, physicalTimeline } };
        try
        {
            Loc.SetLanguage("ar");
            LocalizedPresentation.Apply(root);
            Assert.Equal(FlowDirection.RightToLeft, label.FlowDirection);
            Assert.Equal("ar-SA", root.Language.IetfLanguageTag, ignoreCase: true);
            Assert.Equal(FlowDirection.LeftToRight, physicalTimeline.FlowDirection);
            Loc.SetLanguage("de");
            LocalizedPresentation.Apply(root);
            Assert.Equal(FlowDirection.LeftToRight, label.FlowDirection);
            Assert.Equal("de-DE", label.Language.IetfLanguageTag, ignoreCase: true);
        }
        finally { Loc.SetLanguage("en"); }
    });

    [Fact]
    public void EveryBundledLocaleHasAGreetingAndAccessibleActionNames()
    {
        try
        {
            foreach (var (code, _) in Loc.GetAvailableLanguages())
            {
                Loc.SetLanguage(code);
                foreach (string key in new[] { "greeting.hello", "media.play", "media.pause", "media.previous", "media.next",
                    "media.volume", "media.mute", "nav.media", "nav.clipboard", "nav.timer", "nav.audio", "timer.start", "timer.reset" })
                    Assert.True(Loc.GetKeys(code).Contains(key) && !string.IsNullOrWhiteSpace(Loc.Get(key)), $"{code}: {key}");
            }
        }
        finally { Loc.SetLanguage("en"); }
    }

    [Fact]
    public void ActionBorderSupportsScreenReaderInvocationAndEnterAndSpace() => SharedStaTestRunner.Run(() =>
    {
        var button = new ActionBorder();
        AutomationProperties.SetName(button, "Open Settings");
        int invocations = 0;
        button.Click += (_, _) => invocations++;
        var peer = UIElementAutomationPeer.CreatePeerForElement(button)!;
        Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
        Assert.Equal("Open Settings", peer.GetName());
        Assert.True(button.Focusable);
        var invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
        invoke.Invoke();
        button.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Equal(1, invocations);
        foreach (Key key in new[] { Key.Enter, Key.Space })
            button.RaiseEvent(KeyEvent(button, key));
        Assert.Equal(3, invocations);
        button.IsEnabled = false;
        Assert.Throws<ElementNotEnabledException>(invoke.Invoke);
    });

    [Fact]
    public void VolumePanelExposesRangeValueAndUsesTheSameRequestForKeyboardAndScreenReader() => SharedStaTestRunner.Run(() =>
    {
        var volume = new AccessibleVolumePanel { Value = .5 };
        double requested = -1;
        volume.ValueRequested += (_, e) => requested = e.NewValue;
        AutomationProperties.SetName(volume, "Volume");
        var peer = UIElementAutomationPeer.CreatePeerForElement(volume)!;
        Assert.Equal(AutomationControlType.Slider, peer.GetAutomationControlType());
        var range = Assert.IsAssignableFrom<IRangeValueProvider>(peer.GetPattern(PatternInterface.RangeValue));
        range.SetValue(.75);
        Assert.Equal(.75, requested);
        Assert.Equal(.75, range.Value);
        volume.RaiseEvent(KeyEvent(volume, Key.Home));
        Assert.Equal(0, requested);
        volume.RaiseEvent(KeyEvent(volume, Key.Right));
        Assert.Equal(.05, requested, 6);
        volume.FlowDirection = FlowDirection.RightToLeft;
        volume.RaiseEvent(KeyEvent(volume, Key.Left));
        Assert.Equal(.1, requested, 6);
        Assert.Throws<ArgumentOutOfRangeException>(() => range.SetValue(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => range.SetValue(1.1));
        volume.IsEnabled = false;
        Assert.Throws<ElementNotEnabledException>(() => range.SetValue(.5));
    });

    [Fact]
    public void ElasticSliderInheritsRangeAutomationAndPublishesLocalizedLabels() => SharedStaTestRunner.Run(() =>
    {
        var slider = new ElasticSlider
        {
            Label = "Lautstärke",
            Description = "Lautstärke ändern",
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 5,
            Value = 50,
            FlowDirection = FlowDirection.RightToLeft
        };
        var peer = UIElementAutomationPeer.CreatePeerForElement(slider)!;
        Assert.Equal("Lautstärke", peer.GetName());
        Assert.Equal("Lautstärke ändern", peer.GetHelpText());
        Assert.IsAssignableFrom<IRangeValueProvider>(peer.GetPattern(PatternInterface.RangeValue));
        slider.RaiseEvent(KeyEvent(slider, Key.Left, Keyboard.PreviewKeyDownEvent));
        Assert.Equal(55, slider.Value);
        slider.RaiseEvent(KeyEvent(slider, Key.Right, Keyboard.PreviewKeyDownEvent));
        Assert.Equal(50, slider.Value);
    });

    [Fact]
    public void HighlightedTextAndMorphingTitleExposeTheirFullTextToAutomation() => SharedStaTestRunner.Run(() =>
    {
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, Width = 80 };
        const string title = "Datenschutzeinstellungen und Batteriekapazität";
        HighlightedText.SetText(text, title);
        HighlightedText.SetQuery(text, "Batterie");
        Assert.Equal(title, UIElementAutomationPeer.CreatePeerForElement(text)!.GetName());
        var morph = new MorphingSettingsTitle { Text = title };
        Assert.Equal(title, UIElementAutomationPeer.CreatePeerForElement(morph)!.GetName());
        Assert.Equal(title, morph.ToolTip);
    });

    private static KeyEventArgs KeyEvent(UIElement source, Key key, RoutedEvent? routedEvent = null) => new(Keyboard.PrimaryDevice,
        new TestPresentationSource(), Environment.TickCount, key)
    { RoutedEvent = routedEvent ?? Keyboard.KeyDownEvent, Source = source };

    private sealed class TestPresentationSource : PresentationSource
    {
        public override System.Windows.Media.Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget GetCompositionTargetCore() => null!;
    }
}
