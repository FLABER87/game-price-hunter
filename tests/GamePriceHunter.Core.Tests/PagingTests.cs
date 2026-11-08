using System.Net;
using System.Text.Json;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Providers;
using GamePriceHunter.Core.Providers.KeyShops;
using GamePriceHunter.Core.Providers.Marketplaces;
using GamePriceHunter.Core.Providers.Official;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Tests;

/// <summary>StoreProvider.ReadPagesAsync: when a search reads another result page and when it stops.</summary>
public class ResultPagingTests
{
    private static readonly SearchContext Context = new("game", new UserRegion("SK", "EUR"));

    [Theory]
    [InlineData(60, 20, 3)]
    [InlineData(60, 48, 2)]
    [InlineData(20, 24, 1)]
    // A big result limit still reads at most StoreProvider.MaxPages pages.
    [InlineData(200, 20, 5)]
    public async Task Reads_pages_until_the_result_limit(int maxResults, int pageSize, int expectedPages)
    {
        var provider = new PagedProvider(pageSize, page => Page(page, pageSize));

        var offers = await provider.SearchAsync(Context with { MaxResults = maxResults }, CancellationToken.None);

        Assert.Equal(Enumerable.Range(1, expectedPages), provider.Requested);
        Assert.Equal(expectedPages * pageSize, offers.Count);
    }

    [Fact]
    public async Task A_provider_can_read_fewer_pages_than_the_limit_allows()
    {
        var provider = new PagedProvider(20, page => Page(page, 20), maxPages: 3);

        await provider.SearchAsync(Context with { MaxResults = 200 }, CancellationToken.None);

        Assert.Equal([1, 2, 3], provider.Requested);
    }

    [Fact]
    public async Task Stops_after_the_stores_last_page()
    {
        var provider = new PagedProvider(20, page => Page(page, page == 2 ? 7 : 20, hasMore: page < 2));

        var offers = await provider.SearchAsync(Context, CancellationToken.None);

        Assert.Equal([1, 2], provider.Requested);
        Assert.Equal(27, offers.Count);
    }

    [Fact]
    public async Task Stops_at_a_page_that_brings_nothing_new()
    {
        // Past its last page a store serves the first page again.
        var provider = new PagedProvider(20, page => Page(page == 3 ? 1 : page, 20));

        var offers = await provider.SearchAsync(Context, CancellationToken.None);

        Assert.Equal([1, 2, 3], provider.Requested);
        Assert.Equal(40, offers.Count);
        Assert.Equal(offers.Count, offers.Select(o => o.Url).Distinct().Count());
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("http")]
    [InlineData("markup")]
    public async Task Keeps_what_the_first_pages_found_when_a_later_page_fails(string failure)
    {
        Exception error = failure switch
        {
            "blocked" => new ProviderBlockedException("Too many requests (HTTP 429).", 429),
            "http" => new HttpRequestException("HTTP 500"),
            _ => new InvalidDataException("No results in the page."),
        };
        var provider = new PagedProvider(20, page => page == 3 ? throw error : Page(page, 20));

        var offers = await provider.SearchAsync(Context, CancellationToken.None);

        Assert.Equal([1, 2, 3], provider.Requested);
        Assert.Equal(40, offers.Count);
    }

    [Fact]
    public async Task The_first_pages_error_is_the_searchs_error()
    {
        var provider = new PagedProvider(20, page => throw new ProviderBlockedException("Blocked."));

        await Assert.ThrowsAsync<ProviderBlockedException>(() => provider.SearchAsync(Context, CancellationToken.None));
    }

    [Fact]
    public async Task A_later_page_gets_half_of_the_timeout_and_its_offers_are_dropped_when_it_runs_late()
    {
        var provider = new PagedProvider(20, async (page, token) =>
        {
            if (page == 2)
                await Task.Delay(Timeout.Infinite, token);
            return Page(page, 20);
        });

        var started = DateTime.UtcNow;
        var offers = await provider.SearchAsync(Context with { Timeout = TimeSpan.FromMilliseconds(400) }, CancellationToken.None);

        Assert.Equal([1, 2], provider.Requested);
        Assert.Equal(20, offers.Count);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task No_later_page_starts_after_half_of_the_timeout()
    {
        var provider = new PagedProvider(20, async (page, token) =>
        {
            await Task.Delay(300, token);
            return Page(page, 20);
        });

        var offers = await provider.SearchAsync(Context with { Timeout = TimeSpan.FromMilliseconds(400) }, CancellationToken.None);

        Assert.Equal([1], provider.Requested);
        Assert.Equal(20, offers.Count);
    }

    [Fact]
    public async Task Cancelling_the_search_cancels_it_on_a_later_page_too()
    {
        using var cancel = new CancellationTokenSource();
        var provider = new PagedProvider(20, async (page, token) =>
        {
            if (page == 2)
            {
                cancel.Cancel();
                token.ThrowIfCancellationRequested();
            }
            await Task.Yield();
            return Page(page, 20);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SearchAsync(Context, cancel.Token));
    }

    private static StoreProvider.ResultPage Page(int page, int count, bool hasMore = true) =>
        new([.. Enumerable.Range(1, count).Select(i => Offer($"p{page}-{i}"))], hasMore);

    private static Offer Offer(string id) => new()
    {
        ProviderId = "paged",
        StoreName = "Paged",
        StoreKind = StoreKind.KeyShop,
        Title = $"Game {id}",
        Url = $"https://paged.test/{id}",
        Price = Money.Of(1, "EUR"),
    };

    private sealed class PagedProvider(int pageSize, Func<int, CancellationToken, Task<StoreProvider.ResultPage>> readPage, int maxPages = StoreProvider.MaxPages)
        : StoreProvider(new HttpClient())
    {
        public PagedProvider(int pageSize, Func<int, ResultPage> readPage, int maxPages = MaxPages)
            : this(pageSize, (page, _) => Task.FromResult(readPage(page)), maxPages)
        {
        }

        public List<int> Requested { get; } = [];

        public override ProviderInfo Info { get; } = new("paged", "Paged", StoreKind.KeyShop, "https://paged.test/");

        public override Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken) =>
            ReadPagesAsync(context, pageSize, (page, token) =>
            {
                Requested.Add(page);
                return readPage(page, token);
            }, cancellationToken, maxPages);
    }
}

/// <summary>How each paging store asks for the next page and tells there is one.</summary>
public class ProviderPagingTests
{
    private static readonly SearchContext Slovakia = new("witcher", new UserRegion("SK", "EUR"));

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task Eneba_reads_the_next_page_while_the_search_has_more_hits()
    {
        // The fixture is one page of a search with 168 hits; served again as page 2 it brings nothing new.
        var requests = new List<string>();
        var handler = new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.Query);
            return Ok(Fixture("eneba_search.html"));
        });

        var offers = await new EnebaProvider(new HttpClient(handler)).SearchAsync(Slovakia, CancellationToken.None);

        Assert.NotEmpty(offers);
        Assert.Equal(["?text=witcher&page=1&currency=EUR", "?text=witcher&page=2&currency=EUR"], requests);
    }

    [Fact]
    public async Task Eneba_stops_when_the_first_page_holds_all_hits()
    {
        var requests = 0;
        var page = Fixture("eneba_search.html").Replace("\"nbHits\": 168", "\"nbHits\": 1", StringComparison.Ordinal);
        var handler = new StubHandler(_ =>
        {
            requests++;
            return Ok(page);
        });

        await new EnebaProvider(new HttpClient(handler)).SearchAsync(Slovakia, CancellationToken.None);

        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Buka_asks_for_48_rows_a_page_and_for_the_next_page_while_there_is_one()
    {
        var bodies = new List<JsonElement>();
        var handler = new StubHandler(request =>
        {
            using var doc = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result);
            bodies.Add(doc.RootElement.Clone());
            var page = JsonDocument.Parse(Fixture("buka_search.json")).RootElement.GetProperty("get_page").GetRawText();
            return Ok(bodies.Count == 1 ? page.Replace("\"hasNext\": false", "\"hasNext\": true", StringComparison.Ordinal) : page);
        });

        var offers = await new BukaProvider(new HttpClient(handler)).SearchAsync(Slovakia, CancellationToken.None);

        Assert.NotEmpty(offers);
        Assert.Equal([0L, 1L], bodies.Select(b => b.Long("pageIndex")));
        Assert.All(bodies, b => Assert.Equal(48, b.Long("pageSize")));
    }

    [Fact]
    public async Task TwoGame_reads_the_page_its_pagination_links()
    {
        var requests = new List<string>();
        var handler = new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            var nextLink = requests.Count == 1 ? """<nav><a rel="next" href="/sk-sk/search?q=witcher&amp;page=2">›</a></nav>""" : "";
            return Ok(Fixture("2game_search.html") + nextLink);
        });

        var offers = await new TwoGameProvider(new HttpClient(handler)).SearchAsync(Slovakia, CancellationToken.None);

        Assert.NotEmpty(offers);
        Assert.Equal(["/sk-sk/search?q=witcher", "/sk-sk/search?q=witcher&page=2"], requests);
    }

    [Fact]
    public async Task Yuplay_keeps_the_search_on_the_next_page()
    {
        var requests = new List<string>();
        var handler = new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            // The site's own link drops the search.
            return Ok("""<link rel="next" href="https://www.yuplay.com/products/?page=2">""" + YuplayCard(requests.Count));
        });

        var offers = await new YuplayProvider(new HttpClient(handler)).SearchAsync(Slovakia with { MaxResults = 48 }, CancellationToken.None);

        Assert.Equal(2, offers.Count);
        Assert.Equal(["/products/?search=witcher", "/products/?search=witcher&page=2"], requests);
    }

    [Fact]
    public void Difmark_takes_a_full_page_for_the_sign_of_another()
    {
        Assert.False(DifmarkProvider.IsFullPage(Fixture("difmark_search.html")));
        Assert.True(DifmarkProvider.IsFullPage(string.Concat(Enumerable.Repeat("""<div class="one-item product-item"></div>""", 24))));
    }

    [Theory]
    [InlineData("""<a rel="next" href="/sk-sk/search?q=lego&amp;page=2">2</a>""", true)]
    [InlineData("""<link rel="next" href="https://www.yuplay.com/products/?page=2">""", true)]
    [InlineData("""<a aria-current="page" href="/sk-sk/search?q=lego&amp;page=2">2</a>""", false)]
    public void Pages_that_link_the_next_one(string html, bool links) =>
        Assert.Equal(links, StoreProvider.LinksNextPage(html));

    // The captured Yuplay page holds a single card, out of stock, so the pages here are made up.
    private static string YuplayCard(int number) =>
        $"""<article class="catalog-item"><a href="/product/the-witcher-{number}/" data-name="The Witcher {number}" data-currency="EUR" data-price="9.99">The Witcher {number}</a></article>""";
}
