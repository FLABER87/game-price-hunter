using System.Globalization;
using System.Text.Json;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.Official;

/// <summary>GOG.com catalog API with regional prices.</summary>
public sealed class GogProvider(HttpClient http) : StoreProvider(http)
{
    public override ProviderInfo Info { get; } = new("gog", "GOG", StoreKind.Official, "https://www.gog.com/");

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        var currency = context.Region.CurrencyCode;
        var (offers, products) = await QueryAsync(context, currency, cancellationToken).ConfigureAwait(false);
        // For most currencies GOG doesn't sell in it answers in dollars, but for a few (JPY, BYN) it
        // lists the games without any price: ask in dollars then.
        if (offers.Count == 0 && products > 0 && !string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase))
            (offers, _) = await QueryAsync(context, "USD", cancellationToken).ConfigureAwait(false);
        return offers;
    }

    private async Task<(IReadOnlyList<Offer> Offers, int Products)> QueryAsync(SearchContext context, string currency, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(context.MaxResults, 10, 100);
        var url = $"https://catalog.gog.com/v1/catalog?query=like:{Encode(context.Query)}&limit={limit}&page=1" +
                  $"&countryCode={context.Region.CountryCode}&currencyCode={currency}&locale=en-US&order=desc:score";
        using var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        return (Parse(doc.RootElement), doc.RootElement.Arr("products").Count());
    }

    internal IReadOnlyList<Offer> Parse(JsonElement root)
    {
        var offers = new List<Offer>();
        foreach (var product in root.Arr("products"))
        {
            var title = product.Str("title");
            var currency = product.Str("price.finalMoney.currency");
            if (title is null || currency is null || !TryAmount(product, "price.finalMoney.amount", out var final))
                continue;

            var link = product.Str("storeLink") ?? $"https://www.gog.com/en/game/{product.Str("slug")}";
            Money? regular = TryAmount(product, "price.baseMoney.amount", out var baseAmount) && baseAmount > final
                ? Money.Of(baseAmount, currency)
                : null;

            offers.Add(CreateOffer(title, link, Money.Of(final, currency)) with
            {
                RegularPrice = regular,
                ImageUrl = product.Str("coverHorizontal"),
                Type = product.Str("productType") switch
                {
                    "game" => OfferType.Game,
                    "dlc" => OfferType.Dlc,
                    "pack" => OfferType.Bundle,
                    _ => OfferType.Unknown,
                },
                Platform = Platform.Gog,
            });
        }
        return offers;
    }

    private static bool TryAmount(JsonElement product, string path, out decimal amount) =>
        decimal.TryParse(product.Str(path), NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
}
