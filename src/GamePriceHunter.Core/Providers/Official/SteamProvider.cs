using System.Runtime.ExceptionServices;
using System.Text.Json;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.Official;

/// <summary>
/// Steam store. Uses the store search page (up to 50 rows with discounts) and the
/// storesearch API in parallel; the API also tells which currency the region uses.
/// </summary>
public sealed partial class SteamProvider(HttpClient http) : StoreProvider(http)
{
    private const string Host = "https://store.steampowered.com";

    public override ProviderInfo Info { get; } = new("steam", "Steam", StoreKind.Official, "https://store.steampowered.com/");

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        var cc = context.Region.CountryCode;
        var apiTask = SearchApiAsync($"{Host}/api/storesearch/?term={Encode(context.Query)}&l=english&cc={cc}", cancellationToken);
        var pageTask = GetStringAsync($"{Host}/search/results/?term={Encode(context.Query)}&infinite=1&cc={cc}&l=english&start=0&count=50", cancellationToken);

        // The endpoints fail independently (the HTML one more often): wait for both so no failure goes
        // unobserved, then use whatever answered.
        try
        {
            await Task.WhenAll(apiTask, pageTask).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Each endpoint is checked below.
        }
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<Offer>? apiOffers = null;
        Exception? apiError = null;
        try
        {
            apiOffers = await apiTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (IsEndpointFailure(ex))
        {
            apiError = ex;
        }

        IReadOnlyList<Offer> pageOffers = [];
        try
        {
            var page = await pageTask.ConfigureAwait(false);
            using var doc = JsonDocument.Parse(page);
            var currency = apiOffers?.FirstOrDefault()?.Price.Currency;
            pageOffers = ParseSearchResults(doc.RootElement.Str("results_html") ?? string.Empty, currency, context.Region.CurrencyCode);
        }
        catch (Exception ex) when (IsEndpointFailure(ex))
        {
            // The page only adds rows and discount details to the API result.
        }

        if (apiOffers is null)
        {
            // Without the API and without page rows there is nothing to show: report why.
            if (pageOffers.Count == 0)
                ExceptionDispatchInfo.Throw(apiError!);
            return pageOffers;
        }

        if (pageOffers.Count == 0)
            return apiOffers;

        // The page has more rows and discount info; the API only fills gaps.
        var urls = pageOffers.Select(o => AppKey(o.Url)).ToHashSet();
        return [.. pageOffers, .. apiOffers.Where(o => !urls.Contains(AppKey(o.Url)))];
    }

    /// <summary>Parses inside the task, so a search cancelled while the page loads leaves no document undisposed.</summary>
    private async Task<IReadOnlyList<Offer>> SearchApiAsync(string url, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        return ParseStoreSearch(doc.RootElement);
    }

    /// <summary>A failure of one endpoint; the search itself was not cancelled at this point.</summary>
    private static bool IsEndpointFailure(Exception ex) =>
        ex is JsonException or HttpRequestException or InvalidDataException or ProviderBlockedException or OperationCanceledException;

    internal IReadOnlyList<Offer> ParseStoreSearch(JsonElement root)
    {
        var offers = new List<Offer>();
        foreach (var item in root.Arr("items"))
        {
            var id = item.Str("id");
            var name = item.Str("name");
            var currency = item.Str("price.currency");
            var final = item.Dec("price.final");
            if (id is null || name is null || currency is null || final is null)
                continue;

            var initial = item.Dec("price.initial");
            offers.Add(CreateOffer(name, $"{Host}/app/{id}/", Money.Of(final.Value / 100m, currency)) with
            {
                RegularPrice = initial > final ? Money.Of(initial.Value / 100m, currency) : null,
                ImageUrl = item.Str("tiny_image"),
                Platform = Platform.Steam,
            });
        }
        return offers;
    }

    internal IReadOnlyList<Offer> ParseSearchResults(string html, string? knownCurrency, string regionCurrency)
    {
        var offers = new List<Offer>();
        foreach (var row in Html.Blocks(html, "<a href=\"https://store.steampowered.com/"))
        {
            var url = Html.Match(row, @"<a href=""([^""?]+)");
            var title = Html.Decode(Html.Match(row, @"<span class=""title"">(.*?)</span>"));
            var finalMinor = Html.Match(row, @"data-price-final=""(\d+)""");
            if (url is null || title.Length == 0 || finalMinor is null)
                continue;

            var finalText = Html.Decode(Html.Match(row, @"discount_final_price[^>]*>([^<]+)<"));
            // The API reports the currency authoritatively; symbols like "$" or "kr" are ambiguous.
            var currency = knownCurrency ?? CurrencyFromText(finalText, regionCurrency);
            if (currency is null)
                continue;
            var price = Money.Of(decimal.Parse(finalMinor) / 100m, currency);

            Money? regular = null;
            var originalText = Html.Decode(Html.Match(row, @"discount_original_price[^>]*>([^<]+)<"));
            if (PriceParser.TryParseAmount(originalText, out var original) && original > price.Amount)
                regular = Money.Of(original, currency);

            var itemKey = Html.Match(row, @"data-ds-itemkey=""([A-Za-z]+)_");
            offers.Add(CreateOffer(title, url, price) with
            {
                RegularPrice = regular,
                // Steam's own capsule URL: newer assets live under hashed paths, so a guessed
                // ".../header.jpg" does not exist for every app.
                ImageUrl = Html.Match(row, @"search_capsule""><img src=""([^""]+)"""),
                Type = itemKey is "Sub" or "Bundle" ? OfferType.Bundle : OfferType.Unknown,
                Platform = Platform.Steam,
            });
        }
        return offers;
    }

    /// <summary>
    /// The currency of a page price when the API named none. "$" and "¥" also stand for other
    /// currencies, so a bare one counts only when it is the region's own currency, and one after a
    /// prefix the parser doesn't know ("CDN$", "HK$", "CN¥") never does: such rows are skipped
    /// rather than shown in a wrong currency.
    /// </summary>
    internal static string? CurrencyFromText(string priceText, string regionCurrency)
    {
        var detected = PriceParser.DetectCurrency(priceText);
        if (detected is null)
            return regionCurrency;

        char? symbol = detected switch
        {
            "USD" when !priceText.Contains("USD", StringComparison.Ordinal) && !priceText.Contains("US$", StringComparison.Ordinal) => '$',
            "JPY" when !priceText.Contains("JPY", StringComparison.Ordinal) => '¥',
            _ => null,
        };
        if (symbol is null)
            return detected;

        var index = priceText.IndexOf(symbol.Value);
        var bare = index == 0 || (index > 0 && !char.IsLetter(priceText[index - 1]));
        return bare && detected == regionCurrency ? detected : null;
    }

    private static string AppKey(string url)
    {
        var match = AppKeyRegex().Match(url);
        return match.Success ? match.Value : url;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"/(app|sub|bundle)/\d+")]
    private static partial System.Text.RegularExpressions.Regex AppKeyRegex();
}
