using System.Globalization;
using System.Text.Json;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Currency;

public interface IExchangeRateSource
{
    string Name { get; }

    Task<ExchangeRates> FetchAsync(HttpClient http, CancellationToken cancellationToken);
}

/// <summary>open.er-api.com — free, no key, ~160 currencies, updated daily.</summary>
public sealed class OpenErApiSource : IExchangeRateSource
{
    public string Name => "open.er-api.com";

    public async Task<ExchangeRates> FetchAsync(HttpClient http, CancellationToken cancellationToken)
    {
        await using var stream = await http.GetStreamAsync("https://open.er-api.com/v6/latest/USD", cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.Str("result") != "success")
            throw new InvalidDataException("open.er-api.com returned an error.");

        var updated = root.Long("time_last_update_unix") is { } unix ? DateTimeOffset.FromUnixTimeSeconds(unix) : DateTimeOffset.UtcNow;
        return new ExchangeRates
        {
            Base = root.Str("base_code") ?? "USD",
            Rates = ReadRates(root, "rates"),
            Timestamp = updated,
            Source = Name,
        };
    }

    internal static Dictionary<string, decimal> ReadRates(JsonElement root, string path)
    {
        var rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.Obj(path))
        {
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDecimal(out var rate) && rate > 0)
                rates[property.Name.ToUpperInvariant()] = rate;
        }
        return rates;
    }
}

/// <summary>Frankfurter (European Central Bank reference rates). No RUB/UAH/KZT.</summary>
public sealed class FrankfurterSource : IExchangeRateSource
{
    public string Name => "frankfurter.app (ECB)";

    public async Task<ExchangeRates> FetchAsync(HttpClient http, CancellationToken cancellationToken)
    {
        await using var stream = await http.GetStreamAsync("https://api.frankfurter.app/latest?from=USD", cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        var date = DateTimeOffset.TryParse(root.Str("date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : DateTimeOffset.UtcNow;
        return new ExchangeRates
        {
            Base = "USD",
            Rates = OpenErApiSource.ReadRates(root, "rates"),
            Timestamp = date,
            Source = Name,
        };
    }
}

/// <summary>Central Bank of Russia rates via cbr-xml-daily.ru (base RUB).</summary>
public sealed class CbrSource : IExchangeRateSource
{
    public string Name => "cbr-xml-daily.ru (CBR)";

    public async Task<ExchangeRates> FetchAsync(HttpClient http, CancellationToken cancellationToken)
    {
        await using var stream = await http.GetStreamAsync("https://www.cbr-xml-daily.ru/latest.js", cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        var timestamp = root.Long("timestamp") is { } unix ? DateTimeOffset.FromUnixTimeSeconds(unix) : DateTimeOffset.UtcNow;
        return new ExchangeRates
        {
            Base = "RUB",
            Rates = OpenErApiSource.ReadRates(root, "rates"),
            Timestamp = timestamp,
            Source = Name,
        };
    }
}

/// <summary>National Bank of Ukraine official rates (UAH per unit of each currency).</summary>
public sealed class NbuSource : IExchangeRateSource
{
    public string Name => "bank.gov.ua (NBU)";

    public async Task<ExchangeRates> FetchAsync(HttpClient http, CancellationToken cancellationToken)
    {
        await using var stream = await http.GetStreamAsync("https://bank.gov.ua/NBUStatService/v1/statdirectory/exchange?json", cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("NBU returned an unexpected response.");

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            // NBU gives UAH per 1 unit; ExchangeRates wants units per 1 UAH.
            if (item.Str("cc") is { Length: 3 } code && item.Dec("rate") is > 0 and var uahPerUnit)
                rates[code.ToUpperInvariant()] = 1m / uahPerUnit;
        }

        return new ExchangeRates
        {
            Base = "UAH",
            Rates = rates,
            Timestamp = DateTimeOffset.UtcNow,
            Source = Name,
        };
    }
}
