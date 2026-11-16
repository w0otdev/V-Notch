using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

namespace VNotch.Controls;

// FormattedText does not expose OpenType numeral alignment. Use WPF's formatter
// for both the measured slots and cached glyphs so tabular digits keep their face
// and advance width when the native TextBlock hands over to the animation.
internal static class NumberFlowTextFormatting
{
    internal sealed class Source(string text, NumberFlowGlyphCache.Style[] styles) : TextSource
    {
        public override TextRun GetTextRun(int index)
        {
            if (index >= text.Length) return new TextEndOfParagraph(1);
            int end = index + 1;
            while (end < text.Length && styles[end] == styles[index]) end++;
            return new TextCharacters(text, index, end - index,
                new RunProperties(styles[index]) { PixelsPerDip = styles[index].PixelsPerDip });
        }

        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int limit) =>
            new(limit, new(styles[0].Culture, new CharacterBufferRange(text, 0, limit)));

        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int index) => index;
    }

    internal sealed class Paragraph(NumberFlowGlyphCache.Style style, TextAlignment alignment = TextAlignment.Left,
        TextWrapping wrapping = TextWrapping.NoWrap, double lineHeight = 0) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => style.Direction;
        public override TextAlignment TextAlignment => alignment;
        public override double LineHeight => lineHeight;
        public override bool FirstLineInParagraph => true;
        public override TextRunProperties DefaultTextRunProperties { get; } = new RunProperties(style) { PixelsPerDip = style.PixelsPerDip };
        public override TextWrapping TextWrapping => wrapping;
        public override TextMarkerProperties TextMarkerProperties => null!;
        public override double Indent => 0;
    }

    private sealed class RunProperties(NumberFlowGlyphCache.Style style) : TextRunProperties
    {
        public override Typeface Typeface => style.Typeface;
        public override double FontRenderingEmSize => style.Size;
        public override double FontHintingEmSize => style.Size;
        public override TextDecorationCollection TextDecorations => null!;
        public override Brush ForegroundBrush => style.Brush;
        public override Brush BackgroundBrush => null!;
        public override CultureInfo CultureInfo => style.Culture;
        public override TextEffectCollection TextEffects => null!;
        public override TextRunTypographyProperties TypographyProperties { get; } = new Numerals(style.NumeralAlignment);
    }

    private sealed class Numerals(FontNumeralAlignment alignment) : TextRunTypographyProperties
    {
        public override FontNumeralAlignment NumeralAlignment => alignment;
        public override bool StandardLigatures => true;
        public override bool ContextualLigatures => true;
        public override bool ContextualAlternates => true;
        public override bool Kerning => true;
        public override bool DiscretionaryLigatures => false;
        public override bool HistoricalLigatures => false;
        public override bool HistoricalForms => false;
        public override bool CapitalSpacing => false;
        public override bool CaseSensitiveForms => false;
        public override bool StylisticSet1 => false;
        public override bool StylisticSet2 => false;
        public override bool StylisticSet3 => false;
        public override bool StylisticSet4 => false;
        public override bool StylisticSet5 => false;
        public override bool StylisticSet6 => false;
        public override bool StylisticSet7 => false;
        public override bool StylisticSet8 => false;
        public override bool StylisticSet9 => false;
        public override bool StylisticSet10 => false;
        public override bool StylisticSet11 => false;
        public override bool StylisticSet12 => false;
        public override bool StylisticSet13 => false;
        public override bool StylisticSet14 => false;
        public override bool StylisticSet15 => false;
        public override bool StylisticSet16 => false;
        public override bool StylisticSet17 => false;
        public override bool StylisticSet18 => false;
        public override bool StylisticSet19 => false;
        public override bool StylisticSet20 => false;
        public override bool SlashedZero => false;
        public override bool MathematicalGreek => false;
        public override bool EastAsianExpertForms => false;
        public override FontVariants Variants => FontVariants.Normal;
        public override FontCapitals Capitals => FontCapitals.Normal;
        public override FontFraction Fraction => FontFraction.Normal;
        public override FontNumeralStyle NumeralStyle => FontNumeralStyle.Normal;
        public override FontEastAsianWidths EastAsianWidths => FontEastAsianWidths.Normal;
        public override FontEastAsianLanguage EastAsianLanguage => FontEastAsianLanguage.Normal;
        public override int StandardSwashes => 0;
        public override int ContextualSwashes => 0;
        public override int StylisticAlternates => 0;
        public override int AnnotationAlternates => 0;
    }
}
