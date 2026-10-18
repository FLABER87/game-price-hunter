using System.Globalization;
using System.Text.RegularExpressions;
using GamePriceHunter.Core.Models;

namespace GamePriceHunter.Core.Text;

/// <summary>Parses human-formatted prices such as "€12.99", "1 299,00 ₽" or "199 грн".</summary>
public static partial class PriceParser
{
    // Longest markers first so "R$" wins over "$" and "US$" over "$".
    private static readonly (string Marker, string Currency)[] CurrencyMarkers =
    [
        ("US$", "USD"), ("CA$", "CAD"), ("C$", "CAD"), ("A$", "AUD"), ("AU$", "AUD"), ("NZ$", "NZD"), ("R$", "BRL"), ("MX$", "MXN"),
        ("руб.", "RUB"), ("руб", "RUB"), ("₽", "RUB"), ("р.", "RUB"),
        ("грн.", "UAH"), ("грн", "UAH"), ("₴", "UAH"),
        ("тг", "KZT"), ("₸", "KZT"),
        ("zł", "PLN"), ("kč", "CZK"), ("₺", "TRY"), ("₹", "INR"), ("₩", "KRW"), ("¥", "JPY"),
        ("€", "EUR"), ("£", "GBP"), ("$", "USD"),
    ];

    private static readonly HashSet<string> IsoCodes =
    [
        "USD", "EUR", "GBP", "RUB", "RUR", "UAH", "KZT", "PLN", "CZK", "TRY", "INR", "BRL", "CAD", "AUD", "NZD", "JPY", "CNY",
        "KRW", "CHF", "SEK", "NOK", "DKK", "HUF", "RON", "BGN", "BYN", "MXN", "ARS", "CLP", "COP", "PEN", "SGD", "HKD", "TWD",
        "THB", "IDR", "MYR", "PHP", "VND", "ZAR", "AED", "SAR", "ILS", "QAR", "KWD", "UYU", "CRC", "GEL", "AMD", "AZN", "UZS",
    ];

    public static bool TryParse(string? text, string? defaultCurrency, out Money money)
    {
        money = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var currency = DetectCurrency(text) ?? defaultCurrency;
        if (currency is null)
            return false;

        if (!TryParseAmount(text, out var amount))
            return false;

        money = Money.Of(amount, currency);
        return true;
    }

    public static string? DetectCurrency(string text)
    {
        foreach (Match match in IsoCodeRegex().Matches(text))
        {
            var code = match.Value;
            if (IsoCodes.Contains(code))
                return code == "RUR" ? "RUB" : code;
        }

        foreach (var (marker, currency) in CurrencyMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return currency;
        }
        return null;
    }

    /// <summary>Extracts the first number, guessing which separator is the decimal one.</summary>
    public static bool TryParseAmount(string text, out decimal amount)
    {
        amount = 0;
        var match = NumberRegex().Match(text);
        if (!match.Success)
            return false;

        var raw = match.Value.Replace(" ", "").Replace(" ", "").Replace(" ", "").Replace("'", "").TrimEnd('.', ',');
        var lastDot = raw.LastIndexOf('.');
        var lastComma = raw.LastIndexOf(',');

        string normalized;
        if (lastDot >= 0 && lastComma >= 0)
        {
            // Both present: the later one is the decimal separator.
            var decimalSeparator = lastDot > lastComma ? '.' : ',';
            var thousandSeparator = decimalSeparator == '.' ? ',' : '.';
            normalized = raw.Replace(thousandSeparator.ToString(), "").Replace(decimalSeparator, '.');
        }
        else if (lastDot >= 0 || lastComma >= 0)
        {
            var separator = lastDot >= 0 ? '.' : ',';
            var occurrences = raw.Count(c => c == separator);
            var digitsAfter = raw.Length - raw.LastIndexOf(separator) - 1;
            normalized = occurrences == 1 && digitsAfter != 3
                ? raw.Replace(separator, '.')
                : raw.Replace(separator.ToString(), "");
        }
        else
        {
            normalized = raw;
        }

        return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
    }

    // Space-grouped thousands ("1 299,00") or a plain number with separators ("1,299.00").
    [GeneratedRegex(@"\d{1,3}(?:[   ]\d{3})+(?:[.,]\d+)?|\d[\d.,']*")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"\b[A-Z]{3}\b")]
    private static partial Regex IsoCodeRegex();
}
