using System.Globalization;
using System.Text.Json;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.Aggregators;

/// <summary>
/// CheapShark: free deal aggregator over ~15 authorized stores (US prices in USD). Stores
/// with their own regional providers here are skipped to avoid duplicate, wrong-region rows.
/// </summary>
public sealed class CheapSharkProvider(HttpClient http) : StoreProvider(http)
{
    /// <summary>
    /// CheapShark store ids already covered by direct regional providers: Steam, GOG, Fanatical,
    /// GamersGate, Voidu, Gamesplanet and 2Game (Voidu and 2Game are inactive there).
    /// Epic, Humble and GameBillet reject non-browser clients, so they come through here.
    /// </summary>
    private static readonly HashSet<string> CoveredStores = ["1", "7", "15", "2", "24", "27", "29"];

    public override ProviderInfo Info { get; } = new("cheapshark", "CheapShark", StoreKind.Official, "https://www.cheapshark.com/")
    {
        IsAggregator = true,
        Note = ProviderNote.CheapShark,
    };

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        var stores = await CheapSharkApi.GetStoreNamesAsync(ct => GetJsonAsync($"{CheapSharkApi.Url}/stores", ct, CheapSharkApi.Identify), cancellationToken).ConfigureAwait(false);
        using var doc = await GetJsonAsync($"{CheapSharkApi.Url}/deals?title={Encode(context.Query)}&exact=0&pageSize=60&pageNumber=0", cancellationToken, CheapSharkApi.Identify).ConfigureAwait(false);
        return Parse(doc.RootElement, stores);
    }

    internal IReadOnlyList<Offer> Parse(JsonElement root, IReadOnlyDictionary<string, string> stores)
    {
        var offers = new List<Offer>();
        if (root.ValueKind != JsonValueKind.Array)
            return offers;

        foreach (var deal in root.EnumerateArray())
        {
            var storeId = deal.Str("storeID");
            var title = deal.Str("title");
            var dealId = deal.Str("dealID");
            if (storeId is null || title is null || dealId is null || CoveredStores.Contains(storeId))
                continue;
            if (!decimal.TryParse(deal.Str("salePrice"), NumberStyles.Number, CultureInfo.InvariantCulture, out var sale))
                continue;

            var storeName = stores.TryGetValue(storeId, out var name) ? name : $"Store #{storeId}";
            decimal.TryParse(deal.Str("normalPrice"), NumberStyles.Number, CultureInfo.InvariantCulture, out var normal);

            offers.Add(CreateOffer(title, $"https://www.cheapshark.com/redirect?dealID={dealId}", Money.Of(sale, "USD")) with
            {
                StoreName = storeName,
                StoreUrl = CheapSharkApi.StoreSites.GetValueOrDefault(storeName),
                RegularPrice = normal > sale ? Money.Of(normal, "USD") : null,
                ImageUrl = deal.Str("thumb"),
                Platform = CheapSharkApi.PlatformOf(storeName),
            });
        }
        return offers;
    }
}
