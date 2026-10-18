using System.Globalization;
using System.Reflection;
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
    private const string Api = "https://www.cheapshark.com/api/1.0";

    /// <summary>
    /// CheapShark store ids already covered by direct regional providers: Steam, GOG, Fanatical,
    /// GamersGate, Voidu, Gamesplanet and 2Game (Voidu and 2Game are inactive there).
    /// Epic, Humble and GameBillet reject non-browser clients, so they come through here.
    /// </summary>
    private static readonly HashSet<string> CoveredStores = ["1", "7", "15", "2", "24", "27", "29"];

    private static readonly Dictionary<string, string> StoreSites = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GamersGate"] = "https://www.gamersgate.com/",
        ["GreenManGaming"] = "https://www.greenmangaming.com/",
        ["Uplay"] = "https://store.ubisoft.com/",
        ["WinGameStore"] = "https://www.wingamestore.com/",
        ["GameBillet"] = "https://www.gamebillet.com/",
        ["Gamesplanet"] = "https://www.gamesplanet.com/",
        ["Gamesload"] = "https://www.gamesload.com/",
        ["IndieGala"] = "https://www.indiegala.com/",
        ["2Game"] = "https://2game.com/",
        ["Voidu"] = "https://www.voidu.com/",
        ["Noctre"] = "https://www.noctre.com/",
        ["DreamGame"] = "https://www.dreamgame.com/",
        ["Blizzard Shop"] = "https://shop.battle.net/",
        ["AllYouPlay"] = "https://www.allyouplay.com/",
        ["Games Republic"] = "https://gamesrepublic.com/",
        ["DLGamer"] = "https://www.dlgamer.com/",
        ["GameStop"] = "https://www.gamestop.com/",
        ["Microsoft Store"] = "https://www.xbox.com/",
        ["Epic Games Store"] = "https://store.epicgames.com/",
        ["Humble Store"] = "https://www.humblebundle.com/store",
    };

    private static readonly string UserAgent =
        $"GamePriceHunter/{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0"} (Windows desktop price comparison)";

    private readonly SemaphoreSlim _storesLock = new(1, 1);
    private Dictionary<string, string>? _storeNames;

    public override ProviderInfo Info { get; } = new("cheapshark", "CheapShark", StoreKind.Official, "https://www.cheapshark.com/")
    {
        IsAggregator = true,
        Note = "Epic, Humble, GMG, IndieGala, WinGameStore and more. US prices in USD.",
    };

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        var stores = await GetStoresAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await GetJsonAsync($"{Api}/deals?title={Encode(context.Query)}&exact=0&pageSize=60&pageNumber=0", cancellationToken, Identify).ConfigureAwait(false);
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
                StoreUrl = StoreSites.GetValueOrDefault(storeName),
                RegularPrice = normal > sale ? Money.Of(normal, "USD") : null,
                ImageUrl = deal.Str("thumb"),
                Platform = storeName switch
                {
                    "Uplay" => Platform.Ubisoft,
                    "Blizzard Shop" => Platform.BattleNet,
                    "Epic Games Store" => Platform.Epic,
                    _ => Platform.Unknown,
                },
            });
        }
        return offers;
    }

    private async Task<IReadOnlyDictionary<string, string>> GetStoresAsync(CancellationToken cancellationToken)
    {
        await _storesLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_storeNames is not null)
                return _storeNames;

            using var doc = await GetJsonAsync($"{Api}/stores", cancellationToken, Identify).ConfigureAwait(false);
            var names = new Dictionary<string, string>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var store in doc.RootElement.EnumerateArray())
                {
                    if (store.Str("storeID") is { } id && store.Str("storeName") is { } storeName)
                        names[id] = storeName;
                }
            }
            return _storeNames = names;
        }
        finally
        {
            _storesLock.Release();
        }
    }

    // CheapShark asks API clients to identify themselves instead of posing as a browser.
    private static void Identify(HttpRequestMessage request)
    {
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd(UserAgent);
    }
}
