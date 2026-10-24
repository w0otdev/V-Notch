using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

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
    private static partial int vn_currency_resolve_token(
        ReadOnlySpan<char> tokenPtr,
        int tokenLen,
        ReadOnlySpan<char> sourcePtr,
        int sourceLen,
        ReadOnlySpan<char> localDollarPtr,
        int localDollarLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_currency_normalize_amount(
        ReadOnlySpan<char> valuePtr,
        int valueLen,
        ReadOnlySpan<char> currencyPtr,
        int currencyLen,
        Span<char> outBuf,
        int outCap,
        out int outLen,
        out byte outValid);

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

    // Anchor the entire selection: never rewrite a price embedded in a sentence,
    // a range, a date, or an expression with several amounts.
    private static readonly Regex Price = new(
        @"\A\s*(?<prefix>[A-Za-z\p{L}]+(?:\s+[A-Za-z\p{L}]+)?|US\$|CA\$|AU\$|NZ\$|HK\$|S\$|R\$|CN¥|JP¥|[$€£¥￥₫đ₩₹฿₽])?\s*(?<amount>[0-9][0-9.,'\u00a0\u202f ]*)\s*(?<suffix>[A-Za-z\p{L}]+(?:\s+[A-Za-z\p{L}]+)?|US\$|CA\$|AU\$|NZ\$|HK\$|S\$|R\$|CN¥|JP¥|[$€£¥￥₫đ₩₹฿₽])?\s*\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    private static readonly Regex QualifiedPrice = new(
        @"(?<![\p{L}\p{N}])(?<prefix>" + string.Join('|', Codes) + @"|US\$|CA\$|AU\$|NZ\$|HK\$|S\$|R\$|CN¥|JP¥|[$€£¥￥₫đ₩₹฿₽])\s*[0-9][0-9.,'\u00a0\u202f ]*\s*(?<suffix>" + string.Join('|', Codes) + @"|[$€£¥￥₫đ₩₹฿₽])(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    // Also inspect prices inside sentences before either conversion or model inference.
    internal static bool HasConflictingCurrency(string text, string sourceLanguage)
    {
        foreach (Match match in QualifiedPrice.Matches(text))
        {
            string prefix = match.Groups["prefix"].Value, suffix = match.Groups["suffix"].Value;
            string? left = Resolve(prefix, sourceLanguage), right = Resolve(suffix, sourceLanguage);
            if (prefix == "$" && right is "USD" or "CAD" or "AUD" or "NZD" or "HKD" or "SGD") continue;
            if (prefix is "¥" or "￥" && right is "JPY" or "CNY") continue;
            if (left != null && right != null && left != right) return true;
        }
        return false;
    }

    internal static bool TryParse(string text, string sourceLanguage, out SelectedPrice price, string? currencyOverride = null)
    {
        price = null!;
        if (text.Length > 128) return false;
        var match = Price.Match(text);
        if (!match.Success) return false;
        string prefix = match.Groups["prefix"].Value.Trim(), suffix = match.Groups["suffix"].Value.Trim();
        if (prefix.Length == 0 && suffix.Length == 0) return false;
        string? currency = Resolve(prefix.Length > 0 ? prefix : suffix, sourceLanguage);
        string? explicitCurrency = suffix.Length == 3 ? Resolve(suffix, sourceLanguage) : null;
        bool qualifiedSymbol = (prefix == "$" && explicitCurrency is "USD" or "CAD" or "AUD" or "NZD" or "HKD" or "SGD") ||
            (prefix is "¥" or "￥" && explicitCurrency is "JPY" or "CNY");
        if (qualifiedSymbol) currency = explicitCurrency;
        if (!qualifiedSymbol && prefix.Length > 0 && suffix.Length > 0 && Resolve(suffix, sourceLanguage) != currency) return false;
        bool overridden = Supported(currencyOverride);
        if (currency == null && !(prefix is "$" or "¥" or "￥" || suffix is "$" or "¥" or "￥")) return false;
        if (overridden) currency = currencyOverride;
        if (currency == null) return false;
        if (!TryAmount(match.Groups["amount"].Value.Trim(), currency, out decimal amount)) return false;
        price = new(amount, currency, !overridden && !qualifiedSymbol && (prefix == "$" || suffix == "$" || prefix is "¥" or "￥" || suffix is "¥" or "￥"));
        return true;
    }

    internal static bool HasUnresolvedSymbol(string text, string sourceLanguage) =>
        !TryParse(text, sourceLanguage, out _) && TryParse(text, sourceLanguage, out _, "USD");

    private static string? Resolve(string token, string source)
    {
        ReadOnlySpan<char> t = (token ?? string.Empty).AsSpan();
        ReadOnlySpan<char> s = (source ?? string.Empty).AsSpan();
        string? localDollar = t.Trim().SequenceEqual("$".AsSpan()) ? LocalDollar() : null;
        ReadOnlySpan<char> ld = (localDollar ?? string.Empty).AsSpan();
        Span<char> buf = stackalloc char[8];
        vn_currency_resolve_token(t, t.Length, s, s.Length, ld, ld.Length, buf, buf.Length, out int len);
        return len > 0 ? new string(buf[..len]) : null;
    }

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

    private static bool TryAmount(string value, string currency, out decimal amount)
    {
        amount = 0;
        ReadOnlySpan<char> v = (value ?? string.Empty).AsSpan();
        ReadOnlySpan<char> c = (currency ?? string.Empty).AsSpan();
        Span<char> buf = stackalloc char[140];
        vn_currency_normalize_amount(v, v.Length, c, c.Length, buf, buf.Length, out int len, out byte valid);
        if (valid == 0 || len <= 0) return false;
        return decimal.TryParse(buf[..len], NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out amount) && amount <= 1_000_000_000_000_000m;
    }
}
