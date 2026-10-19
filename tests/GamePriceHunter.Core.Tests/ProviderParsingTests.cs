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
    public void Steam_row_with_a_bundle_discount_keeps_the_items_own_price()
    {
        // The row shows "Includes 2 games": $35.26 instead of $76.48 for the bundle, while the game alone is $37.49.
        var html = Json("steam_bundle_row_search_infinite.json").Str("results_html")!;
        var offers = new SteamProvider(Http).ParseSearchResults(html, "USD", "USD");

        var game = offers.First(o => o.Title == "The Sinking City 2");
        Assert.Equal(37.49m, game.Price.Amount);
        Assert.Null(game.RegularPrice);
        Assert.Equal(10.39m, offers.First(o => o.Title == "The Sinking City 2 – The Holloway Manor").Price.Amount);
        Assert.Equal(12.99m, offers.First(o => o.Title == "The Sinking City 2 – The Holloway Manor").RegularPrice?.Amount);
    }

    [Fact]
    public void Steam_rows_without_a_price_are_not_free()
    {
        // Both kinds say data-price-final="0"; only the demo shows "Free", the others show no price at all.
        var html = Json("steam_bundle_row_search_infinite.json").Str("results_html")!;
        var offers = new SteamProvider(Http).ParseSearchResults(html, "USD", "USD");

        Assert.Equal(0m, offers.Single(o => o.Title == "The Sinking City 2 Demo").Price.Amount);
        Assert.DoesNotContain(offers, o => o.Title is "Crazy City Golf" or "Sandara: City of a Thousand Eclipses" or "Afterglow Fortress");
        Assert.DoesNotContain(new SteamProvider(Http).ParseSearchResults(Json("steam_search_infinite.json").Str("results_html")!, "EUR", "EUR"),
            o => o.Title == "Rize of the Summonds");
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
        Assert.All(catalog, o => Assert.True(o.SellerRating is { Value: > 0, OutOf: null }, $"{o.Title}: rating {o.SellerRating}"));
    }

    [Fact]
    public void Plati_seller_rating_and_sales_as_plati_shows_them()
    {
        var root = JsonDocument.Parse("""
            {"content": {"items": [
              {"product_id": 1, "name": "A", "price": 9.5, "currency": "USD", "seller_name": "LMAO_94", "seller_rating": 4343.6871, "total_sales": 6544},
              {"product_id": 2, "name": "B", "price": 9.5, "currency": "USD", "seller_name": "New", "seller_rating": 0, "total_sales": 9},
              {"product_id": 3, "name": "C", "price": 9.5, "currency": "USD", "seller_name": "Capped", "seller_rating": 32.29, "total_sales": 0, "hide_total_sales": 1000}
            ]}}
            """).RootElement;

        var offers = new PlatiProvider(Http).Parse(root);

        Assert.Equal(new SellerRating(4343.6871m, null), offers[0].SellerRating);
        Assert.Equal(new SalesCount(6544, AtLeast: false), offers[0].Sales);
        Assert.Null(offers[1].SellerRating);
        Assert.Null(offers[1].Sales);
        Assert.Null(offers[2].Sales);
    }

    [Theory]
    [InlineData(6544L, null, 6544L, false)]
    [InlineData(127L, 50L, 50L, true)]     // Plati: "Sold 50+"
    [InlineData(183L, 1000L, 183L, false)] // below the cap Plati shows the real count
    [InlineData(139L, -1L, null, false)]   // hidden
    [InlineData(9L, null, null, false)]    // Plati: "Sold fewer than 10"
    [InlineData(0L, 1000L, null, false)]
    public void Plati_sales_count(long sold, long? cap, long? count, bool atLeast)
    {
        var sales = PlatiProvider.ShownSales(sold, cap);
        Assert.Equal(count is null ? null : new SalesCount(count.Value, atLeast), sales);
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

    [Theory]
    [InlineData("JPY", 360, 3600, 360, 3600)]
    [InlineData("KRW", 3300, 33000, 3300, 33000)]
    [InlineData("EUR", 195, 1999, 1.95, 19.99)]
    [InlineData("HUF", 70800, 700000, 708, 7000)]
    public void Eneba_prices_are_in_the_currencys_own_minor_units(string currency, long minor, long before, double price, double regular)
    {
        var html = $$"""
            <script id="__ALGOLIA_STATE__" type="application/json">{"responses": {"q": {"results": [{"hits": [
              {"slug": "the-witcher-3", "translations": {"en_US": {"name": "The Witcher 3"} }, "productType": "game",
               "lowestPrice": {"{{currency}}": {{minor}} }, "lowestPriceBeforeDiscount": {"{{currency}}": {{before}} } }
            ]}]} } }</script>
            """;

        var offer = Assert.Single(new EnebaProvider(Http).Parse(html, currency));

        Assert.Equal(Money.Of((decimal)price, currency), offer.Price);
        Assert.Equal(Money.Of((decimal)regular, currency), offer.RegularPrice);
    }

    [Fact]
    public void Eneba_page_without_search_state_is_named_in_the_error()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            new EnebaProvider(Http).Parse("<html><head><title>Games &amp; More</title></head><body></body></html>", "EUR"));
        Assert.Contains("\"Games & More\"", error.Message);
    }

    [Fact]
    public async Task Eneba_retries_once_when_the_page_has_no_search_state()
    {
        var pages = new Queue<string>(["<html><head><title>Eneba</title></head><body></body></html>", Fixture("eneba_search.html")]);
        var handler = new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(pages.Dequeue()) });

        var offers = await new EnebaProvider(new HttpClient(handler)).SearchAsync(new SearchContext("witcher", new UserRegion("SK", "EUR")), CancellationToken.None);

        AssertWellFormed(offers, "EUR");
        Assert.Empty(pages);
    }

    [Fact]
    public void InstantGaming_embedded_results() =>
        AssertWellFormed(new InstantGamingProvider(Http).Parse(Fixture("instantgaming_search.html")), "EUR");

    [Fact]
    public void InstantGaming_reads_the_euro_fields_when_the_page_shows_another_currency()
    {
        const string html = """
            <script>window.searchResults = {"hits": [{"prod_id": "1", "seo_name": "the-witcher-3", "fullname": "The Witcher 3",
              "has_stock": true, "price": "25.49", "price_eur": "21.99", "retail": "57.99", "retail_currency": "USD",
              "retail_prices": {"EUR": "49.99"} }]};</script>
            """;

        var offer = Assert.Single(new InstantGamingProvider(Http).Parse(html));

        Assert.Equal(Money.Of(21.99m, "EUR"), offer.Price);
        Assert.Equal(Money.Of(49.99m, "EUR"), offer.RegularPrice);
    }

    [Fact]
    public void Driffle_products() =>
        AssertWellFormed(new DriffleProvider(Http).Parse(Json("driffle_search.json")), "EUR");

    [Fact]
    public void Ggsel_goods() =>
        AssertWellFormed(new GgselProvider(Http).Parse(Json("ggsel_search.json"), "EUR"), null);

    [Fact]
    public void Ggsel_seller_stars_and_item_sales()
    {
        var root = JsonDocument.Parse("""
            {"data": {"items": [
              {"name": "A", "url": "a-1", "price_wmz": 9.68, "seller_name": "SteamGifts official", "rating": 4.9, "cnt_sell": 295},
              {"name": "B", "url": "b-2", "price_wmz": 1.79, "seller_name": "Alexander993", "rating": 0, "cnt_sell": 0}
            ]}}
            """).RootElement;

        var offers = new GgselProvider(Http).Parse(root, "USD");

        Assert.Equal(new SellerRating(4.9m, 5), offers[0].SellerRating);
        Assert.Equal(new SalesCount(295, AtLeast: false), offers[0].Sales);
        Assert.Null(offers[1].SellerRating);
        Assert.Null(offers[1].Sales);
    }

    [Fact]
    public void GamersGate_catalog() =>
        AssertWellFormed(new GamersGateProvider(Http).Parse(Json("gamersgate_search.json")), null);

    [Fact]
    public void TwoGame_suggestions() =>
        AssertWellFormed(new TwoGameProvider(Http).Parse(Json("2game_suggest.json"), "EUR"), "EUR");

    [Fact]
    public async Task Gog_asks_in_dollars_when_the_local_currency_comes_without_prices()
    {
        var currencies = new List<string>();
        var handler = new StubHandler(request =>
        {
            var currency = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["currencyCode"]!;
            currencies.Add(currency);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(currency == "JPY"
                    ? """{"products":[{"title":"The Witcher 3: Wild Hunt","price":null}]}"""
                    : Fixture("gog_catalog.json")),
            };
        });

        var offers = await new GogProvider(new HttpClient(handler)).SearchAsync(new SearchContext("witcher", new UserRegion("JP", "JPY")), CancellationToken.None);

        Assert.NotEmpty(offers);
        Assert.Equal(["JPY", "USD"], currencies);
    }

    [Fact]
    public async Task TwoGame_falls_back_to_the_root_store_when_the_local_one_cannot_search()
    {
        var requests = new List<string>();
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            if (path.StartsWith("/ja-jp/", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.ExpectationFailed)
                {
                    Content = new StringContent("""{"status":417,"message":"Expectation Failed","description":"Unsupported buyer locale"}"""),
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(path.StartsWith("/products/", StringComparison.Ordinal)
                    ? """{"product":{"variants":[{"price_currency":"USD"}]}}"""
                    : Fixture("2game_suggest.json")),
            };
        });

        var offers = await new TwoGameProvider(new HttpClient(handler)).SearchAsync(new SearchContext("witcher", new UserRegion("JP", "JPY")), CancellationToken.None);

        AssertWellFormed(offers, "USD");
        Assert.Equal(["/ja-jp/search/suggest.json", "/search/suggest.json"], requests.Take(2));
    }

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
    public async Task A_page_row_without_its_own_original_price_takes_the_apis()
    {
        var provider = new SteamProvider(new HttpClient(new StubHandler(request =>
        {
            var isApi = request.RequestUri!.AbsolutePath.StartsWith("/api/storesearch", StringComparison.Ordinal);
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", isApi ? "steam_bundle_row_storesearch.json" : "steam_bundle_row_search_infinite.json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(fixture)) };
        })));

        var offers = await provider.SearchAsync(new SearchContext("sinking city 2", new UserRegion("US", "USD")), CancellationToken.None);

        var game = Assert.Single(offers, o => o.Title == "The Sinking City 2");
        Assert.Equal(37.49m, game.Price.Amount);
        Assert.Equal(49.99m, game.RegularPrice?.Amount);
        Assert.Equal(25, game.DiscountPercent);
    }

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
