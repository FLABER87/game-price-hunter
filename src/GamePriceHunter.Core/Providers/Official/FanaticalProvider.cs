using System.Text.Json;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.Official;

/// <summary>
/// Fanatical. The site hands out a short-lived Algolia search key (region filter built in),
/// which is cached until shortly before it expires.
/// </summary>
public sealed class FanaticalProvider(HttpClient http) : StoreProvider(http)
{
    private const string AlgoliaAppId = "W2M9492DDV";
    private static readonly string[] SupportedCurrencies = ["USD", "EUR", "GBP", "CAD", "AUD", "RUB", "JPY"];

    private readonly SemaphoreSlim _keyLock = new(1, 1);
    private string? _key;
    private DateTimeOffset _keyExpires;

    public override ProviderInfo Info { get; } = new("fanatical", "Fanatical", StoreKind.Official, "https://www.fanatical.com/");

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await QueryAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            // Expired search key ("validUntil parameter expired"): fetch a new one once.
            _key = null;
            return await QueryAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<Offer>> QueryAsync(SearchContext context, CancellationToken cancellationToken)
    {
        var key = await GetKeyAsync(cancellationToken).ConfigureAwait(false);
        var body = JsonSerializer.Serialize(new
        {
            query = context.Query,
            hitsPerPage = Math.Clamp(context.MaxResults, 10, 50),
            page = 0,
            facetFilters = new[] { new[] { "type:game", "type:dlc", "type:bundle", "type:pick-and-mix" } },
        });

        using var doc = await PostJsonAsync($"https://{AlgoliaAppId.ToLowerInvariant()}-dsn.algolia.net/1/indexes/fan/query", body, cancellationToken, request =>
        {
            request.Headers.Add("X-Algolia-Application-Id", AlgoliaAppId);
            request.Headers.Add("X-Algolia-API-Key", key);
        }).ConfigureAwait(false);

        var currency = SupportedCurrencies.Contains(context.Region.CurrencyCode) ? context.Region.CurrencyCode : "EUR";
        return Parse(doc.RootElement, currency);
    }

    internal IReadOnlyList<Offer> Parse(JsonElement root, string currency)
    {
        var offers = new List<Offer>();
        foreach (var hit in root.Arr("hits"))
        {
            var name = hit.Str("name");
            var slug = hit.Str("slug");
            var type = hit.Str("type") ?? "game";
            var price = hit.Dec($"price.{currency}");
            if (name is null || slug is null || price is null || hit.Bool("hide_price") == true)
                continue;

            var full = hit.Dec($"fullPrice.{currency}");
            var drm = hit.Arr("drm").Select(d => d.GetString()).FirstOrDefault();
            var cover = hit.Str("cover");

            offers.Add(CreateOffer(name, $"https://www.fanatical.com/en/{type}/{slug}", Money.Of(price.Value, currency)) with
            {
                RegularPrice = full > price ? Money.Of(full.Value, currency) : null,
                ImageUrl = cover is null ? null : $"https://fanatical.imgix.net/product/original/{cover}?auto=compress,format&w=460",
                Type = type switch
                {
                    "game" => OfferType.Game,
                    "dlc" => OfferType.Dlc,
                    "bundle" or "pick-and-mix" => OfferType.Bundle,
                    _ => OfferType.Unknown,
                },
                Platform = OfferClassifier.ParsePlatform(drm),
            });
        }
        return offers;
    }

    private async Task<string> GetKeyAsync(CancellationToken cancellationToken)
    {
        await _keyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_key is not null && DateTimeOffset.UtcNow < _keyExpires)
                return _key;

            using var doc = await GetJsonAsync("https://www.fanatical.com/api/algolia/key", cancellationToken).ConfigureAwait(false);
            _key = doc.RootElement.Str("key") ?? throw new InvalidDataException("Fanatical did not return a search key.");

            // The key is valid for ~10 minutes; the site itself refreshes it after ~6.
            var validUntil = doc.RootElement.Long("validUntil");
            _keyExpires = validUntil is > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(validUntil.Value)
                : DateTimeOffset.UtcNow.AddMinutes(5);
            if (_keyExpires > DateTimeOffset.UtcNow.AddMinutes(6))
                _keyExpires = DateTimeOffset.UtcNow.AddMinutes(5);
            return _key;
        }
        finally
        {
            _keyLock.Release();
        }
    }
}
