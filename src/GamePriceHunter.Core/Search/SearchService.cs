using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GamePriceHunter.Core.Diagnostics;
using GamePriceHunter.Core.Geo;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Providers;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Search;

public sealed record SearchOptions
{
    /// <summary>Provider ids to query; null means every provider enabled by default.</summary>
    public IReadOnlySet<string>? Providers { get; init; }

    public TimeSpan ProviderTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public MatchMode MatchMode { get; init; } = MatchMode.AllWords;

    public int MaxResultsPerProvider { get; init; } = 60;

    public IReadOnlyDictionary<string, string> ApiKeys { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Fans a query out to all providers in parallel and streams each provider's result as
/// soon as it completes. Offers are relevance-filtered and classified before they are yielded.
/// </summary>
public sealed class SearchService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    private readonly IReadOnlyList<IStoreProvider> _providers;
    private readonly IAppLogger _logger;
    private readonly ConcurrentDictionary<string, (DateTime Time, IReadOnlyList<Offer> Offers)> _cache = new();

    public SearchService(IReadOnlyList<IStoreProvider> providers, IAppLogger logger)
    {
        _providers = providers;
        _logger = logger;
    }

    public IReadOnlyList<IStoreProvider> Providers => _providers;

    public IEnumerable<IStoreProvider> SelectProviders(SearchOptions options) =>
        _providers.Where(p => options.Providers?.Contains(p.Info.Id) ?? p.Info.EnabledByDefault);

    public async IAsyncEnumerable<ProviderSearchResult> SearchAsync(
        string query,
        UserRegion region,
        SearchOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        var matcher = new TitleMatcher(query, options.MatchMode);
        if (matcher.IsEmpty)
            yield break;

        var context = new SearchContext(query, region)
        {
            MaxResults = options.MaxResultsPerProvider,
            ApiKeys = options.ApiKeys,
        };

        var selected = SelectProviders(options).ToList();
        var channel = Channel.CreateUnbounded<ProviderSearchResult>(new UnboundedChannelOptions { SingleReader = true });
        _logger.Info($"Search \"{query}\" in {region.CountryCode}/{region.CurrencyCode} across {selected.Count} providers.");

        var tasks = selected.Select(async provider =>
        {
            var result = await RunProviderAsync(provider, context, matcher, options, cancellationToken).ConfigureAwait(false);
            await channel.Writer.WriteAsync(result, CancellationToken.None).ConfigureAwait(false);
        }).ToList();

        _ = Task.WhenAll(tasks).ContinueWith(_ => channel.Writer.TryComplete(), TaskScheduler.Default);

        await foreach (var result in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return result;
    }

    public void ClearCache() => _cache.Clear();

    private async Task<ProviderSearchResult> RunProviderAsync(
        IStoreProvider provider,
        SearchContext context,
        TitleMatcher matcher,
        SearchOptions options,
        CancellationToken cancellationToken)
    {
        var info = provider.Info;
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ProviderTimeout);

        try
        {
            var cacheKey = $"{info.Id}|{TextNormalizer.Normalize(context.Query)}|{context.Region.CountryCode}|{context.Region.CurrencyCode}";
            IReadOnlyList<Offer> raw;
            if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.Time < CacheLifetime)
            {
                raw = cached.Offers;
            }
            else
            {
                raw = await provider.SearchAsync(context, timeout.Token).ConfigureAwait(false);
                _cache[cacheKey] = (DateTime.UtcNow, raw);
            }

            var offers = Refine(raw, matcher, context);
            _logger.Debug($"{info.Name}: {offers.Count}/{raw.Count} offers in {stopwatch.ElapsedMilliseconds} ms.");
            return new ProviderSearchResult(info, ProviderStatus.Succeeded, offers, stopwatch.Elapsed) { RawCount = raw.Count };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ProviderSearchResult(info, ProviderStatus.Cancelled, [], stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn($"{info.Name}: timed out after {options.ProviderTimeout.TotalSeconds:0} s.");
            return new ProviderSearchResult(info, ProviderStatus.TimedOut, [], stopwatch.Elapsed, "Timed out");
        }
        catch (ProviderBlockedException ex)
        {
            _logger.Warn($"{info.Name}: blocked. {ex.Message}");
            return new ProviderSearchResult(info, ProviderStatus.Blocked, [], stopwatch.Elapsed, ex.Message);
        }
        catch (ProviderSkippedException ex)
        {
            return new ProviderSearchResult(info, ProviderStatus.Skipped, [], stopwatch.Elapsed, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.Error($"{info.Name}: search failed.", ex);
            return new ProviderSearchResult(info, ProviderStatus.Failed, [], stopwatch.Elapsed, ex.Message);
        }
    }

    internal static IReadOnlyList<Offer> Refine(IReadOnlyList<Offer> raw, TitleMatcher matcher, SearchContext context)
    {
        var byUrl = new Dictionary<string, Offer>(StringComparer.OrdinalIgnoreCase);
        foreach (var offer in raw)
        {
            if (string.IsNullOrWhiteSpace(offer.Title) || !IsWebUrl(offer.Url))
                continue;

            if (offer.Price.Amount < 0)
                continue;

            var score = matcher.Score(offer.Title);
            if (score <= 0)
                continue;

            // Free items are real only in official stores, and only interesting when they are
            // what was searched for (a free-to-play game), not a free soundtrack or mod kit.
            if (offer.Price.Amount == 0 && (offer.StoreKind != StoreKind.Official || score < 0.95))
                continue;

            var region = offer.Region is not null
                ? RegionTags.FromLabel(offer.Region, context.Region.CountryCode)
                : RegionTags.FromTitle(offer.Title, context.Region.CountryCode);

            var refined = offer with
            {
                Title = offer.Title.Trim(),
                Relevance = score,
                Type = offer.Type == OfferType.Unknown ? OfferClassifier.DetectType(offer.Title) : offer.Type,
                Platform = offer.Platform == Platform.Unknown ? OfferClassifier.DetectPlatform(offer.Title) : offer.Platform,
                Region = region?.Label ?? offer.Region,
                RegionCompatibility = offer.RegionCompatibility != RegionCompatibility.Unknown
                    ? offer.RegionCompatibility
                    : region?.Compatibility ?? RegionCompatibility.Unknown,
            };

            if (!byUrl.TryGetValue(refined.Url, out var existing) || refined.Price.Amount < existing.Price.Amount)
                byUrl[refined.Url] = refined;
        }

        return byUrl.Values
            .OrderByDescending(o => o.Relevance)
            .ThenBy(o => o.Price.Amount)
            .Take(context.MaxResults)
            .ToList();
    }

    private static bool IsWebUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
