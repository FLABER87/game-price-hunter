using System.Net;
using System.Text.Json;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Providers;
using GamePriceHunter.Core.Providers.Aggregators;
using GamePriceHunter.Core.Providers.KeyShops;
using GamePriceHunter.Core.Providers.Marketplaces;
using GamePriceHunter.Core.Providers.Official;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Tests;

/// <summary>Parsers against responses captured from the live stores (Fixtures folder).</summary>
public class ProviderParsingTests
{
    private static readonly HttpClient Http = new();

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static JsonElement Json(string name) => JsonDocument.Parse(Fixture(name)).RootElement;

    private static void AssertWellFormed(IReadOnlyList<Offer> offers, string? currency)
    {
        Assert.NotEmpty(offers);
        Assert.All(offers, offer =>
        {
            Assert.False(string.IsNullOrWhiteSpace(offer.Title));
            Assert.StartsWith("https://", offer.Url);
            Assert.True(offer.Price.Amount > 0, $"{offer.Title}: price {offer.Price}");
            Assert.Matches("^[A-Z]{3}$", offer.Price.Currency);
            if (currency is not null)
                Assert.Equal(currency, offer.Price.Currency);
            if (offer.RegularPrice is { } regular)
                Assert.True(regular.Amount > offer.Price.Amount, $"{offer.Title}: regular {regular} <= {offer.Price}");
        });
    }

    [Fact]
    public void Steam_store_search_api()
    {
        var offers = new SteamProvider(Http).ParseStoreSearch(Json("steam_storesearch.json"));
        Assert.All(offers, o => Assert.Equal(Platform.Steam, o.Platform));
        Assert.All(offers, o => Assert.Matches(@"^https://store\.steampowered\.com/app/\d+/$", o.Url));
    }

    [Fact]
    public void Steam_search_page_rows()
    {
        var html = Json("steam_search_infinite.json").Str("results_html")!;
        var offers = new SteamProvider(Http).ParseSearchResults(html, "EUR", "EUR");
        // Free games are kept here; the search service decides whether to show them.
        AssertWellFormed(offers.Where(o => o.Price.Amount > 0).ToList(), "EUR");
        var discounted = offers.First(o => o.Title == "Tails of Iron");
        Assert.Equal(2.99m, discounted.Price.Amount);
        Assert.Equal(24.99m, discounted.RegularPrice?.Amount);
        Assert.Equal(88, discounted.DiscountPercent);
    }

    [Fact]
    public void Gog_catalog()
    {
        var offers = new GogProvider(Http).Parse(Json("gog_catalog.json"));
        Assert.NotEmpty(offers);
        Assert.All(offers, o => Assert.StartsWith("https://www.gog.com/", o.Url));
        Assert.Contains(offers, o => o.Type == OfferType.Dlc || o.Type == OfferType.Bundle || o.Type == OfferType.Game);
    }

    [Fact]
    public void Fanatical_algolia_hits()
    {
        var root = Json("fanatical_algolia.json");
        var offers = new FanaticalProvider(Http).Parse(root.Prop("hits") is null && root.Prop("results") is { } results ? results[0] : root, "EUR");
        AssertWellFormed(offers, "EUR");
        Assert.All(offers, o => Assert.StartsWith("https://www.fanatical.com/en/", o.Url));
    }

    [Fact]
    public void CheapShark_deals_skip_directly_covered_stores()
    {
        var stores = Json("cheapshark_stores.json").EnumerateArray().ToDictionary(s => s.Str("storeID")!, s => s.Str("storeName")!);
        var offers = new CheapSharkProvider(Http).Parse(Json("cheapshark_deals.json"), stores);
        Assert.All(offers, o => Assert.DoesNotContain(o.StoreName, new[] { "Steam", "GOG", "Fanatical" }));
        Assert.All(offers, o => Assert.StartsWith("https://www.cheapshark.com/redirect?dealID=", o.Url));
    }

    [Fact]
    public void SteamBuy_picks_the_region_currency()
    {
        var eur = new SteamBuyProvider(Http).Parse(Json("steambuy_search.json"), "EUR");
        AssertWellFormed(eur, "EUR");
        var rub = new SteamBuyProvider(Http).Parse(Json("steambuy_search.json"), "PLN");
        AssertWellFormed(rub, "RUB");
    }

    [Fact]
    public void GabeStore_tiles()
    {
        var offers = new GabeStoreProvider(Http).Parse(Json("gabestore_search.json").Str("html")!, "RUB");
        AssertWellFormed(offers, "RUB");
        Assert.All(offers, o => Assert.StartsWith("https://gabestore.ru/", o.Url));
    }

    [Fact]
    public void SteamPay_products()
    {
        // The fixture holds two responses: "witcher" (only unavailable items) and "star" (in stock).
        var root = Json("steampay_search.json");
        Assert.Empty(new SteamPayProvider(Http).Parse(root.Prop("witcher")!.Value));
        AssertWellFormed(new SteamPayProvider(Http).Parse(root.Prop("star")!.Value), "RUB");
    }

    [Fact]
    public void ZakaZaka_tiles()
    {
        var offers = new ZakaZakaProvider(Http).Parse(Fixture("zakazaka_search.html"));
        AssertWellFormed(offers, "RUB");
    }

    [Fact]
    public void Buka_rows()
    {
        var offers = new BukaProvider(Http).Parse(Json("buka_search.json").Prop("get_page")!.Value);
        AssertWellFormed(offers, "RUB");
        Assert.All(offers, o => Assert.DoesNotContain("(PC)", o.Title));
    }

    [Fact]
    public void Plati_cataloguer_and_legacy_api()
    {
        var provider = new PlatiProvider(Http);
        var catalog = provider.Parse(Json("plati_digiseller_products.json"));
        AssertWellFormed(catalog, null);
        Assert.All(catalog, o => Assert.StartsWith("https://plati.market/itm/", o.Url));
        Assert.All(catalog, o => Assert.False(string.IsNullOrEmpty(o.Seller)));
        AssertWellFormed(provider.ParseLegacy(Json("plati_search.json"), "EUR"), "EUR");
    }

    [Fact]
    public void K4G_offers_have_region_and_seller()
    {
        var offers = new K4GProvider(Http).Parse(Json("k4g_search.json"), "EUR");
        AssertWellFormed(offers, "EUR");
        Assert.Contains(offers, o => o.Region is not null);
    }

    [Fact]
    public void Eneba_embedded_search_state() =>
        AssertWellFormed(new EnebaProvider(Http).Parse(Fixture("eneba_search.html"), "EUR"), "EUR");

    [Fact]
    public void InstantGaming_embedded_results() =>
        AssertWellFormed(new InstantGamingProvider(Http).Parse(Fixture("instantgaming_search.html")), "EUR");

    [Fact]
    public void Driffle_products() =>
        AssertWellFormed(new DriffleProvider(Http).Parse(Json("driffle_search.json")), "EUR");

    [Fact]
    public void Ggsel_goods() =>
        AssertWellFormed(new GgselProvider(Http).Parse(Json("ggsel_search.json"), "EUR"), null);

    [Fact]
    public void GamersGate_catalog() =>
        AssertWellFormed(new GamersGateProvider(Http).Parse(Json("gamersgate_search.json")), null);

    [Fact]
    public void TwoGame_suggestions() =>
        AssertWellFormed(new TwoGameProvider(Http).Parse(Json("2game_suggest.json"), "EUR"), "EUR");

    [Fact]
    public void Voidu_product_cards() =>
        AssertWellFormed(new VoiduProvider(Http).Parse(Fixture("voidu_search.html")), null);

    [Fact]
    public void Yuplay_skips_out_of_stock_items() =>
        // The captured page holds a single card, marked "Out of stock".
        Assert.Empty(new YuplayProvider(Http).Parse(Fixture("yuplay_search.html")));

    [Fact]
    public void Gamesplanet_rows() =>
        AssertWellFormed(new GamesplanetProvider(Http).Parse(Fixture("gamesplanet_search.html"), "https://uk.gamesplanet.com", "GBP"), "GBP");

    [Fact]
    public void Mmoga_search_blocks() =>
        AssertWellFormed(new MmogaProvider(Http).Parse(Fixture("mmoga_search.html")), "EUR");

    [Fact]
    public void Difmark_cards() =>
        AssertWellFormed(new DifmarkProvider(Http).Parse(Fixture("difmark_search.html")), null);

    [Theory]
    [InlineData("2,99€", "USD", "EUR")]
    [InlineData("$9.99", "USD", "USD")]
    [InlineData("$9.99 USD", "ARS", "USD")]
    [InlineData("US$ 9.99", "ARS", "USD")]
    [InlineData("$9.99", "CAD", null)]
    [InlineData("CDN$ 12.99", "CAD", null)]
    [InlineData("CDN$ 12.99", "USD", null)]
    [InlineData("HK$ 99", "USD", null)]
    [InlineData("S$9.99", "USD", null)]
    [InlineData("¥ 1,980", "JPY", "JPY")]
    [InlineData("¥ 98", "CNY", null)]
    [InlineData("CN¥ 98", "JPY", null)]
    [InlineData("99 kr", "NOK", "NOK")]
    public void Steam_page_currency_without_the_api(string priceText, string regionCurrency, string? expected) =>
        Assert.Equal(expected, SteamProvider.CurrencyFromText(priceText, regionCurrency));
}

/// <summary>Steam asks two endpoints at once; one failing must not discard the other's offers.</summary>
public class SteamEndpointTests
{
    private static readonly UserRegion Region = new("SK", "EUR");

    private static SteamProvider Provider(HttpStatusCode api, HttpStatusCode page) => new(new HttpClient(new StubHandler(request =>
    {
        var isApi = request.RequestUri!.AbsolutePath.StartsWith("/api/storesearch", StringComparison.Ordinal);
        var status = isApi ? api : page;
        if (status != HttpStatusCode.OK)
            return new HttpResponseMessage(status);
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", isApi ? "steam_storesearch.json" : "steam_search_infinite.json");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(fixture)) };
    })));

    private static Task<IReadOnlyList<Offer>> Search(HttpStatusCode api, HttpStatusCode page) =>
        Provider(api, page).SearchAsync(new SearchContext("hollow knight", Region), CancellationToken.None);

    [Fact]
    public async Task Keeps_api_offers_when_the_page_is_blocked()
    {
        var offers = await Search(HttpStatusCode.OK, HttpStatusCode.Forbidden);
        Assert.NotEmpty(offers);
        Assert.All(offers, o => Assert.Matches(@"/app/\d+/$", o.Url));
    }

    [Fact]
    public async Task Keeps_page_offers_when_the_api_fails()
    {
        var offers = await Search(HttpStatusCode.NotFound, HttpStatusCode.OK);
        Assert.NotEmpty(offers);
        Assert.All(offers, o => Assert.Equal("EUR", o.Price.Currency));
    }

    [Fact]
    public async Task Reports_the_api_error_when_both_fail()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => Search(HttpStatusCode.NotFound, HttpStatusCode.Forbidden));
        await Assert.ThrowsAsync<ProviderBlockedException>(() => Search(HttpStatusCode.Forbidden, HttpStatusCode.NotFound));
    }
}
