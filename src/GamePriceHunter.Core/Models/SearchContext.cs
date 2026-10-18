namespace GamePriceHunter.Core.Models;

/// <summary>The user's region as used for store requests.</summary>
/// <param name="CountryCode">ISO 3166-1 alpha-2, upper case ("SK").</param>
/// <param name="CurrencyCode">ISO 4217, upper case ("EUR").</param>
public sealed record UserRegion(string CountryCode, string CurrencyCode)
{
    public static UserRegion Default { get; } = new("US", "USD");
}

/// <summary>Everything a provider needs to run one search.</summary>
public sealed record SearchContext(string Query, UserRegion Region)
{
    /// <summary>Upper bound of offers a provider should return.</summary>
    public int MaxResults { get; init; } = 60;

    /// <summary>Optional per-provider secrets (API keys) by provider id.</summary>
    public IReadOnlyDictionary<string, string> ApiKeys { get; init; } = new Dictionary<string, string>();
}

/// <summary>Outcome of one provider within a search.</summary>
public sealed record ProviderSearchResult(
    ProviderInfo Provider,
    ProviderStatus Status,
    IReadOnlyList<Offer> Offers,
    TimeSpan Elapsed,
    string? Error = null)
{
    /// <summary>Offers the store returned before relevance filtering.</summary>
    public int RawCount { get; init; }
}
