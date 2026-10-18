using System.Net;
using System.Text.Json;
using GamePriceHunter.Core.Diagnostics;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Search;

namespace GamePriceHunter.Core.Tests;

/// <summary>Search suggestions against Steam responses captured for "witcher" (Fixtures folder).</summary>
public class SuggestionServiceTests
{
    private static readonly UserRegion Germany = new("DE", "EUR");

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    /// <summary>A service whose Steam answers from the fixtures; <paramref name="lookup"/> replaces the GetItems answer.</summary>
    private static (SuggestionService Service, List<Uri> Lookups) Steam(Func<HttpResponseMessage>? lookup = null)
    {
        var lookups = new List<Uri>();
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.Host != "api.steampowered.com")
                return Ok(Fixture("steam_suggest_witcher.json"));
            lookups.Add(request.RequestUri);
            return lookup?.Invoke() ?? Ok(Fixture("steam_getitems.json"));
        });
        return (new SuggestionService(new HttpClient(handler), NullLogger.Instance), lookups);
    }

    [Fact]
    public async Task Free_games_are_marked_and_unreleased_ones_dropped()
    {
        var (service, lookups) = Steam();

        var suggestions = await service.GetAsync("witcher", Germany, CancellationToken.None);

        Assert.Equal(9, suggestions.Count);
        Assert.DoesNotContain(suggestions, s => s.Title.Contains("Songs of the Past"));
        var gwent = Assert.Single(suggestions, s => s.Title.StartsWith("GWENT"));
        Assert.True(gwent.IsFree);
        Assert.Null(gwent.Price);
        Assert.Equal(49.99m, suggestions[0].Price?.Amount);
        Assert.All(suggestions.Where(s => s != gwent), s => Assert.False(s.IsFree));

        // One request, about the two apps without a price only.
        var input = Uri.UnescapeDataString(Assert.Single(lookups).Query);
        Assert.Contains("5006530", input);
        Assert.Contains("1284410", input);
        Assert.DoesNotContain("292030", input);
    }

    [Fact]
    public async Task Steam_is_asked_about_an_app_once()
    {
        var (service, lookups) = Steam();

        await service.GetAsync("witcher", Germany, CancellationToken.None);
        var again = await service.GetAsync("witcher", Germany, CancellationToken.None);

        Assert.Single(lookups);
        Assert.True(Assert.Single(again, s => s.Title.StartsWith("GWENT")).IsFree);
        Assert.DoesNotContain(again, s => s.Title.Contains("Songs of the Past"));
    }

    [Fact]
    public async Task Suggestions_survive_a_failed_lookup()
    {
        var (service, lookups) = Steam(() => new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var suggestions = await service.GetAsync("witcher", Germany, CancellationToken.None);
        Assert.Equal(10, suggestions.Count);
        Assert.All(suggestions, s => Assert.False(s.IsFree));

        // A failure is not remembered: the next keystroke asks again.
        await service.GetAsync("witcher", Germany, CancellationToken.None);
        Assert.Equal(2, lookups.Count);
    }

    [Fact]
    public async Task Suggestions_survive_a_connection_dropped_mid_answer()
    {
        var (service, _) = Steam(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) });

        var suggestions = await service.GetAsync("witcher", Germany, CancellationToken.None);

        Assert.Equal(10, suggestions.Count);
    }

    [Fact]
    public async Task Apps_Steam_could_not_describe_are_asked_about_again()
    {
        var answers = new Queue<string>([
            """{"response":{"store_items":[{"id":5006530,"appid":5006530,"success":1,"is_coming_soon":true},{"id":1284410,"appid":0,"success":42}]}}""",
            Fixture("steam_getitems.json"),
        ]);
        var (service, lookups) = Steam(() => Ok(answers.Dequeue()));

        var first = await service.GetAsync("witcher", Germany, CancellationToken.None);
        var second = await service.GetAsync("witcher", Germany, CancellationToken.None);

        Assert.False(Assert.Single(first, s => s.Title.StartsWith("GWENT")).IsFree);
        Assert.True(Assert.Single(second, s => s.Title.StartsWith("GWENT")).IsFree);
        var retry = Uri.UnescapeDataString(lookups[1].Query);
        Assert.Contains("1284410", retry);
        Assert.DoesNotContain("5006530", retry);
    }

    [Fact]
    public void Zero_price_means_free()
    {
        using var doc = JsonDocument.Parse("""{"items":[{"id":7,"name":"Giveaway","price":{"currency":"EUR","initial":1999,"final":0}}]}""");

        var hit = Assert.Single(SuggestionService.Parse(doc.RootElement));

        Assert.True(hit.Suggestion.IsFree);
        Assert.Null(hit.Suggestion.Price);
    }

    [Fact]
    public void Preorders_keep_their_price()
    {
        var preorder = new SuggestionService.SearchHit(7, new GameSuggestion("Upcoming", null, Money.Of(59.99m, "EUR")));

        var suggestions = SuggestionService.Resolve([preorder], new Dictionary<long, PricelessApp> { [7] = PricelessApp.ComingSoon });

        Assert.Equal(59.99m, Assert.Single(suggestions).Price?.Amount);
    }

    /// <summary>A response body whose connection breaks after the headers arrived.</summary>
    private sealed class BrokenStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("connection reset"));
    }
}
