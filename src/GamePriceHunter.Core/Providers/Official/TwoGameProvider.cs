using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.Official;

/// <summary>
/// 2game: authorized key store on Shopify with per-country storefronts ("/en-sk", "/en-gb"...)
/// priced in local currency. Search uses the storefront's predictive-search JSON (10 hits);
/// the storefront currency is read once per market from a product's JSON.
/// </summary>
public sealed class TwoGameProvider(HttpClient http) : StoreProvider(http)
{
    private const string Host = "https://www.2game.com";

    /// <summary>Storefront path per country, from the site's hreflang links. The US store is the root.</summary>
    private static readonly Dictionary<string, string> Markets = BuildMarkets();

    private readonly ConcurrentDictionary<string, string> _currencies = new();

    public override ProviderInfo Info { get; } = new("2game", "2game", StoreKind.Official, "https://www.2game.com/");

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        var market = Markets.GetValueOrDefault(context.Region.CountryCode, string.Empty);
        JsonDocument doc;
        try
        {
            doc = await SuggestAsync(market, context.Query, cancellationToken).ConfigureAwait(false);
        }
        // A storefront that is gone (404), or one in a language Shopify's search does not support:
        // ja-jp, th-th and zh-cn answer 417 "Unsupported buyer locale". The root (US) store still sells there.
        catch (HttpRequestException ex) when ((ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.ExpectationFailed) && market.Length > 0)
        {
            market = string.Empty;
            doc = await SuggestAsync(market, context.Query, cancellationToken).ConfigureAwait(false);
        }

        using (doc)
        {
            var handle = doc.RootElement.Arr("resources.results.products").Select(p => p.Str("handle")).FirstOrDefault(h => h is not null);
            if (handle is null)
                return [];
            var currency = await GetCurrencyAsync(market, handle, cancellationToken).ConfigureAwait(false);
            return Parse(doc.RootElement, currency);
        }
    }

    internal IReadOnlyList<Offer> Parse(JsonElement root, string currency)
    {
        var offers = new List<Offer>();
        foreach (var product in root.Arr("resources.results.products"))
        {
            var title = product.Str("title");
            var url = product.Str("url");
            var price = product.Dec("price");
            if (string.IsNullOrWhiteSpace(title) || url is null || price is not > 0 || product.Bool("available") == false)
                continue;

            // Drop the search tracking parameters ("?_pos=1&_psq=...").
            var query = url.IndexOf('?');
            if (query >= 0)
                url = url[..query];

            var compareAt = product.Dec("compare_at_price_max");
            var handle = product.Str("handle") ?? string.Empty;
            offers.Add(CreateOffer(Html.Decode(title), Absolute(Host, url), Money.Of(price.Value, currency)) with
            {
                RegularPrice = compareAt > price ? Money.Of(compareAt.Value, currency) : null,
                ImageUrl = product.Str("image") ?? product.Str("featured_image.url"),
                Platform = PlatformFromHandle(handle),
                Type = product.Str("type") is { } type && type.Contains("dlc", StringComparison.OrdinalIgnoreCase) ? OfferType.Dlc : OfferType.Unknown,
            });
        }
        return offers;
    }

    /// <summary>Handles end with the DRM: "glitchpunk-steam", "...-steam-key", "...-epic-games".</summary>
    internal static Platform PlatformFromHandle(string handle)
    {
        var parts = handle.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? Platform.Unknown : OfferClassifier.DetectPlatform(string.Join(' ', parts.TakeLast(2)));
    }

    private Task<JsonDocument> SuggestAsync(string market, string query, CancellationToken cancellationToken) =>
        GetJsonAsync($"{MarketBase(market)}/search/suggest.json?q={Encode(query)}&resources%5Btype%5D=product&resources%5Blimit%5D=10", cancellationToken);

    private async Task<string> GetCurrencyAsync(string market, string handle, CancellationToken cancellationToken)
    {
        if (_currencies.TryGetValue(market, out var cached))
            return cached;

        using var doc = await GetJsonAsync($"{MarketBase(market)}/products/{Uri.EscapeDataString(handle)}.json", cancellationToken).ConfigureAwait(false);
        var currency = doc.RootElement.Str("product.variants.0.price_currency")
            ?? throw new InvalidDataException("2game storefront currency not found.");
        _currencies[market] = currency;
        return currency;
    }

    private static string MarketBase(string market) => market.Length == 0 ? Host : $"{Host}/{market}";

    private static Dictionary<string, string> BuildMarkets()
    {
        var markets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var country in "AE AU BG CA CL CO CR CY EE GB HK HR IE IL IN KR KZ LT LV MT MX MY NO NZ PE PH PL PT QA SA SG SI SK TW UY ZA BR".Split(' '))
            markets[country] = "en-" + country.ToLowerInvariant();

        // Countries whose storefront exists only in the local language.
        foreach (var path in "de-at de-de el-gr es-es fi-fi fr-be fr-ch fr-lu fr-fr id-id it-it ja-jp nl-nl th-th vi-vn zh-cn".Split(' '))
            markets[path[3..].ToUpperInvariant()] = path;
        return markets;
    }
}
