using System.Text.Json;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.Official;

/// <summary>GamersGate: authorized key store; its catalog API prices in the visitor's currency.</summary>
public sealed class GamersGateProvider(HttpClient http) : StoreProvider(http)
{
    private const string Host = "https://www.gamersgate.com";

    public override ProviderInfo Info { get; } = new("gamersgate", "GamersGate", StoreKind.Official, "https://www.gamersgate.com/");

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync($"{Host}/api/games/?query={Encode(context.Query)}&sort=default&page=1&per_page=60", cancellationToken).ConfigureAwait(false);
        return Parse(doc.RootElement);
    }

    internal IReadOnlyList<Offer> Parse(JsonElement root)
    {
        var offers = new List<Offer>();
        // "catalog" is missing altogether when nothing matches.
        foreach (var item in root.Arr("catalog"))
        {
            var title = item.Str("name");
            var link = item.Str("link");
            var currency = item.Str("currency_code");
            var price = item.Dec("raw_price");
            if (string.IsNullOrWhiteSpace(title) || link is null || currency is null || price is not > 0 || item.Bool("is_available") == false)
                continue;

            // "&#8364; 17.99"
            Money? regular = PriceParser.TryParse(Html.Decode(item.Str("baseprice")), currency, out var basePrice) && basePrice.Amount > price
                ? Money.Of(basePrice.Amount, currency)
                : null;
            offers.Add(CreateOffer(Html.Decode(title), Absolute(Host, link), Money.Of(price.Value, currency)) with
            {
                RegularPrice = regular,
                ImageUrl = item.Str("image"),
                Type = item.Str("labels.dlc") is not null ? OfferType.Dlc : OfferType.Unknown,
            });
        }
        return offers;
    }
}
