using System.Text.Json;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.Marketplaces;

/// <summary>K4G.com marketplace. Its search API returns each product's featured (cheapest) offer in ~25 currencies.</summary>
public sealed class K4GProvider(HttpClient http) : StoreProvider(http)
{
    private const string Host = "https://k4g.com";

    public override ProviderInfo Info { get; } = new("k4g", "K4G", StoreKind.Marketplace, "https://k4g.com/")
    {
        Note = ProviderNote.Marketplace,
    };

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        var show = Math.Clamp(context.MaxResults, 10, 60);
        var url = $"{Host}/api/v1/en/search/search?q={Encode(context.Query)}&page=1&show={show}&sort=bestseller&sort_dir=1";
        using var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        return Parse(doc.RootElement, context.Region.CurrencyCode);
    }

    internal IReadOnlyList<Offer> Parse(JsonElement root, string preferredCurrency)
    {
        var offers = new List<Offer>();
        foreach (var item in root.Arr("items"))
        {
            var id = item.Str("id");
            var title = item.Str("title");
            var slug = item.Str("slug");
            // No featured offer means nobody sells it right now.
            if (item.Prop("featured_offer") is not { } offer || id is null || slug is null || string.IsNullOrWhiteSpace(title))
                continue;

            var currency = offer.Path($"price.{preferredCurrency}") is not null ? preferredCurrency : "EUR";
            var price = offer.Dec($"price.{currency}.price");
            if (price is not > 0)
                continue;

            var retail = offer.Dec($"retail_price.{currency}");
            offers.Add(CreateOffer(title.Trim(), $"{Host}/product/{slug}-{id}", Money.Of(price.Value, currency)) with
            {
                RegularPrice = retail > price ? Money.Of(retail.Value, currency) : null,
                ImageUrl = item.Arr("cover").FirstOrDefault(c => c.Str("type") == "cover_270").Str("resource")
                           ?? item.Arr("cover").FirstOrDefault().Str("resource"),
                Platform = OfferClassifier.ParsePlatform(item.Str("platform.name")),
                Region = item.Str("region.name"),
                Seller = offer.Str("owner.username"),
                // 1 = key, 2 = gift; 3 = account, 6 = digital goods (wallpapers etc.).
                // "Aim To Get" products are random-key lotteries.
                Type = item.Long("distribution") is null or 1 or 2 && item.Str("product.type.name") != "Aim To Get"
                    ? OfferType.Unknown
                    : OfferType.Other,
            });
        }
        return offers;
    }
}
