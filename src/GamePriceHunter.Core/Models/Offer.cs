namespace GamePriceHunter.Core.Models;

/// <summary>A single purchasable item found in a store.</summary>
public sealed record Offer
{
    public required string ProviderId { get; init; }

    /// <summary>Store that sells the item. Differs from the provider for aggregators.</summary>
    public required string StoreName { get; init; }

    public required StoreKind StoreKind { get; init; }

    /// <summary>Store home page; used for the store icon.</summary>
    public string? StoreUrl { get; init; }

    public required string Title { get; init; }

    public required string Url { get; init; }

    public string? ImageUrl { get; init; }

    public required Money Price { get; init; }

    /// <summary>Price before discount, when the store reports one.</summary>
    public Money? RegularPrice { get; init; }

    public OfferType Type { get; init; }

    public Platform Platform { get; init; }

    /// <summary>Activation region as the store names it ("Global", "EU", "RU/CIS"...).</summary>
    public string? Region { get; init; }

    public RegionCompatibility RegionCompatibility { get; init; }

    /// <summary>Marketplace seller name.</summary>
    public string? Seller { get; init; }

    /// <summary>The marketplace's rating of the seller.</summary>
    public SellerRating? SellerRating { get; init; }

    /// <summary>Times the item sold, when the store shows it.</summary>
    public SalesCount? Sales { get; init; }

    /// <summary>Query relevance in 0..1, filled by the search service.</summary>
    public double Relevance { get; init; }

    public int? DiscountPercent
    {
        get
        {
            if (RegularPrice is not { } regular || regular.Currency != Price.Currency || regular.Amount <= 0 || Price.Amount >= regular.Amount)
                return null;
            var percent = (int)Math.Round((1 - Price.Amount / regular.Amount) * 100, MidpointRounding.AwayFromZero);
            return percent > 0 ? percent : null;
        }
    }
}
