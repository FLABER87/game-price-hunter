namespace GamePriceHunter.Core.Models;

/// <summary>
/// A marketplace's rating of a seller, on the marketplace's own scale: stars out of
/// <see cref="OutOf"/> (GGSEL: 5), or open-ended points when <see cref="OutOf"/> is null (Plati).
/// </summary>
public readonly record struct SellerRating(decimal Value, decimal? OutOf);

/// <summary>
/// How many times an item sold, as the store shows it. A seller may cap the count: once sales
/// reach the cap, the store shows only the cap as a lower bound ("100+").
/// </summary>
public readonly record struct SalesCount(long Count, bool AtLeast);
