using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Providers;

namespace GamePriceHunter.Core.Giveaways;

/// <summary>A site that lists what is free to keep right now.</summary>
public interface IGiveawaySource
{
    ProviderInfo Info { get; }

    /// <summary>Current and announced giveaways; the service merges duplicates and drops ended ones.</summary>
    Task<IReadOnlyList<Giveaway>> GetAsync(UserRegion region, CancellationToken cancellationToken);
}

public abstract class GiveawaySource : WebSource, IGiveawaySource
{
    protected GiveawaySource(HttpClient http)
        : base(http)
    {
    }

    public abstract ProviderInfo Info { get; }

    protected override string SourceName => Info.Name;

    public abstract Task<IReadOnlyList<Giveaway>> GetAsync(UserRegion region, CancellationToken cancellationToken);
}
