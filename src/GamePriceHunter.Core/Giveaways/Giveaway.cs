using GamePriceHunter.Core.Models;

namespace GamePriceHunter.Core.Giveaways;

public enum GiveawayKind
{
    Game,

    /// <summary>DLC and in-game items.</summary>
    Dlc,

    /// <summary>Beta and playtest access.</summary>
    Beta,
}

/// <summary>Something a store gives away to keep for a limited time.</summary>
public sealed record Giveaway
{
    /// <summary>The source whose details are shown.</summary>
    public required string SourceId { get; init; }

    /// <summary>Other sources that reported the same giveaway.</summary>
    public IReadOnlyList<string> OtherSourceIds { get; init; } = [];

    public required string Title { get; init; }

    /// <summary>Where the giveaway is claimed: "Epic Games Store", "IndieGala", "Alienware Arena".</summary>
    public required string StoreName { get; init; }

    /// <summary>Store home page; used for the store icon.</summary>
    public string? StoreUrl { get; init; }

    /// <summary>The page where the giveaway is claimed.</summary>
    public required string Url { get; init; }

    public string? ImageUrl { get; init; }

    public GiveawayKind Kind { get; init; }

    /// <summary>Where the game is activated or downloaded.</summary>
    public Platform Platform { get; init; }

    /// <summary>The usual price, when the source reports it.</summary>
    public Money? Worth { get; init; }

    /// <summary>When the giveaway starts; a time in the future means it is coming soon.</summary>
    public DateTimeOffset? StartsAt { get; init; }

    public DateTimeOffset? EndsAt { get; init; }

    /// <summary>The source gives the end without a time zone, so only the day is reliable.</summary>
    public bool EndsOnDateOnly { get; init; }

    /// <summary>When the source listed the giveaway; giveaways without an end are dropped when they get old.</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>The store was asked about the user's country (Epic, Steam); aggregators don't say.</summary>
    public bool IsRegionChecked { get; init; }
}

/// <summary>Outcome of one source within a refresh.</summary>
public sealed record GiveawaySourceResult(string SourceId, ProviderStatus Status, int Count, TimeSpan Elapsed, string? Error = null);

/// <summary>Giveaways from all sources, merged.</summary>
public sealed record GiveawayFeed(
    DateTimeOffset FetchedAt,
    string CountryCode,
    IReadOnlyList<Giveaway> Giveaways,
    IReadOnlyList<GiveawaySourceResult> Sources);
