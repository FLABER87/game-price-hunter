using System.Text.Json;
using System.Text.RegularExpressions;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.Official;

/// <summary>Voidu: authorized key store (EUR); search cards carry their data as a JSON attribute.</summary>
public sealed class VoiduProvider(HttpClient http) : StoreProvider(http)
{
    private const string Host = "https://www.voidu.com";

    public override ProviderInfo Info { get; } = new("voidu", "Voidu", StoreKind.Official, "https://www.voidu.com/");

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        var html = await GetStringAsync($"{Host}/en/search?search={Encode(context.Query)}", cancellationToken).ConfigureAwait(false);
        return Parse(html);
    }

    internal IReadOnlyList<Offer> Parse(string html)
    {
        var offers = new List<Offer>();
        foreach (Match match in Html.Matches(html, @"data-product='(\{.*?\})'>"))
        {
            using var doc = JsonDocument.Parse(System.Net.WebUtility.HtmlDecode(match.Groups[1].Value));
            var product = doc.RootElement;
            var title = product.Str("name");
            var url = product.Str("href");
            var listPrice = product.Dec("price_raw");
            var sale = product.Dec("special_raw");
            if (string.IsNullOrWhiteSpace(title) || url is null || product.Str("stock_status_key") == "out_of_stock" || product.Bool("available_country") == false)
                continue;

            var price = sale is > 0 ? sale : listPrice;
            if (price is not > 0)
                continue;

            var currency = PriceParser.DetectCurrency(product.Str("price") ?? string.Empty) ?? "EUR";
            offers.Add(CreateOffer(Html.Decode(title), Absolute(Host, url), Money.Of(price.Value, currency)) with
            {
                RegularPrice = listPrice > price ? Money.Of(listPrice.Value, currency) : null,
                ImageUrl = product.Str("thumb"),
                Platform = OfferClassifier.ParsePlatform(product.Str("platforms")),
            });
        }
        return offers;
    }
}

/// <summary>Yuplay: authorized key store; catalog cards carry price and currency as data attributes.</summary>
public sealed class YuplayProvider(HttpClient http) : StoreProvider(http)
{
    private const string Host = "https://www.yuplay.com";

    public override ProviderInfo Info { get; } = new("yuplay", "Yuplay", StoreKind.Official, "https://www.yuplay.com/");

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        var html = await GetStringAsync($"{Host}/products/?search={Encode(context.Query)}", cancellationToken).ConfigureAwait(false);
        return Parse(html);
    }

    internal IReadOnlyList<Offer> Parse(string html)
    {
        var offers = new List<Offer>();
        foreach (var card in Html.Blocks(html, "<article class=\"catalog-item\""))
        {
            if (card.Contains("aria-label=\"Out of stock\"", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = Html.Matches(card, @"data-name=""([^""]*)""\s+data-currency=""(\w+)""(?:\s+data-sku=""[^""]*"")?\s+data-price=""([\d.]+)""").FirstOrDefault();
            var link = Html.Match(card, @"href=""(/product/[^""]+)""");
            if (data is null || link is null || !PriceParser.TryParseAmount(data.Groups[3].Value, out var amount) || amount <= 0)
                continue;

            var currency = data.Groups[2].Value.ToUpperInvariant();
            // "59.<sup>99&#8364;</sup>"
            var full = Html.Decode(Regex.Replace(Html.Match(card, @"catalog-item-full-price"">(.*?)</span>") ?? string.Empty, "<[^>]+>", ""));
            var image = Html.Match(card, @"<img class=""catalog-item-image"" src=""([^""]+)""");
            offers.Add(CreateOffer(Html.Decode(data.Groups[1].Value), Absolute(Host, link), Money.Of(amount, currency)) with
            {
                RegularPrice = PriceParser.TryParseAmount(full, out var regular) && regular > amount ? Money.Of(regular, currency) : null,
                ImageUrl = image is null ? null : Absolute(Host, image),
            });
        }
        return offers;
    }
}

// Gamebillet was evaluated and dropped: Cloudflare answers .NET's TLS client with a
// "Just a moment..." challenge (HTTP 403), even though plain curl gets the page. Its prices
// still arrive through CheapShark and IsThereAnyDeal.

/// <summary>
/// Gamesplanet: authorized key store with separate UK (GBP), US (USD), DE and FR (EUR) shops;
/// the shop is picked by the user's country.
/// </summary>
public sealed class GamesplanetProvider(HttpClient http) : StoreProvider(http)
{
    private static readonly HashSet<string> FrenchShopCountries = ["FR", "BE", "LU"];

    public override ProviderInfo Info { get; } = new("gamesplanet", "Gamesplanet", StoreKind.Official, "https://www.gamesplanet.com/");

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        var (host, currency) = PickShop(context.Region);
        var html = await GetStringAsync($"{host}/search?query={Encode(context.Query)}", cancellationToken).ConfigureAwait(false);
        return Parse(html, host, currency);
    }

    internal static (string Host, string Currency) PickShop(UserRegion region) => region.CountryCode switch
    {
        "GB" => ("https://uk.gamesplanet.com", "GBP"),
        "US" or "CA" => ("https://us.gamesplanet.com", "USD"),
        _ when FrenchShopCountries.Contains(region.CountryCode) => ("https://fr.gamesplanet.com", "EUR"),
        _ when region.CurrencyCode == "EUR" => ("https://de.gamesplanet.com", "EUR"),
        _ => ("https://uk.gamesplanet.com", "GBP"),
    };

    internal IReadOnlyList<Offer> Parse(string html, string host, string currency)
    {
        var offers = new List<Offer>();
        foreach (var row in Html.Blocks(html, "row no-gutters game_list"))
        {
            var link = Html.Matches(row, @"<h4[^>]*>\s*<a[^>]*href=""(/game/[^""]+)"">([^<]+)").FirstOrDefault();
            // Unreleased games without a price have no price_current.
            if (link is null || !PriceParser.TryParse(Html.Decode(Html.Match(row, @"price_current"">([^<]+)")), currency, out var price) || price.Amount <= 0)
                continue;

            var slug = link.Groups[1].Value;
            Money? regular = PriceParser.TryParse(Html.Decode(Html.Match(row, @"price_base""><strike>([^<]+)")), currency, out var basePrice) && basePrice.Amount > price.Amount
                ? basePrice
                : null;
            // Long names are cut ("... Deluxe E...") in the heading but not in the cover's alt text.
            var image = Html.Matches(row, @"<img[^>]*\balt=""([^""]*)""[^>]*\bsrc=""([^""]+)""").FirstOrDefault();
            var title = Html.Decode(image?.Groups[1].Value);
            if (title.Length == 0)
                title = Html.Decode(link.Groups[2].Value);
            offers.Add(CreateOffer(title, Absolute(host, slug), price) with
            {
                RegularPrice = regular,
                ImageUrl = image?.Groups[2].Value ?? Html.Match(row, @"<img[^>]+src=""([^""]+)"""),
                Type = row.Contains("class=\"class-dlc\"", StringComparison.Ordinal) ? OfferType.Dlc : OfferType.Unknown,
                Platform = PlatformFromSlug(slug),
            });
        }
        return offers;
    }

    /// <summary>"/game/anno-2070-ubisoft-connect--1451-1" → Ubisoft; "...-steam-key--4771-1" → Steam.</summary>
    private static Platform PlatformFromSlug(string slug)
    {
        var name = slug["/game/".Length..];
        var end = name.LastIndexOf("--", StringComparison.Ordinal);
        if (end > 0)
            name = name[..end];
        var parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return OfferClassifier.DetectPlatform(string.Join(' ', parts.TakeLast(3)));
    }
}
