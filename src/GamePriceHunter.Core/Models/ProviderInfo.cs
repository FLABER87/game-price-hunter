namespace GamePriceHunter.Core.Models;

/// <summary>What the store note in the UI says; the app has the text in every language.</summary>
public enum ProviderNote
{
    None,

    /// <summary>Prices come from independent sellers.</summary>
    Marketplace,

    /// <summary>Russian store: keys and gifts are usually for Russia and the CIS.</summary>
    RussianStore,

    /// <summary>CheapShark: Epic, Humble, GMG and other stores, US prices in USD.</summary>
    CheapShark,

    /// <summary>IsThereAnyDeal: regional prices from 50+ stores, needs a free API key.</summary>
    IsThereAnyDeal,
}

/// <summary>Static description of a store provider.</summary>
/// <param name="Id">Stable identifier used in settings (lowercase, no spaces).</param>
/// <param name="Name">Display name.</param>
/// <param name="Kind">Seller kind of the store itself (aggregators report it per offer).</param>
/// <param name="HomeUrl">Store home page; also used to resolve the store icon.</param>
public sealed record ProviderInfo(string Id, string Name, StoreKind Kind, string HomeUrl)
{
    /// <summary>True when the provider reports offers from many stores (CheapShark, IsThereAnyDeal).</summary>
    public bool IsAggregator { get; init; }

    /// <summary>True when the provider only works after the user enters an API key.</summary>
    public bool RequiresApiKey { get; init; }

    public bool EnabledByDefault { get; init; } = true;

    /// <summary>Short note about the store, shown in its tooltip and in Settings.</summary>
    public ProviderNote Note { get; init; }

    public string Host => Uri.TryCreate(HomeUrl, UriKind.Absolute, out var uri) ? uri.Host : HomeUrl;
}
