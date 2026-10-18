using System.Text.Json;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.Marketplaces;

/// <summary>Driffle marketplace (EUR); public search API with the lowest offer per product.</summary>
public sealed class DriffleProvider(HttpClient http) : StoreProvider(http)
{
    public override ProviderInfo Info { get; } = new("driffle", "Driffle", StoreKind.Marketplace, "https://driffle.com/")
    {
        Note = "Marketplace: prices from independent sellers",
    };

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        // The API caps a page at 50 products.
        var limit = Math.Clamp(context.MaxResults, 10, 50);
        using var doc = await GetJsonAsync($"https://search.driffle.com/products/v3/list?q={Encode(context.Query)}&limit={limit}&page=1", cancellationToken).ConfigureAwait(false);
        return Parse(doc.RootElement);
    }

    internal IReadOnlyList<Offer> Parse(JsonElement root)
    {
        var offers = new List<Offer>();
        foreach (var item in root.Arr("data"))
        {
            var title = item.Str("title");
            var slug = item.Str("slug");
            // No price means no seller has stock.
            var price = item.Dec("price");
            if (string.IsNullOrWhiteSpace(title) || slug is null || price is not > 0)
                continue;

            var mrp = item.Dec("mrp");
            var platform = item.Str("platform");
            offers.Add(CreateOffer(title.Trim(), $"https://driffle.com/{slug}", Money.Of(price.Value, "EUR")) with
            {
                RegularPrice = mrp > price ? Money.Of(mrp.Value, "EUR") : null,
                ImageUrl = item.Str("image"),
                Platform = OfferClassifier.ParsePlatform(platform),
                Region = item.Str("regionName"),
                // "Steam Account", "EA Play Account": shared accounts, not keys.
                Type = platform?.Contains("Account", StringComparison.OrdinalIgnoreCase) == true ? OfferType.Other : OfferType.Unknown,
            });
        }
        return offers;
    }
}
