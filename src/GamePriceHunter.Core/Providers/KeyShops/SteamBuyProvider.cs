using System.Text.Json;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.KeyShops;

/// <summary>SteamBuy.com: key and gift shop; its catalog API returns RUB, UAH, KZT and EUR prices at once.</summary>
public sealed class SteamBuyProvider(HttpClient http) : StoreProvider(http)
{
    private const string Host = "https://steambuy.com";

    public override ProviderInfo Info { get; } = new("steambuy", "SteamBuy", StoreKind.KeyShop, "https://steambuy.com/") { Note = ProviderNote.RussianStore };

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync($"{Host}/sb/api/catalog?q={Encode(context.Query)}&page=1", cancellationToken).ConfigureAwait(false);
        return Parse(doc.RootElement, context.Region.CurrencyCode);
    }

    internal IReadOnlyList<Offer> Parse(JsonElement root, string preferredCurrency)
    {
        // Price fields per currency; RUB is the plain "price".
        var (suffix, currency) = preferredCurrency switch
        {
            "EUR" => ("_eur", "EUR"),
            "UAH" => ("_uah", "UAH"),
            "KZT" => ("_kzt", "KZT"),
            _ => ("", "RUB"),
        };

        var imageBase = root.Str("staticBaseUrl") ?? "https://steammachine.ru";
        var imageExt = root.Str("staticImgExt") ?? "webp";
        var offers = new List<Offer>();
        foreach (var item in root.Arr("items"))
        {
            var title = item.Str("title");
            var url = item.Str("url");
            var price = item.Dec("price" + suffix);
            if (title is null || url is null || price is not > 0 || item.Long("nal") != 1)
                continue;

            var regular = item.Dec($"price_rrc{suffix}");
            var activation = item.Str("activation");
            offers.Add(CreateOffer(title, Absolute(Host, url), Money.Of(price.Value, currency)) with
            {
                RegularPrice = regular > price ? Money.Of(regular.Value, currency) : null,
                ImageUrl = item.Str("id_good") is { } id ? $"{imageBase}/slider/{id}-308x177.{imageExt}" : null,
                Platform = OfferClassifier.ParsePlatform(activation),
                Type = item.Str("category") switch
                {
                    "dlc" => OfferType.Dlc,
                    "games" => OfferType.Unknown,
                    _ => OfferType.Unknown,
                },
                // Region-specific gifts carry the region in the URL slug ("...-russia/").
                Region = url.Contains("-russia", StringComparison.OrdinalIgnoreCase) ? "RU" : null,
            });
        }
        return offers;
    }
}
