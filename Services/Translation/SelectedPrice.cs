using System.Globalization;
using System.Text.RegularExpressions;

namespace VNotch.Services.Translation;

internal sealed record SelectedPrice(decimal Amount, string Currency, bool AssumedCurrency = false);

internal static class TranslationCurrencies
{
    internal static readonly string[] Codes = "AED ARS AUD BDT BGN BHD BRL CAD CHF CLP CNY COP CZK DKK EGP EUR GBP HKD HUF IDR ILS INR ISK JPY KRW KWD LAK LKR MAD MMK MXN MYR NOK NPR NZD PEN PHP PKR PLN QAR RON RSD RUB SAR SEK SGD THB TRY TWD UAH USD UZS VND ZAR".Split(' ');
    internal static bool Supported(string? code) => code != null && Array.IndexOf(Codes, code) >= 0;
    internal static string Normalize(string? code) => Supported(code) ? code! : "auto";
    internal static string? ForLanguage(string language) => language switch
    {
        "vi" => "VND",
        "en" => "USD",
        "ja" => "JPY",
        "zh" => "CNY",
        "ko" => "KRW",
        "de" or "fr" or "es" or "it" or "nl" or "el" or "fi" or "et" or "lv" or "lt" or "ga" or "sk" or "sl" or "ca" => "EUR",
        "pt" => "BRL",
        "ar" => "SAR",
        "ru" or "ba" => "RUB",
        "bn" => "BDT",
        "hi" or "gu" or "kn" or "ml" or "mr" or "or" or "pa" or "ta" => "INR",
        "th" => "THB",
        "id" or "jv" or "su" => "IDR",
        "ms" => "MYR",
        "tr" => "TRY",
        "pl" => "PLN",
        "cs" => "CZK",
        "da" => "DKK",
        "no" => "NOK",
        "sv" => "SEK",
        "hu" => "HUF",
        "ro" => "RON",
        "uk" => "UAH",
        "he" or "yi" => "ILS",
        "ur" or "sd" => "PKR",
        "ne" => "NPR",
        "my" => "MMK",
        "lo" => "LAK",
        "si" => "LKR",
        "tl" or "ceb" or "ilo" => "PHP",
        "af" or "zu" or "xh" or "ns" => "ZAR",
        "is" => "ISK",
        "sr" => "RSD",
        "uz" => "UZS",
        "cy" or "gd" => "GBP",
        _ => null
    };
    internal static int DecimalPlaces(string currency) => currency switch
    { "VND" or "JPY" or "KRW" or "CLP" => 0, "KWD" or "BHD" => 3, _ => 2 };

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

    internal static bool TryParse(string text, string sourceLanguage, out SelectedPrice price)
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
        if (currency == null || (!qualifiedSymbol && prefix.Length > 0 && suffix.Length > 0 && Resolve(suffix, sourceLanguage) != currency)) return false;
        if (!TryAmount(match.Groups["amount"].Value.Trim(), currency, out decimal amount)) return false;
        price = new(amount, currency, !qualifiedSymbol && (prefix == "$" || suffix == "$" || prefix is "¥" or "￥" || suffix is "¥" or "￥"));
        return true;
    }

    private static string? Resolve(string token, string source) => token.Trim().ToUpperInvariant() switch
    {
        "$" => LocalDollar(),
        "US$" or "DOLLAR" or "DOLLARS" or "BUCK" or "BUCKS" or "ĐÔ" or "ĐÔ LA" => "USD",
        "CA$" => "CAD",
        "AU$" => "AUD",
        "NZ$" => "NZD",
        "HK$" => "HKD",
        "S$" => "SGD",
        "R$" => "BRL",
        "€" or "EURO" or "EUROS" => "EUR",
        "£" or "POUND" or "POUNDS" => "GBP",
        "₫" or "Đ" or "ĐỒNG" or "DONG" => "VND",
        "₩" or "WON" => "KRW",
        "₹" or "RUPEE" or "RUPEES" => "INR",
        "฿" or "BAHT" => "THB",
        "₽" or "RUBLE" or "RUBLES" => "RUB",
        "CN¥" or "YUAN" or "RMB" => "CNY",
        "JP¥" or "YEN" => "JPY",
        "¥" or "￥" => source == "zh" ? "CNY" : source == "ja" ? "JPY" : null,
        var code => Supported(code) ? code : null
    };

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
        value = value.Replace('\u00a0', ' ').Replace('\u202f', ' ');
        int dot = value.LastIndexOf('.'), comma = value.LastIndexOf(',');
        char? decimalMark = null;
        if (dot >= 0 && comma >= 0) decimalMark = dot > comma ? '.' : ',';
        else if (Math.Max(dot, comma) is int separator && separator >= 0)
        {
            int decimals = value.Length - separator - 1;
            // A single separator followed by 3 digits is ambiguous for 3-decimal currencies.
            if (decimals == 3 && DecimalPlaces(currency) == 3) return false;
            if (decimals is 1 or 2) decimalMark = value[separator];
        }
        string integer = value, fraction = "";
        if (decimalMark is char mark)
        {
            int split = value.LastIndexOf(mark);
            integer = value[..split]; fraction = value[(split + 1)..];
            if (fraction.Length > DecimalPlaces(currency) || !fraction.All(char.IsAsciiDigit) || integer.Contains(mark)) return false;
        }
        // Validate grouping before stripping it; "1,2,3" must never become 123.
        var separators = integer.Where(c => !char.IsAsciiDigit(c)).Distinct().ToArray();
        if (separators.Length > 1) return false;
        if (separators.Length == 1)
        {
            if (separators[0] is not ('.' or ',' or ' ' or '\'')) return false;
            var groups = integer.Split(separators[0]);
            if (groups[0].Length is < 1 or > 3 || groups.Skip(1).Any(g => g.Length != 3) || groups.Any(g => !g.All(char.IsAsciiDigit))) return false;
            integer = string.Concat(groups);
        }
        return decimal.TryParse(integer + (fraction.Length > 0 ? "." + fraction : ""), NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out amount) && amount <= 1_000_000_000_000_000m;
    }
}
