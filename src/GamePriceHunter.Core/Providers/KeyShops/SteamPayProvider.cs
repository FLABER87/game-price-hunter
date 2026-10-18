using System.Text.Json;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.KeyShops;

/// <summary>SteamPay.com: key shop with RUB prices.</summary>
public sealed class SteamPayProvider(HttpClient http) : StoreProvider(http)
{
    private const string Host = "https://steampay.com";

    public override ProviderInfo Info { get; } = new("steampay", "SteamPay", StoreKind.KeyShop, "https://steampay.com/") { Note = ProviderNote.RussianStore };

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync($"{Host}/ajax/search?q={Encode(context.Query)}&available=true", cancellationToken,
            request => request.Headers.Add("X-Requested-With", "XMLHttpRequest")).ConfigureAwait(false);
        return Parse(doc.RootElement);
    }

    internal IReadOnlyList<Offer> Parse(JsonElement root)
    {
        var offers = new List<Offer>();
        foreach (var product in root.Arr("products"))
        {
            var name = product.Str("name");
            var url = product.Str("url");
            // "3395 ₽" when in stock; "СКОРО" (coming soon) or "—" otherwise.
            if (name is null || url is null || !PriceParser.TryParse(product.Str("price"), "RUB", out var price) || price.Amount <= 0)
                continue;

            var image = product.Str("img");
            offers.Add(CreateOffer(Html.Decode(name), Absolute(Host, url), price) with
            {
                RegularPrice = GabeStoreProvider.RegularFromDiscount(price, product.Str("discount")),
                ImageUrl = image is null ? null : Absolute(Host, image),
                Platform = OfferClassifier.ParsePlatform(product.Str("activation.title")),
            });
        }
        return offers;
    }
}
