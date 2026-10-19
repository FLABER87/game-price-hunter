using GamePriceHunter.Core.Models;

namespace GamePriceHunter.Core.Providers;

/// <summary>Base class of the store providers.</summary>
public abstract class StoreProvider : WebSource, IStoreProvider
{
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
}
