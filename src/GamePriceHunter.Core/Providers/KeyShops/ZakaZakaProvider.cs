using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.KeyShops;

/// <summary>Zaka-Zaka.com: key shop with RUB prices; search returns an HTML fragment of tiles.</summary>
public sealed class ZakaZakaProvider(HttpClient http) : StoreProvider(http)
{
    private const string Host = "https://zaka-zaka.com";

    public override ProviderInfo Info { get; } = new("zakazaka", "Zaka-Zaka", StoreKind.KeyShop, "https://zaka-zaka.com/") { Note = RegionalNotes.Russia };

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        var offers = new List<Offer>();
        var path = $"{Host}/search/ask/{Encode(context.Query)}/sort/name.asc";
        for (var offset = 0; offset < 36; offset += 12)
        {
            var url = offset == 0 ? path : $"{path}/offset/{offset}";
            var html = await GetStringAsync(url, cancellationToken, request => request.Headers.Add("X-Requested-With", "XMLHttpRequest")).ConfigureAwait(false);
            var page = Parse(html);
            offers.AddRange(page);
            if (page.Count < 12 || !html.Contains("show-next", StringComparison.Ordinal))
                break;
        }
        return offers;
    }

    internal IReadOnlyList<Offer> Parse(string html)
    {
        var offers = new List<Offer>();
        foreach (var tile in Html.Blocks(html, "<a class=\"game-block"))
        {
            var url = Html.Match(tile, @"href=""([^""]+)""");
            var title = Html.Decode(Html.Match(tile, @"tile__title"">([^<]+)<"));
            var priceText = Html.Match(tile, @"tile__price-now"">([\d\s ]+)");
            if (url is null || title.Length == 0 || !PriceParser.TryParseAmount(priceText ?? string.Empty, out var amount) || amount <= 0)
                continue;

            var price = Money.Of(amount, "RUB");
            Money? regular = PriceParser.TryParseAmount(Html.Match(tile, @"tile__price-old"">([\d\s ]+)") ?? string.Empty, out var old) && old > amount
                ? Money.Of(old, "RUB")
                : null;

            // The first <span> in the meta line is the activation service.
            var activation = Html.Match(tile, @"tile__meta[^>]*>.*?<span[^>]*>([^<]+)</span>");
            var image = Html.Match(tile, @"<img[^>]+src=""([^""]+)""");
            offers.Add(CreateOffer(title, Absolute(Host, url), price) with
            {
                RegularPrice = regular,
                ImageUrl = image is null ? null : Absolute(Host, image),
                Platform = OfferClassifier.ParsePlatform(Html.Decode(activation)),
            });
        }
        return offers;
    }
}
