using System.Text.Json;
using System.Text.RegularExpressions;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.KeyShops;

/// <summary>IgroMagaz.ru: key shop (RUB); server-rendered search page.</summary>
public sealed class IgroMagazProvider(HttpClient http) : StoreProvider(http)
{
    private const string Host = "https://www.igromagaz.ru";

    public override ProviderInfo Info { get; } = new("igromagaz", "IgroMagaz", StoreKind.KeyShop, "https://www.igromagaz.ru/") { Note = RegionalNotes.Russia };

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        var html = await GetStringAsync($"{Host}/search/?q={Encode(context.Query)}", cancellationToken).ConfigureAwait(false);
        return Parse(html);
    }

    internal IReadOnlyList<Offer> Parse(string html)
    {
        var offers = new List<Offer>();
        // Only real hits: "container-recommended-item" cards are suggestions shown when nothing matches.
        foreach (var card in Html.Blocks(html, "<div class=\"product-card container-good-item-js_"))
        {
            if (card.Contains("product-availability--not-available", StringComparison.Ordinal))
                continue;

            var link = Html.Matches(card, @"href=""(/[^""]+\.php)""[^>]*class=""product-img""><img src=""([^""]+)"" alt=""([^""]*)""").FirstOrDefault();
            var priceText = Html.Match(card, @"product-price__standart[^""]*"">\s*([\d\s]+)\s*руб");
            if (link is null || !PriceParser.TryParseAmount(priceText ?? string.Empty, out var amount) || amount <= 0)
                continue;

            var old = Html.Match(card, @"product-price__fail"">\s*([\d\s]+)");
            offers.Add(CreateOffer(Html.Decode(link.Groups[3].Value), Absolute(Host, link.Groups[1].Value), Money.Of(amount, "RUB")) with
            {
                RegularPrice = PriceParser.TryParseAmount(old ?? string.Empty, out var regular) && regular > amount ? Money.Of(regular, "RUB") : null,
                ImageUrl = Absolute(Host, link.Groups[2].Value),
            });
        }
        return offers;
    }
}

/// <summary>Gamazavr.ru: key shop (RUB); one search page with all hits.</summary>
public sealed class GamazavrProvider(HttpClient http) : StoreProvider(http)
{
    private const string Host = "https://gamazavr.ru";

    public override ProviderInfo Info { get; } = new("gamazavr", "Gamazavr", StoreKind.KeyShop, "https://gamazavr.ru/") { Note = RegionalNotes.Russia };

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        var html = await GetStringAsync($"{Host}/search/?query={Encode(context.Query)}", cancellationToken).ConfigureAwait(false);
        return Parse(html);
    }

    internal IReadOnlyList<Offer> Parse(string html)
    {
        var offers = new List<Offer>();
        foreach (Match item in Html.Matches(html, @"<div class=""item"">\s*<div class=""price"">(.*?)</div>\s*<a href=""(/product/[^""]+)"" title=""([^""]*)"" class=""img""><img src=""([^""]+)"""))
        {
            var priceBlock = item.Groups[1].Value;
            if (!PriceParser.TryParseAmount(Html.Match(priceBlock, @"<b>([\d\s]+)</b>") ?? string.Empty, out var amount) || amount <= 0)
                continue;

            var old = Html.Match(priceBlock, @"<s>([\d\s]+)");
            offers.Add(CreateOffer(Html.Decode(item.Groups[3].Value), Absolute(Host, item.Groups[2].Value), Money.Of(amount, "RUB")) with
            {
                RegularPrice = PriceParser.TryParseAmount(old ?? string.Empty, out var regular) && regular > amount ? Money.Of(regular, "RUB") : null,
                ImageUrl = Absolute(Host, item.Groups[4].Value),
            });
        }
        return offers;
    }
}

/// <summary>Steamkey.com: key shop (RUB); autocomplete endpoint returns up to 10 items.</summary>
public sealed class SteamkeyProvider(HttpClient http) : StoreProvider(http)
{
    private const string Host = "https://steamkey.com";

    public override ProviderInfo Info { get; } = new("steamkey", "Steamkey", StoreKind.KeyShop, "https://steamkey.com/") { Note = RegionalNotes.Russia };

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        if (context.Query.Length < 3)
            return [];

        var body = await PostFormAsync($"{Host}/ajax/autocomplete", [new("q", context.Query)], cancellationToken,
            request => request.Headers.Add("X-Requested-With", "XMLHttpRequest")).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        return Parse(doc.RootElement.Str("html") ?? string.Empty);
    }

    internal IReadOnlyList<Offer> Parse(string html)
    {
        var offers = new List<Offer>();
        foreach (var item in Html.Blocks(html, "search-result__item"))
        {
            var url = Html.Match(item, @"^search-result__item""\s+href=""([^""]+)""") ?? Html.Match(item, @"href=""(/[^""]+)""");
            var title = Html.Decode(Html.Match(item, @"search-result__title"">([^<]+)"));
            var category = Html.Decode(Html.Match(item, @"search-result__cat"">([^<]+)"));
            var priceText = Html.Match(item, @"search-result__cost"">([\d\s ]+)");
            if (url is null || title.Length == 0 || !PriceParser.TryParseAmount(priceText ?? string.Empty, out var amount) || amount <= 0)
                continue;

            var image = Html.Match(item, @"<img src=""([^""]+)""");
            offers.Add(CreateOffer(title, Absolute(Host, url), Money.Of(amount, "RUB")) with
            {
                ImageUrl = image is null ? null : Absolute(Host, image),
                Platform = OfferClassifier.ParsePlatform(category),
                // "Steam Аккаунты" are shared accounts, not keys.
                Type = category.Contains("Аккаунт", StringComparison.OrdinalIgnoreCase) ? OfferType.Other : OfferType.Unknown,
            });
        }
        return offers;
    }
}

/// <summary>
/// IgroShop.com: key shop (USD). Its site search runs on Searchanise; the public widget key is
/// read from the home page and cached. The bare domain redirects https → http://www, which
/// HttpClient refuses to follow, so the www host is used directly.
/// </summary>
public sealed class IgroShopProvider(HttpClient http) : StoreProvider(http)
{
    private const string Host = "https://www.igroshop.com";
    private readonly SemaphoreSlim _keyLock = new(1, 1);
    private string? _apiKey;

    public override ProviderInfo Info { get; } = new("igroshop", "IgroShop", StoreKind.KeyShop, "https://www.igroshop.com/") { Note = RegionalNotes.Russia };

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        var key = await GetApiKeyAsync(cancellationToken).ConfigureAwait(false);
        var url = "https://searchserverapi1.com/getresults?api_key=" + Encode(key) + "&q=" + Encode(context.Query) +
                  "&output=json&items=true&facets=false&maxResults=30&startIndex=0" +
                  "&restrictBy%5Bstatus%5D=A&restrictBy%5Bempty_categories%5D=N&restrictBy%5Busergroup_ids%5D=0%7C1";
        using var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        return Parse(doc.RootElement);
    }

    internal IReadOnlyList<Offer> Parse(JsonElement root)
    {
        var offers = new List<Offer>();
        foreach (var item in root.Arr("items"))
        {
            var title = item.Str("title");
            var link = item.Str("link");
            var price = item.Dec("price");
            if (title is null || link is null || price is not > 0 || item.Long("quantity") is 0)
                continue;

            var list = item.Dec("list_price");
            offers.Add(CreateOffer(Html.Decode(title), link.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase), Money.Of(price.Value, "USD")) with
            {
                RegularPrice = list > price ? Money.Of(list.Value, "USD") : null,
                ImageUrl = item.Str("image_link"),
            });
        }
        return offers;
    }

    private async Task<string> GetApiKeyAsync(CancellationToken cancellationToken)
    {
        await _keyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_apiKey is not null)
                return _apiKey;
            var home = await GetStringAsync(Host + "/", cancellationToken).ConfigureAwait(false);
            _apiKey = Html.Match(home, @"Searchanise\.api_key\s*=\s*'([^']+)'")
                ?? throw new InvalidDataException("IgroShop search configuration not found.");
            return _apiKey;
        }
        finally
        {
            _keyLock.Release();
        }
    }
}
