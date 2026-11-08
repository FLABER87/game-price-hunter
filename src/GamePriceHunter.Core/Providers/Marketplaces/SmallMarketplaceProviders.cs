using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.Marketplaces;

/// <summary>
/// MMOGA: German key shop; one server-rendered search page. www.mmoga.com answers non-browser
/// clients with a Cloudflare challenge, but MMOGA's national shops don't: UK users get
/// mmoga.co.uk (GBP), everyone else mmoga.de (EUR).
/// </summary>
public sealed class MmogaProvider(HttpClient http) : StoreProvider(http)
{
    public override ProviderInfo Info { get; } = new("mmoga", "MMOGA", StoreKind.KeyShop, "https://www.mmoga.com/");

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        var (host, currency) = PickShop(context.Region);
        // The shop's own order, not price: the German shop matches loosely ("witcher" also finds
        // "witch"), and sorted by price those cheap misses fill the first page.
        var html = await GetStringAsync($"{host}/advanced_search.php?keywords={Encode(context.Query)}", cancellationToken).ConfigureAwait(false);
        return Parse(html, host, currency);
    }

    internal static (string Host, string Currency) PickShop(UserRegion region) => region.CountryCode == "GB"
        ? ("https://www.mmoga.co.uk", "GBP")
        : ("https://www.mmoga.de", "EUR");

    internal IReadOnlyList<Offer> Parse(string html, string host, string currency)
    {
        var offers = new List<Offer>();
        foreach (var block in Html.Blocks(html, "<div class=\"searchCont\">"))
        {
            var link = Html.Matches(block, @"itemCont"">\s*<a href=""(/[^""]+)"" title=""([^""]+)""").FirstOrDefault();
            // "&pound;&nbsp;22.26" on mmoga.co.uk, "26,99&nbsp;&euro;" on mmoga.de; a sale puts the old
            // price in <del> first. Sold-out products have an empty price cell.
            var cell = Html.Matches(block, @"itemPrice"">\s*(?:<del>(.*?)</del>\s*<span[^>]*>)?([^<]*\d[^<]*)").FirstOrDefault();
            if (link is null || cell is null || !PriceParser.TryParse(Html.Decode(cell.Groups[2].Value), currency, out var price) || price.Amount <= 0)
                continue;

            var path = link.Groups[1].Value;
            var image = Html.Match(block, @"data-background=""([^""]+)""");
            offers.Add(CreateOffer(Html.Decode(link.Groups[2].Value), Absolute(host, path), price) with
            {
                RegularPrice = PriceParser.TryParseAmount(Html.Decode(cell.Groups[1].Value), out var regular) && regular > price.Amount
                    ? Money.Of(regular, price.Currency)
                    : null,
                ImageUrl = image is null ? null : Absolute(host, image),
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
