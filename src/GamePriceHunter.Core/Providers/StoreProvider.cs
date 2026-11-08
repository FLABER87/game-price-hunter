using System.Diagnostics;
using GamePriceHunter.Core.Models;

namespace GamePriceHunter.Core.Providers;

/// <summary>Base class of the store providers.</summary>
public abstract class StoreProvider : WebSource, IStoreProvider
{
    /// <summary>
    /// The most result pages one search reads from a store, whatever the result limit: a few quick
    /// requests per search is what stores tolerate.
    /// </summary>
    internal const int MaxPages = 5;

    protected StoreProvider(HttpClient http)
        : base(http)
    {
    }

    public abstract ProviderInfo Info { get; }

    protected override string SourceName => Info.Name;

    public abstract Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken);

    protected Offer CreateOffer(string title, string url, Money price) => new()
    {
        ProviderId = Info.Id,
        StoreName = Info.Name,
        StoreKind = Info.Kind,
        StoreUrl = Info.HomeUrl,
        Title = title,
        Url = url,
        Price = price,
    };

    /// <summary>Whether a server-rendered result page links the next one (&lt;a rel="next"&gt; or &lt;link rel="next"&gt;).</summary>
    protected internal static bool LinksNextPage(string html) => html.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase);

    /// <summary>One page of search results, and whether the store has a page after it.</summary>
    protected internal readonly record struct ResultPage(IReadOnlyList<Offer> Offers, bool HasMore);

    /// <summary>
    /// Reads search result pages (1, 2, 3...) one after another until there are
    /// <see cref="SearchContext.MaxResults"/> offers, the store has no more pages, a page brings no
    /// offer not seen before, or <paramref name="maxPages"/> pages are read.
    /// </summary>
    /// <remarks>
    /// Errors of the first page are the search's errors. A later page only adds to what the first one
    /// found, so when it fails (HTTP error, rate limit, unexpected markup) or does not finish within
    /// half of <see cref="SearchContext.Timeout"/>, the search ends with the offers read so far
    /// instead of failing or timing out.
    /// </remarks>
    /// <param name="pageSize">How many results the store puts on one page.</param>
    protected internal async Task<IReadOnlyList<Offer>> ReadPagesAsync(
        SearchContext context,
        int pageSize,
        Func<int, CancellationToken, Task<ResultPage>> readPage,
        CancellationToken cancellationToken,
        int maxPages = MaxPages)
    {
        var started = Stopwatch.GetTimestamp();
        var pages = Math.Clamp((context.MaxResults + pageSize - 1) / pageSize, 1, maxPages);

        var page = await readPage(1, cancellationToken).ConfigureAwait(false);
        var offers = new List<Offer>(page.Offers);
        var seen = new HashSet<string>(offers.Select(o => o.Url), StringComparer.OrdinalIgnoreCase);

        for (var number = 2; number <= pages && page.HasMore && offers.Count < context.MaxResults; number++)
        {
            var timeLeft = context.Timeout / 2 - Stopwatch.GetElapsedTime(started);
            if (timeLeft <= TimeSpan.Zero)
                break;

            using var pageTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pageTimeout.CancelAfter(timeLeft);
            try
            {
                page = await readPage(number, pageTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // Past its last page a store may serve the last one again, or the first.
            var fresh = page.Offers.Where(o => seen.Add(o.Url)).ToList();
            if (fresh.Count == 0)
                break;
            offers.AddRange(fresh);
        }
        return offers;
    }
}
