using System.Text.Json.Serialization;

namespace GamePriceHunter.Core.Currency;

/// <summary>Exchange rates as units of each currency per one unit of <see cref="Base"/>.</summary>
public sealed record ExchangeRates
{
    public required string Base { get; init; }

    public required IReadOnlyDictionary<string, decimal> Rates { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required string Source { get; init; }

    [JsonIgnore]
    public IEnumerable<string> Currencies => Rates.Keys.Append(Base).Distinct();

    public bool Supports(string currency) =>
        string.Equals(currency, Base, StringComparison.OrdinalIgnoreCase) || Rates.ContainsKey(currency.ToUpperInvariant());

    /// <summary>Converts through the base currency; null when either currency is unknown.</summary>
    public decimal? Convert(decimal amount, string from, string to)
    {
        from = from.ToUpperInvariant();
        to = to.ToUpperInvariant();
        if (from == to)
            return amount;

        var fromRate = RateOf(from);
        var toRate = RateOf(to);
        if (fromRate is null or 0 || toRate is null)
            return null;
        return amount / fromRate.Value * toRate.Value;
    }

    private decimal? RateOf(string currency)
    {
        if (currency == Base)
            return 1m;
        return Rates.TryGetValue(currency, out var rate) ? rate : null;
    }
}
