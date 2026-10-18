namespace GamePriceHunter.Core.Models;

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

    /// <summary>Short note shown in settings, e.g. "Prices in USD (US region)".</summary>
    public string? Note { get; init; }

    public string Host => Uri.TryCreate(HomeUrl, UriKind.Absolute, out var uri) ? uri.Host : HomeUrl;
}
