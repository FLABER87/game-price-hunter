using System.Text.Json;
using System.Text.RegularExpressions;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.Official;

/// <summary>
/// 2game: authorized key store (rebuilt on Next.js in late 2026, replacing a Shopify store) with a storefront per country
/// ("/sk-sk", "/en-gb", "/ja-jp"...) priced in local currency. The search page carries its results
/// (24 a page) and their prices as JSON in the page's React Server Components payload.
/// </summary>
public sealed partial class TwoGameProvider(HttpClient http) : StoreProvider(http)
{
    private const string Host = "https://www.2game.com";
    private const int PageSize = 24;

    /// <summary>
    /// Storefront path per country, from the sitemaps listed in robots.txt. Other countries get the
    /// "Rest of world" storefront (en-ww), which shows no prices.
    /// </summary>
    private static readonly Dictionary<string, string> Markets = BuildMarkets();

    public override ProviderInfo Info { get; } = new("2game", "2game", StoreKind.Official, "https://www.2game.com/");

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        if (!Markets.TryGetValue(context.Region.CountryCode, out var market))
            return [];

        return await ReadPagesAsync(context, PageSize, async (page, token) =>
        {
            var url = $"{Host}/{market}/search?q={Encode(context.Query)}" + (page > 1 ? $"&page={page}" : "");
            var html = await GetStringAsync(url, token).ConfigureAwait(false);
            return new ResultPage(Parse(html, market, DateTimeOffset.UtcNow), LinksNextPage(html));
        }, cancellationToken).ConfigureAwait(false);
    }

    internal IReadOnlyList<Offer> Parse(string html, string market, DateTimeOffset now)
    {
        var payload = ServerPayload(html);
        if (payload.Length == 0)
            throw new InvalidDataException("2game search page has no Next.js data.");

        var offers = new List<Offer>();
        // A search without results renders neither list.
        using var hits = JsonAfter(payload, "hits", '[');
        if (hits is null)
            return offers;
        using var prices = JsonAfter(payload, "prices", '{')
            ?? throw new InvalidDataException("2game search results come without prices.");
        // "Rest of world" answers {"status":"unpriced","reason":"STORE_NOT_PRICED"}.
        if (prices.RootElement.Str("status") != "ok")
            return offers;
        var currency = prices.RootElement.Str("scope.currency")
            ?? throw new InvalidDataException("2game storefront currency not found.");
        var parents = prices.RootElement.Prop("parents");

        foreach (var hit in hits.RootElement.EnumerateArray())
        {
            var id = hit.Str("id");
            var slug = hit.Str("slug");
            var title = hit.Str("title");
            if (id is null || slug is null || string.IsNullOrWhiteSpace(title) || parents?.Prop(id) is not { } entry)
                continue;
            // Items that are not for sale say "unpublished" or "none".
            if (entry.Str("status") != "priced" || CurrentPrice(entry, now) is not { } current)
                continue;

            var (price, compareAt) = current;
            var platform = hit.Str("platform");
            offers.Add(CreateOffer(title.Trim(), $"{Host}/{market}/product/{Uri.EscapeDataString(slug)}", Money.Of(price, currency)) with
            {
                RegularPrice = compareAt > price ? Money.Of(compareAt.Value, currency) : null,
                ImageUrl = hit.Str("wide.src") ?? hit.Str("card.src"),
                // "Microsoft" keys are redeemed in the Microsoft Store, like the other Xbox keys.
                Platform = string.Equals(platform, "Microsoft", StringComparison.OrdinalIgnoreCase) ? Platform.Xbox : OfferClassifier.ParsePlatform(platform),
                Type = hit.Str("type") == "dlc" ? OfferType.Dlc : OfferType.Unknown,
            });
        }
        return offers;
    }

    /// <summary>
    /// The cheapest variant's price at <paramref name="now"/>. Each variant lists price segments
    /// over the next day ("from"/"until", Unix seconds), so a sale that ends today is a segment of its own.
    /// </summary>
    private static (decimal Price, decimal? CompareAt)? CurrentPrice(JsonElement entry, DateTimeOffset now)
    {
        var seconds = now.ToUnixTimeSeconds();
        (decimal Price, decimal? CompareAt)? best = null;
        foreach (var choice in entry.Arr("choices"))
        {
            var segments = choice.Arr("segments").ToList();
            if (segments.Count == 0)
                continue;
            var segment = segments.FirstOrDefault(s => s.Long("from") <= seconds && seconds < s.Long("until"));
            if (segment.ValueKind == JsonValueKind.Undefined)
                segment = segments[0];
            if (segment.Dec("price") is decimal price && price > 0 && (best is null || price < best.Value.Price))
                best = (price, segment.Dec("compareAt"));
        }
        return best;
    }

    /// <summary>
    /// Joins the page's React Server Components payload: the chunks of
    /// <c>self.__next_f.push([1,"..."])</c>, each a JSON string literal.
    /// </summary>
    private static string ServerPayload(string html)
    {
        var payload = new System.Text.StringBuilder();
        foreach (Match match in PayloadChunkRegex().Matches(html))
        {
            using var chunk = JsonDocument.Parse(match.Groups[1].Value);
            payload.Append(chunk.RootElement.GetString());
        }
        return payload.ToString();
    }

    /// <summary>
    /// The first <c>"property":[...]</c> (or <c>{...}</c>) in the payload; other occurrences of the
    /// property may hold a "$12:props:..." reference to it instead.
    /// </summary>
    private static JsonDocument? JsonAfter(string payload, string property, char opening)
    {
        var marker = $"\"{property}\":";
        var index = payload.IndexOf(marker + opening, StringComparison.Ordinal);
        if (index < 0)
            return null;
        var json = Html.ExtractJson(payload[index..], marker);
        return json is null ? null : JsonDocument.Parse(json);
    }

    private static Dictionary<string, string> BuildMarkets()
    {
        var markets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        const string paths = "bg-bg cs-cz da-dk de-at de-de el-gr en-ae en-au en-ca en-ch en-cy en-gb en-hk en-ie en-il en-in en-kw en-kz " +
            "en-lu en-mt en-nz en-ph en-qa en-sa en-sg en-us en-za es-cl es-co es-cr es-es es-mx es-pe es-uy et-ee fi-fi fr-be fr-fr " +
            "hr-hr hu-hu id-id it-it ja-jp ko-kr lt-lt lv-lv ms-my nb-no nl-nl pl-pl pt-br pt-pt ro-ro sk-sk sl-si sv-se th-th uk-ua " +
            "vi-vn zh-cn zh-tw";
        foreach (var path in paths.Split(' '))
            markets[path[3..]] = path;
        return markets;
    }

    [GeneratedRegex(@"self\.__next_f\.push\(\[1,(""(?:[^""\\]|\\.)*"")\]\)")]
    private static partial Regex PayloadChunkRegex();
}
