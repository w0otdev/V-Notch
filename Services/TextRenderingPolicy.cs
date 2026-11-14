using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace VNotch.Services;

internal static partial class TextRenderingPolicy
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll)]
    private static partial int vn_text_snap_bundled_weight(
        int requestedWeight,
        byte isBundledSfPro,
        out int outWeight);

    [LibraryImport(NativeDll)]
    private static partial int vn_text_resolve_rendering_policy(
        int elementRole,
        byte hasExplicitRenderingMode,
        int currentRenderingMode,
        out int outFormattingMode,
        out int outRenderingMode,
        out int outHintingMode,
        out byte outUseLayoutRounding,
        out byte outSnapsToDevicePixels);

    static TextRenderingPolicy()
    {
        // Cover windows, panels, decorators, controls, popups, and text leaves
        // with non-overlapping FrameworkElement subclasses so ancestors cannot
        // reintroduce device-pixel layout rounding onto animated text subtrees.
        foreach (Type type in new[]
        {
            typeof(Control), typeof(Panel), typeof(Decorator), typeof(Popup),
            typeof(TextBlock), typeof(AccessText), typeof(ContentPresenter)
        })
        {
            EventManager.RegisterClassHandler(type, FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnRootLoaded));
        }
    }

    private static void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        var root = (FrameworkElement)sender;
        var currentRenderingMode = TextOptions.GetTextRenderingMode(root);
        bool hasExplicitRendering = DependencyPropertyHelper.GetValueSource(
            root, TextOptions.TextRenderingModeProperty).BaseValueSource != BaseValueSource.Default;

        ResolvePolicy(
            role: root is TextBlock or AccessText or TextBoxBase or PasswordBox ? 1 : 0,
            hasExplicitRendering,
            currentRenderingMode,
            out TextFormattingMode formattingMode,
            out TextRenderingMode renderingMode,
            out TextHintingMode hintingMode,
            out bool useLayoutRounding,
            out bool snapsToDevicePixels);

        if (TextOptions.GetTextFormattingMode(root) != formattingMode)
            root.SetCurrentValue(TextOptions.TextFormattingModeProperty, formattingMode);
        if (!hasExplicitRendering && currentRenderingMode != renderingMode)
            root.SetCurrentValue(TextOptions.TextRenderingModeProperty, renderingMode);
        if (TextOptions.GetTextHintingMode(root) != hintingMode)
            root.SetCurrentValue(TextOptions.TextHintingModeProperty, hintingMode);

        if (root.UseLayoutRounding != useLayoutRounding)
            root.SetCurrentValue(FrameworkElement.UseLayoutRoundingProperty, useLayoutRounding);
        if (root.SnapsToDevicePixels != snapsToDevicePixels)
            root.SetCurrentValue(UIElement.SnapsToDevicePixelsProperty, snapsToDevicePixels);

        NormalizeBundledWeight(root);
    }

    private static void ResolvePolicy(
        int role,
        bool hasExplicitRenderingMode,
        TextRenderingMode currentRenderingMode,
        out TextFormattingMode formattingMode,
        out TextRenderingMode renderingMode,
        out TextHintingMode hintingMode,
        out bool useLayoutRounding,
        out bool snapsToDevicePixels)
    {
        try
        {
            if (vn_text_resolve_rendering_policy(
                    role,
                    hasExplicitRenderingMode ? (byte)1 : (byte)0,
                    (int)currentRenderingMode,
                    out int fmt,
                    out int rnd,
                    out int hint,
                    out byte round,
                    out byte snap) == 0)
            {
                formattingMode = (TextFormattingMode)fmt;
                renderingMode = (TextRenderingMode)rnd;
                hintingMode = (TextHintingMode)hint;
                useLayoutRounding = round != 0;
                snapsToDevicePixels = snap != 0;
                return;
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        formattingMode = TextFormattingMode.Ideal;
        renderingMode = hasExplicitRenderingMode ? currentRenderingMode : TextRenderingMode.Grayscale;
        hintingMode = TextHintingMode.Animated;
        useLayoutRounding = false;
        snapsToDevicePixels = false;
    }

    internal static void ApplyDocumentDefaults(FlowDocument document)
    {
        if (document == null) return;
        SetDefault(document, TextOptions.TextFormattingModeProperty, TextFormattingMode.Ideal);
        SetDefault(document, TextOptions.TextRenderingModeProperty, TextRenderingMode.Grayscale);
        SetDefault(document, TextOptions.TextHintingModeProperty, TextHintingMode.Animated);
        document.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
    }

    internal static FontWeight SnapBundledWeight(FontWeight weight, FontFamily? family)
    {
        bool isBundled = family?.Source is { Length: > 0 } src &&
            src.Contains("SF Pro Display", StringComparison.OrdinalIgnoreCase);
        if (!isBundled) return weight;

        int requested = weight.ToOpenTypeWeight();
        try
        {
            if (vn_text_snap_bundled_weight(requested, 1, out int snapped) == 0 && snapped != requested)
                return FontWeight.FromOpenTypeWeight(snapped);
        }
        catch (DllNotFoundException)
        {
            return FallbackSnapWeight(requested);
        }
        catch (EntryPointNotFoundException)
        {
            return FallbackSnapWeight(requested);
        }
        return weight;
    }

    private static FontWeight FallbackSnapWeight(int requested) => requested switch
    {
        < 450 => FontWeights.Normal,
        < 550 => FontWeights.Medium,
        < 650 => FontWeights.SemiBold,
        _ => FontWeights.Bold
    };

    private static void NormalizeBundledWeight(FrameworkElement root)
    {
        switch (root)
        {
            case TextBlock textBlock:
            {
                FontWeight snapped = SnapBundledWeight(textBlock.FontWeight, textBlock.FontFamily);
                if (snapped != textBlock.FontWeight)
                    textBlock.SetCurrentValue(TextBlock.FontWeightProperty, snapped);
                break;
            }
            case AccessText accessText:
            {
                FontWeight snapped = SnapBundledWeight(accessText.FontWeight, accessText.FontFamily);
                if (snapped != accessText.FontWeight)
                    accessText.SetCurrentValue(AccessText.FontWeightProperty, snapped);
                break;
            }
            case Control control:
            {
                FontWeight snapped = SnapBundledWeight(control.FontWeight, control.FontFamily);
                if (snapped != control.FontWeight)
                    control.SetCurrentValue(Control.FontWeightProperty, snapped);
                break;
            }
        }
    }

    private static void SetDefault(DependencyObject root, DependencyProperty property, object value)
    {
        if (DependencyPropertyHelper.GetValueSource(root, property).BaseValueSource == BaseValueSource.Default)
            root.SetCurrentValue(property, value);
    }

    // The CLR registers the class handlers once, before any UI is constructed.
    internal static void Initialize() { }
}
