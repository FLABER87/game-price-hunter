using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.Marketplaces;

/// <summary>MMOGA: German key shop (EUR); one server-rendered search page sorted by price.</summary>
public sealed class MmogaProvider(HttpClient http) : StoreProvider(http)
{
    private const string Host = "https://www.mmoga.com";

    public override ProviderInfo Info { get; } = new("mmoga", "MMOGA", StoreKind.KeyShop, "https://www.mmoga.com/");

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        var html = await GetStringAsync($"{Host}/advanced_search.php?keywords={Encode(context.Query)}&sort_filter=price/asc", cancellationToken).ConfigureAwait(false);
        return Parse(html);
    }

    internal IReadOnlyList<Offer> Parse(string html)
    {
        var offers = new List<Offer>();
        foreach (var block in Html.Blocks(html, "<div class=\"searchCont\">"))
        {
            var link = Html.Matches(block, @"itemCont"">\s*<a href=""(/[^""]+)"" title=""([^""]+)""").FirstOrDefault();
            // Sold-out products have an empty price cell.
            var price = Html.Matches(block, @"itemPrice"">\s*(?:<del>(.*?)</del>\s*<span[^>]*>)?\s*&euro;&nbsp;([\d.,]+)").FirstOrDefault();
            if (link is null || price is null || !PriceParser.TryParseAmount(price.Groups[2].Value, out var amount) || amount <= 0)
                continue;

            var path = link.Groups[1].Value;
            var oldText = Html.Decode(price.Groups[1].Value);
            var image = Html.Match(block, @"data-background=""([^""]+)""");
            offers.Add(CreateOffer(Html.Decode(link.Groups[2].Value), Absolute(Host, path), Money.Of(amount, "EUR")) with
            {
                RegularPrice = PriceParser.TryParseAmount(oldText, out var regular) && regular > amount ? Money.Of(regular, "EUR") : null,
                ImageUrl = image is null ? null : Absolute(Host, image),
                // The first path segment is the shop section: "GOG-Keys", "Steam-Games", "Xbox-Live"...
                Platform = OfferClassifier.DetectPlatform(path.Split('/', StringSplitOptions.RemoveEmptyEntries)[0].Replace('-', ' ')),
                // Game accounts from third-party sellers live under ".../Cyberpunk-2077-Unique-Accounts,...".
                Type = path.Contains("-Accounts,", StringComparison.OrdinalIgnoreCase) ? OfferType.Other : OfferType.Unknown,
            });
        }
        return offers;
    }
}

/// <summary>Difmark marketplace (EUR); server-rendered catalog cards with the lowest ("from") price.</summary>
public sealed class DifmarkProvider(HttpClient http) : StoreProvider(http)
{
    private const string Host = "https://difmark.com";

    public override ProviderInfo Info { get; } = new("difmark", "Difmark", StoreKind.Marketplace, "https://difmark.com/")
    {
        Note = ProviderNote.Marketplace,
    };

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        var html = await GetStringAsync($"{Host}/en/products?name={Encode(context.Query)}&page=1", cancellationToken).ConfigureAwait(false);
        return Parse(html);
    }

    internal IReadOnlyList<Offer> Parse(string html)
    {
        var offers = new List<Offer>();
        foreach (var card in Html.Blocks(html, "one-item product-item"))
        {
            var url = Html.Match(card, @"href=""(/en/buy-[^""]+)""");
            var title = Html.Decode(Html.Match(card, @"data-text=""([^""]+)"""));
            var priceText = Html.Decode(Html.Match(card, @"catalog-card__price"">([^<]+)"));
            if (url is null || title.Length == 0 || !PriceParser.TryParse(priceText, "EUR", out var price) || price.Amount <= 0)
                continue;

            var gridType = Html.Match(card, @"^[^""]*grid-type\s+(\w*)") ?? string.Empty;
            var image = Html.Match(card, @"<img\s+src=""([^""]+)""");
            offers.Add(CreateOffer(title, Absolute(Host, url), price) with
            {
                ImageUrl = image is null ? null : Absolute(Host, image),
                // Console/PC accounts are listed next to keys ("grid-type account", "/en/buy-console-account-...").
                Type = gridType.Equals("account", StringComparison.OrdinalIgnoreCase) || url.Contains("-account-", StringComparison.OrdinalIgnoreCase)
                    ? OfferType.Other
                    : OfferType.Unknown,
            });
        }
        return offers;
    }
}
