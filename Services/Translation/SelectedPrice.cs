using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace VNotch.Services.Translation;

internal sealed record SelectedPrice(decimal Amount, string Currency, bool AssumedCurrency = false);

internal static partial class TranslationCurrencies
{
    private const string NativeDll = "VNotch.Native.dll";

    internal static readonly string[] Codes = "AED ARS AUD BDT BGN BHD BRL CAD CHF CLP CNY COP CZK DKK EGP EUR GBP HKD HUF IDR ILS INR ISK JPY KRW KWD LAK LKR MAD MMK MXN MYR NOK NPR NZD PEN PHP PKR PLN QAR RON RSD RUB SAR SEK SGD THB TRY TWD UAH USD UZS VND ZAR".Split(' ');

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_currency_supported(
        ReadOnlySpan<char> codePtr,
        int codeLen,
        out byte outSupported);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_currency_decimal_places(
        ReadOnlySpan<char> currencyPtr,
        int currencyLen,
        out int outPlaces);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_currency_for_language(
        ReadOnlySpan<char> langPtr,
        int langLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_selected_price_has_conflicting_currency(
        ReadOnlySpan<char> textPtr,
        int textLen,
        ReadOnlySpan<char> sourcePtr,
        int sourceLen,
        ReadOnlySpan<char> localDollarPtr,
        int localDollarLen,
        out byte outConflicting);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_selected_price_try_parse(
        ReadOnlySpan<char> textPtr,
        int textLen,
        ReadOnlySpan<char> sourcePtr,
        int sourceLen,
        ReadOnlySpan<char> localDollarPtr,
        int localDollarLen,
        ReadOnlySpan<char> overridePtr,
        int overrideLen,
        Span<char> outAmountBuf,
        int amountCap,
        out int outAmountLen,
        Span<char> outCurrencyBuf,
        int currencyCap,
        out int outCurrencyLen,
        out byte outAssumedCurrency,
        out byte outOk);

    internal static bool Supported(string? code)
    {
        if (string.IsNullOrEmpty(code)) return false;
        ReadOnlySpan<char> span = code.AsSpan();
        vn_currency_supported(span, span.Length, out byte supported);
        return supported != 0;
    }

    internal static string Normalize(string? code) => Supported(code) ? code! : "auto";

    internal static string? ForLanguage(string language)
    {
        if (string.IsNullOrEmpty(language)) return null;
        ReadOnlySpan<char> span = language.AsSpan();
        Span<char> buf = stackalloc char[8];
        vn_currency_for_language(span, span.Length, buf, buf.Length, out int len);
        return len > 0 ? new string(buf[..len]) : null;
    }

    internal static int DecimalPlaces(string currency)
    {
        ReadOnlySpan<char> span = (currency ?? string.Empty).AsSpan();
        vn_currency_decimal_places(span, span.Length, out int places);
        return places;
    }

    // Also inspect prices inside sentences before either conversion or model inference.
    internal static bool HasConflictingCurrency(string text, string sourceLanguage)
    {
        if (string.IsNullOrEmpty(text)) return false;
        ReadOnlySpan<char> t = text.AsSpan();
        ReadOnlySpan<char> s = (sourceLanguage ?? string.Empty).AsSpan();
        string? localDollar = t.Contains('$') ? LocalDollar() : null;
        ReadOnlySpan<char> ld = (localDollar ?? string.Empty).AsSpan();
        return vn_selected_price_has_conflicting_currency(t, t.Length, s, s.Length, ld, ld.Length, out byte conflicting) == 0 &&
               conflicting != 0;
    }

    internal static bool TryParse(string text, string sourceLanguage, out SelectedPrice price, string? currencyOverride = null)
    {
        price = null!;
        if (string.IsNullOrEmpty(text) || text.Length > 128) return false;
        ReadOnlySpan<char> t = text.AsSpan();
        ReadOnlySpan<char> s = (sourceLanguage ?? string.Empty).AsSpan();
        string? localDollar = t.Contains('$') ? LocalDollar() : null;
        ReadOnlySpan<char> ld = (localDollar ?? string.Empty).AsSpan();
        ReadOnlySpan<char> ov = (currencyOverride ?? string.Empty).AsSpan();
        Span<char> amountBuf = stackalloc char[140];
        Span<char> currencyBuf = stackalloc char[8];

        if (vn_selected_price_try_parse(
                t, t.Length, s, s.Length, ld, ld.Length, ov, ov.Length,
                amountBuf, amountBuf.Length, out int amountLen,
                currencyBuf, currencyBuf.Length, out int currencyLen,
                out byte assumed, out byte ok) != 0 ||
            ok == 0 || amountLen <= 0 || currencyLen <= 0)
        {
            return false;
        }

        if (!decimal.TryParse(amountBuf[..amountLen], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal amount) ||
            amount > 1_000_000_000_000_000m)
        {
            return false;
        }

        price = new(amount, new string(currencyBuf[..currencyLen]), assumed != 0);
        return true;
    }

    internal static bool HasUnresolvedSymbol(string text, string sourceLanguage) =>
        !TryParse(text, sourceLanguage, out _) && TryParse(text, sourceLanguage, out _, "USD");

    private static string? LocalDollar()
    {
        try
        {
            var region = new RegionInfo(CultureInfo.CurrentCulture.Name);
            return region.ISOCurrencySymbol is "USD" or "CAD" or "AUD" or "NZD" or "HKD" or "SGD"
                ? region.ISOCurrencySymbol : null;
        }
        catch (ArgumentException) { return null; }
    }
}
