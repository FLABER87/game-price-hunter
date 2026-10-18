using System.Text.Json;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.Marketplaces;

/// <summary>
/// Plati.market (Digiseller marketplace). Uses the Digiseller cataloguer API filtered to the
/// "keys" and "gifts" content types, which leaves out shared accounts and services. The old
/// search.ashx API is kept as a fallback.
/// </summary>
public sealed class PlatiProvider(HttpClient http) : StoreProvider(http)
{
    private const string CatalogApi = "https://api.digiseller.com/api/cataloguer/front/products";
    private const string LegacyApi = "https://plati.market/api/search.ashx";

    /// <summary>Digiseller "content type" attribute values.</summary>
    private const string KeysAttribute = "37800-80939";
    private const string GiftsAttribute = "37800-80943";

    private static readonly string[] Currencies = ["USD", "EUR", "RUB", "UAH"];

    public override ProviderInfo Info { get; } = new("plati", "Plati.market", StoreKind.Marketplace, "https://plati.market/")
    {
        Note = "Marketplace: prices from independent sellers",
    };

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        var currency = Currencies.Contains(context.Region.CurrencyCode) ? context.Region.CurrencyCode : "USD";
        var count = Math.Clamp(context.MaxResults, 10, 100);

        var tasks = new[] { KeysAttribute, GiftsAttribute }
            .Select(attribute => FetchCatalogAsync(context.Query, currency, count, attribute, cancellationToken))
            .ToArray();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception) when (tasks.Any(t => t.IsCompletedSuccessfully))
        {
            // One of the two content types failed; the other still has useful results.
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not ProviderBlockedException)
        {
            return await FetchLegacyAsync(context.Query, currency, count, cancellationToken).ConfigureAwait(false);
        }

        return tasks.Where(t => t.IsCompletedSuccessfully).SelectMany(t => t.Result).ToList();
    }

    private async Task<IReadOnlyList<Offer>> FetchCatalogAsync(string query, string currency, int count, string attribute, CancellationToken cancellationToken)
    {
        var url = $"{CatalogApi}?productName={Encode(query)}&ownerId=plati&currency={currency}&page=1&count={count}" +
                  $"&sortBy=popular&getProductsRecursive=true&lang=en-US&attributes={attribute}";
        using var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        return Parse(doc.RootElement);
    }

    private async Task<IReadOnlyList<Offer>> FetchLegacyAsync(string query, string currency, int count, CancellationToken cancellationToken)
    {
        var url = $"{LegacyApi}?query={Encode(query)}&pagesize={count}&pagenum=1&visibleOnly=true&response=json";
        using var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        return ParseLegacy(doc.RootElement, currency);
    }

    /// <summary>Parses a Digiseller cataloguer response (content.items[]).</summary>
    internal IReadOnlyList<Offer> Parse(JsonElement root)
    {
        var offers = new List<Offer>();
        foreach (var item in root.Arr("content.items"))
        {
            var id = item.Str("product_id");
            var title = PickName(item);
            var price = item.Dec("price");
            var currency = item.Str("currency");
            if (id is null || string.IsNullOrWhiteSpace(title) || price is not > 0 || currency is null)
                continue;

            var amount = Math.Round(price.Value, 2);
            var before = item.Dec("price_before_discount");
            var slug = item.Str("name_url");
            offers.Add(CreateOffer(Html.Decode(title), ProductUrl(slug, id), Money.Of(amount, currency)) with
            {
                RegularPrice = before > amount ? Money.Of(Math.Round(before.Value, 2), currency) : null,
                ImageUrl = $"https://digiseller2.mycdn.ink/imgwebp.ashx?id_d={id}&w=328&h=328",
                Seller = item.Str("seller_name"),
            });
        }
        return offers;
    }

    /// <summary>Parses the legacy plati.market/api/search.ashx response (items[]).</summary>
    internal IReadOnlyList<Offer> ParseLegacy(JsonElement root, string currency)
    {
        var field = currency switch
        {
            "EUR" => "price_eur",
            "RUB" => "price_rur",
            "UAH" => "price_uah",
            _ => "price_usd",
        };
        if (field == "price_usd")
            currency = "USD";

        var offers = new List<Offer>();
        foreach (var item in root.Arr("items"))
        {
            var id = item.Str("id");
            var title = item.Str("name_eng") is { Length: > 0 } english ? english : item.Str("name");
            var price = item.Dec(field);
            if (id is null || string.IsNullOrWhiteSpace(title) || price is not > 0)
                continue;

            var image = item.Str("image");
            offers.Add(CreateOffer(Html.Decode(title), ProductUrl(item.Str("name_translit_eng") ?? item.Str("name_translit"), id), Money.Of(Math.Round(price.Value, 2), currency)) with
            {
                ImageUrl = string.IsNullOrEmpty(image) ? null : Absolute(Info.HomeUrl, image),
                Seller = item.Str("seller_name"),
            });
        }
        return offers;
    }

    private static string? PickName(JsonElement item)
    {
        string? first = null;
        foreach (var name in item.Arr("name"))
        {
            var value = name.Str("value");
            if (string.IsNullOrWhiteSpace(value))
                continue;
            if (name.Str("locale") == "en-US")
                return value;
            first ??= value;
        }
        return first ?? item.Str("name");
    }

    private static string ProductUrl(string? slug, string id) =>
        string.IsNullOrEmpty(slug) ? $"https://plati.market/itm/{id}" : $"https://plati.market/itm/{slug}/{id}";
}
