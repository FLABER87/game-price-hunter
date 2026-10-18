using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.KeyShops;

/// <summary>GabeStore.ru: Steam/GOG key shop with RUB, USD, EUR and UAH price lists.</summary>
public sealed class GabeStoreProvider(HttpClient http) : StoreProvider(http)
{
    private const string Host = "https://gabestore.ru";
    private static readonly string[] Currencies = ["RUB", "USD", "EUR", "UAH"];

    private readonly SemaphoreSlim _currencyLock = new(1, 1);
    private string? _sessionCurrency;

    public override ProviderInfo Info { get; } = new("gabestore", "GabeStore", StoreKind.KeyShop, "https://gabestore.ru/") { Note = RegionalNotes.Russia };

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        var currency = Currencies.Contains(context.Region.CurrencyCode) ? context.Region.CurrencyCode : "RUB";
        await EnsureCurrencyAsync(currency, cancellationToken).ConfigureAwait(false);

        var offers = new List<Offer>();
        for (var page = 1; page <= 2; page++)
        {
            var url = $"{Host}/search/next?ProductFilter%5Bsearch%5D={Encode(context.Query)}&ProductFilter%5BsortName%5D=views&ProductFilter%5Bavailable%5D=1&page={page}";
            using var doc = await GetJsonAsync(url, cancellationToken, Xhr).ConfigureAwait(false);
            offers.AddRange(Parse(doc.RootElement.Str("html") ?? string.Empty, currency));
            if (doc.RootElement.Prop("page") is null || offers.Count >= context.MaxResults)
                break;
        }
        return offers;
    }

    internal IReadOnlyList<Offer> Parse(string html, string currency)
    {
        var offers = new List<Offer>();
        foreach (var item in Html.Blocks(html, "<div class=\"shop-item\">"))
        {
            if (item.Contains("btn--empty-item", StringComparison.Ordinal) || item.Contains(">Отсутствует<", StringComparison.Ordinal))
                continue;

            var link = Html.Matches(item, @"class=""shop-item__name""\s+href=""([^""]+)"">([^<]+)<").FirstOrDefault();
            var priceText = Html.Match(item, @"shop-item__price-current"">([^<]+)<");
            if (link is null || !PriceParser.TryParse(Html.Decode(priceText), currency, out var price))
                continue;

            var title = Html.Decode(link.Groups[2].Value);
            var discount = Html.Match(item, @"shop-item__price-discount"">-?(\d+)%");
            offers.Add(CreateOffer(title, Absolute(Host, link.Groups[1].Value), price) with
            {
                RegularPrice = RegularFromDiscount(price, discount),
                ImageUrl = Html.Match(item, @"<img[^>]+src=""([^""]+)"""),
            });
        }
        return offers;
    }

    /// <summary>Regular price from a discount label: "15", "-15%" or "Sale 15%" → price / 0.85.</summary>
    internal static Money? RegularFromDiscount(Money price, string? discountPercent)
    {
        var digits = discountPercent is null ? null : Html.Match(discountPercent, @"(\d+)");
        if (!int.TryParse(digits, out var percent) || percent is <= 0 or >= 100)
            return null;
        return price with { Amount = Math.Round(price.Amount / (1 - percent / 100m), 2) };
    }

    private async Task EnsureCurrencyAsync(string currency, CancellationToken cancellationToken)
    {
        await _currencyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessionCurrency == currency)
                return;
            // Sets signed session cookies; prices in search results follow them.
            await GetStringAsync($"{Host}/api/currency?value={currency}", cancellationToken, Xhr).ConfigureAwait(false);
            _sessionCurrency = currency;
        }
        finally
        {
            _currencyLock.Release();
        }
    }

    private static void Xhr(HttpRequestMessage request) => request.Headers.Add("X-Requested-With", "XMLHttpRequest");
}
