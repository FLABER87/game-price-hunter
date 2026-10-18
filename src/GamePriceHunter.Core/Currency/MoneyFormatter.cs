using System.Globalization;
using GamePriceHunter.Core.Globalization;
using GamePriceHunter.Core.Models;

namespace GamePriceHunter.Core.Currency;

/// <summary>
/// Formats money the way the reader's culture writes it: separators, the place of the symbol and the space
/// next to it come from the culture (CLDR data in Windows), the number of decimals from the currency.
/// Ruble prices read "1 499 ₽" in ru-RU, euro prices "1.234,56 €" in de-DE and "€1,234.56" in en-US.
/// </summary>
public static class MoneyFormatter
{
    private const char NoBreakSpace = ' ';
    private const char MinusSign = '−';

    /// <summary>Currencies shown without minor units: game prices in them never use kopecks, sen or fillér.</summary>
    private static readonly HashSet<string> ZeroDecimalCurrencies =
        ["JPY", "KRW", "VND", "CLP", "ISK", "HUF", "IDR", "COP", "KZT", "UZS", "PYG", "UGX", "RUB", "UAH"];

    /// <summary>
    /// Symbols for currencies outside their home culture, where they are clear on their own. Every other
    /// currency is written as its ISO code there ("SEK", "ARS"), since its symbol ("kr", "$") is shared.
    /// </summary>
    private static readonly Dictionary<string, string> Symbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = "$", ["EUR"] = "€", ["GBP"] = "£", ["JPY"] = "¥", ["CNY"] = "CN¥", ["KRW"] = "₩", ["INR"] = "₹",
        ["RUB"] = "₽", ["UAH"] = "₴", ["KZT"] = "₸", ["TRY"] = "₺", ["PLN"] = "zł", ["CZK"] = "Kč", ["BRL"] = "R$",
        ["CAD"] = "CA$", ["AUD"] = "A$", ["NZD"] = "NZ$", ["MXN"] = "MX$", ["HKD"] = "HK$", ["TWD"] = "NT$",
        ["ILS"] = "₪", ["VND"] = "₫", ["PHP"] = "₱",
    };

    public static int Decimals(string currency) => ZeroDecimalCurrencies.Contains(currency.ToUpperInvariant()) ? 0 : 2;

    /// <param name="culture">The format culture; a specific one (ru-RU, not ru), so its own currency is known.</param>
    public static string Format(Money money, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var decimals = Decimals(money.Currency);
        var format = (NumberFormatInfo)culture.NumberFormat.Clone();
        format.CurrencySymbol = Symbol(money.Currency, culture);
        format.CurrencyDecimalDigits = decimals;

        // Letters touching digits read badly ("SEK12.00", "12,00zł"), so such symbols get a space.
        var symbol = format.CurrencySymbol;
        if (symbol.Length > 0 && format.CurrencyPositivePattern == 0 && char.IsLetter(symbol[^1]))
            format.CurrencyPositivePattern = 2;
        else if (symbol.Length > 0 && format.CurrencyPositivePattern == 1 && char.IsLetter(symbol[0]))
            format.CurrencyPositivePattern = 3;

        var amount = Math.Round(money.Amount, decimals, MidpointRounding.AwayFromZero);
        return KeepTogether(amount.ToString("C", format));
    }

    /// <summary>The symbol of <paramref name="currency"/> as a reader of <paramref name="culture"/> expects it.</summary>
    public static string Symbol(string currency, CultureInfo culture)
    {
        var code = currency.ToUpperInvariant();
        var symbol = Symbols.GetValueOrDefault(code, code);
        var home = HomeCurrency(culture);
        if (code == home)
        {
            // Windows lets the user clear the symbol of the regional format.
            return culture.NumberFormat.CurrencySymbol is { Length: > 0 } own ? own : symbol;
        }

        if (home is null)
            return symbol;

        // Where the local currency has a dollar sign or the same symbol, the foreign one is spelled out:
        // US$ in Mexico, Canada and Brazil, JP¥ in China.
        var local = culture.NumberFormat.CurrencySymbol;
        if (code == "USD" && local.Contains('$'))
            return "US$";
        if (symbol == local)
            return code == "JPY" ? "JP¥" : code;
        return symbol;
    }

    /// <summary>A discount: "−25%" in en-US, "−25 %" in de-DE, "−%25" in tr-TR.</summary>
    public static string Discount(int percent, CultureInfo culture) =>
        MinusSign + Percent(percent / 100.0, culture);

    /// <summary>A share of 0..1 as a whole percentage in the culture's style.</summary>
    public static string Percent(double fraction, CultureInfo culture) =>
        KeepTogether(fraction.ToString("P0", culture));

    /// <summary>"EUR — евро" style names for currency pickers, in the language of <paramref name="uiCulture"/>.</summary>
    public static IReadOnlyList<(string Code, string Name)> KnownCurrencies(CultureInfo uiCulture) =>
        CultureInfo.GetCultures(CultureTypes.SpecificCultures)
            .Select(c =>
            {
                try { return new RegionInfo(c.Name); }
                catch (ArgumentException) { return null; }
            })
            .OfType<RegionInfo>()
            .GroupBy(r => r.ISOCurrencySymbol)
            .Select(g => (g.Key, Name: CurrencyName(g.Key, uiCulture) ?? g.First().CurrencyEnglishName))
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToList();

    private static string? CurrencyName(string code, CultureInfo uiCulture)
    {
        var name = IcuNames.Currency(code, uiCulture.Name);
        if (string.IsNullOrEmpty(name))
            return null;
        // "евро", "российский рубль": in a list they start with a capital letter.
        return uiCulture.TextInfo.ToUpper(name[..1]) + name[1..];
    }

    private static string? HomeCurrency(CultureInfo culture)
    {
        if (culture.IsNeutralCulture || culture.Name.Length == 0)
            return null;
        try
        {
            return new RegionInfo(culture.Name).ISOCurrencySymbol;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>.NET puts a plain space between number and symbol; a line break must not split them.</summary>
    private static string KeepTogether(string text) => text.Replace(' ', NoBreakSpace);
}
