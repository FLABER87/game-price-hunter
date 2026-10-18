using GamePriceHunter.Core.Models;

namespace GamePriceHunter.Core.Providers;

/// <summary>A store (or aggregator) that can be searched by game title.</summary>
public interface IStoreProvider
{
    ProviderInfo Info { get; }

    /// <summary>
    /// Returns raw offers for the query. Relevance filtering, classification and region
    /// checks are done by the search service, so providers should return what the store gives.
    /// </summary>
    Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken);
}

/// <summary>The store refused the request (bot protection, rate limit, geo block).</summary>
public sealed class ProviderBlockedException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}

/// <summary>The provider cannot run in the current configuration (e.g. missing API key).</summary>
public sealed class ProviderSkippedException(string reason) : Exception(reason);
