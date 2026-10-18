using System.Text.Json;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.Marketplaces;

/// <summary>
/// Instant Gaming: key shop (EUR). The search page embeds its first page of results as
/// <c>window.searchResults = {...};</c>, so no search API key is needed.
/// </summary>
public sealed class InstantGamingProvider(HttpClient http) : StoreProvider(http)
{
    private const string Host = "https://www.instant-gaming.com";

    public override ProviderInfo Info { get; } = new("instantgaming", "Instant Gaming", StoreKind.KeyShop, "https://www.instant-gaming.com/");

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        var html = await GetStringAsync($"{Host}/en/search/?query={Encode(context.Query)}", cancellationToken).ConfigureAwait(false);
        return Parse(html);
    }

    internal IReadOnlyList<Offer> Parse(string html)
    {
        var json = Html.ExtractJson(html, "window.searchResults =")
            ?? throw new InvalidDataException("Instant Gaming search results not found in the page.");
        using var doc = JsonDocument.Parse(json);

        var offers = new List<Offer>();
        foreach (var hit in doc.RootElement.Arr("hits"))
        {
            var id = hit.Str("prod_id");
            var seoName = hit.Str("seo_name");
            var title = hit.Str("fullname") ?? hit.Str("name");
            var price = hit.Dec("price");
            // Price "0.00" is a placeholder for products that are not on sale yet.
            if (id is null || seoName is null || string.IsNullOrWhiteSpace(title) || price is not > 0 || hit.Bool("has_stock") != true)
                continue;

            var retail = hit.Dec("retail");
            offers.Add(CreateOffer(Html.Decode(title), $"{Host}/en/{id}-buy-{seoName}/", Money.Of(price.Value, "EUR")) with
            {
                RegularPrice = retail > price ? Money.Of(retail.Value, "EUR") : null,
                ImageUrl = $"https://gaming-cdn.com/images/products/{id}/616x353/{seoName}-cover.jpg",
                Platform = OfferClassifier.ParsePlatform(hit.Str("type")),
                Region = hit.Str("region"),
                Type = hit.Bool("is_prepaid") == true || hit.Bool("is_subscription") == true ? OfferType.Other
                    : hit.Bool("is_dlc") == true ? OfferType.Dlc
                    : OfferType.Unknown,
            });
        }
        return offers;
    }
}
