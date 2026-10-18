using System.Text.Json;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.Marketplaces;

/// <summary>
/// GGSEL marketplace (USD, EUR or RUB). Its catalog search API sits behind QRATOR, which may
/// refuse requests from time to time.
/// </summary>
public sealed class GgselProvider(HttpClient http) : StoreProvider(http)
{
    /// <summary>Content types that are shared or offline accounts, currency/top-ups or item drops.</summary>
    private static readonly HashSet<long> OtherContentTypes = [1, 9, 10, 12, 25, 31];

    private const long DlcContentType = 19;

    public override ProviderInfo Info { get; } = new("ggsel", "GGSEL", StoreKind.Marketplace, "https://ggsel.net/")
    {
        Note = ProviderNote.Marketplace,
    };

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        var (field, _) = PriceField(context.Region.CurrencyCode);
        var body = JsonSerializer.Serialize(new
        {
            search_term = context.Query,
            limit = Math.Clamp(context.MaxResults, 10, 50),
            search_after = Array.Empty<object>(),
            sort = "sortByRec",
            content_type_ids = Array.Empty<int>(),
            platforms = Array.Empty<string>(),
            currency = field,
            lang = "en",
        });
        using var doc = await PostJsonAsync("https://api.ggsel.com/elastic/goods/query", body, cancellationToken).ConfigureAwait(false);
        return Parse(doc.RootElement, context.Region.CurrencyCode);
    }

    internal IReadOnlyList<Offer> Parse(JsonElement root, string preferredCurrency)
    {
        var (field, currency) = PriceField(preferredCurrency);
        var offers = new List<Offer>();
        foreach (var item in root.Arr("data.items"))
        {
            var title = item.Str("name");
            var url = item.Str("url");
            var price = item.Dec($"price_{field}_for_one") ?? item.Dec($"price_{field}");
            if (string.IsNullOrWhiteSpace(title) || url is null || price is not > 0 || item.Bool("is_active") == false)
                continue;

            var contentType = item.Long("content_type_id");
            offers.Add(CreateOffer(Html.Decode(title), $"https://ggsel.net/en/catalog/product/{url}", Money.Of(price.Value, currency)) with
            {
                ImageUrl = item.Str("image"),
                Seller = item.Str("seller_name"),
                Type = contentType switch
                {
                    { } type when OtherContentTypes.Contains(type) => OfferType.Other,
                    DlcContentType => OfferType.Dlc,
                    _ => OfferType.Unknown,
                },
            });
        }
        return offers;
    }

    /// <summary>GGSEL names its currencies after WebMoney purses: wmz = USD, wme = EUR, wmr = RUB.</summary>
    private static (string Field, string Currency) PriceField(string preferredCurrency) => preferredCurrency switch
    {
        "EUR" => ("wme", "EUR"),
        "RUB" => ("wmr", "RUB"),
        _ => ("wmz", "USD"),
    };
}
