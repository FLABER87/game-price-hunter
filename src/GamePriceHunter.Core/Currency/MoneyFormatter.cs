using System.Collections.Frozen;
using System.Globalization;
using GamePriceHunter.Core.Models;

namespace GamePriceHunter.Core.Currency;

/// <summary>Formats money with the currency's own symbol and number of decimals.</summary>
public static class MoneyFormatter
{
    private static readonly HashSet<string> ZeroDecimalCurrencies =
        ["JPY", "KRW", "VND", "CLP", "ISK", "HUF", "IDR", "COP", "KZT", "UZS", "PYG", "UGX"];

    // Symbols that read better than what Windows reports for some regions.
    private static readonly Dictionary<string, string> PreferredSymbols = new()
    {
        ["USD"] = "$", ["EUR"] = "€", ["GBP"] = "£", ["RUB"] = "₽", ["UAH"] = "₴", ["KZT"] = "₸", ["TRY"] = "₺",
        ["INR"] = "₹", ["JPY"] = "¥", ["CNY"] = "CN¥", ["BRL"] = "R$", ["CAD"] = "CA$", ["AUD"] = "A$", ["PLN"] = "zł",
        ["CZK"] = "Kč",
    };

    private static readonly Lazy<FrozenDictionary<string, string>> Symbols = new(BuildSymbols);

    private static readonly HashSet<string> SymbolAfterAmount = ["PLN", "CZK", "SEK", "NOK", "DKK", "HUF", "RON", "BGN", "UAH", "RUB", "KZT", "BYN"];

    public static string Symbol(string currency) =>
        Symbols.Value.TryGetValue(currency.ToUpperInvariant(), out var symbol) ? symbol : currency.ToUpperInvariant();

    public static int Decimals(string currency) => ZeroDecimalCurrencies.Contains(currency.ToUpperInvariant()) ? 0 : 2;

    public static string Format(Money money, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var decimals = Decimals(money.Currency);
        var number = Math.Round(money.Amount, decimals, MidpointRounding.AwayFromZero).ToString("N" + decimals, culture);
        var symbol = Symbol(money.Currency);
        if (symbol.Length > 2 && symbol == money.Currency)
            return $"{number} {symbol}";
        return SymbolAfterAmount.Contains(money.Currency.ToUpperInvariant()) ? $"{number} {symbol}" : $"{symbol}{number}";
    }

    /// <summary>"EUR — Euro" style names for currency pickers.</summary>
    public static IReadOnlyList<(string Code, string Name)> KnownCurrencies() =>
        CultureInfo.GetCultures(CultureTypes.SpecificCultures)
            .Select(c =>
            {
                try { return new RegionInfo(c.Name); }
                catch (ArgumentException) { return null; }
            })
            .OfType<RegionInfo>()
            .GroupBy(r => r.ISOCurrencySymbol)
            .Select(g => (g.Key, g.First().CurrencyEnglishName))
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToList();

    private static FrozenDictionary<string, string> BuildSymbols()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                map.TryAdd(region.ISOCurrencySymbol, region.CurrencySymbol);
            }
            catch (ArgumentException)
            {
            }
        }
        foreach (var (code, symbol) in PreferredSymbols)
            map[code] = symbol;
        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
