using System.Globalization;
using System.Text.Json;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Providers.Aggregators;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Giveaways.Sources;

/// <summary>
/// CheapShark's deals at 100% off in ~30 stores. Catches what the stores' own lists miss, like Epic
/// games given away outside the weekly section, but its prices are US ones and it says nothing about regions.
/// </summary>
public sealed class CheapSharkGiveawaySource(HttpClient http) : GiveawaySource(http)
{
    public const string SourceId = "cheapshark";

    public override ProviderInfo Info { get; } = new(SourceId, "CheapShark", StoreKind.Official, "https://www.cheapshark.com/")
    {
        IsAggregator = true,
        Note = ProviderNote.CheapShark,
    };

    public override async Task<IReadOnlyList<Giveaway>> GetAsync(UserRegion region, CancellationToken cancellationToken)
    {
        var stores = await CheapSharkApi.GetStoreNamesAsync(ct => GetJsonAsync($"{CheapSharkApi.Url}/stores", ct, CheapSharkApi.Identify), cancellationToken).ConfigureAwait(false);
        using var doc = await GetJsonAsync($"{CheapSharkApi.Url}/deals?upperPrice=0&pageSize=60", cancellationToken, CheapSharkApi.Identify).ConfigureAwait(false);
        return Parse(doc.RootElement, stores);
    }

    internal IReadOnlyList<Giveaway> Parse(JsonElement root, IReadOnlyDictionary<string, string> stores)
    {
        var giveaways = new List<Giveaway>();
        if (root.ValueKind != JsonValueKind.Array)
            return giveaways;

        foreach (var deal in root.EnumerateArray())
        {
            var title = deal.Str("title")?.Trim();
            var dealId = deal.Str("dealID");
            var storeId = deal.Str("storeID");
            if (string.IsNullOrEmpty(title) || dealId is null || storeId is null || !stores.TryGetValue(storeId, out var storeName))
                continue;

            // Free-to-play games cost nothing all the time; a giveaway is a paid game at 100% off.
            var sale = Amount(deal.Str("salePrice"));
            var normal = Amount(deal.Str("normalPrice"));
            if (sale is not 0 || normal is not > 0)
                continue;

            giveaways.Add(new Giveaway
            {
                SourceId = SourceId,
                Title = title,
                StoreName = storeName,
                StoreUrl = CheapSharkApi.StoreSites.GetValueOrDefault(storeName),
                Url = $"https://www.cheapshark.com/redirect?dealID={dealId}",
                ImageUrl = deal.Str("thumb"),
                Kind = OfferClassifier.DetectType(title) == OfferType.Dlc ? GiveawayKind.Dlc : GiveawayKind.Game,
                Platform = CheapSharkApi.PlatformOf(storeName),
                Worth = Money.Of(normal.Value, "USD"),
                PublishedAt = deal.Long("lastChange") is > 0 and var changed ? DateTimeOffset.FromUnixTimeSeconds(changed) : null,
            });
        }
        return giveaways;
    }

    private static decimal? Amount(string? text) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ? amount : null;
}
