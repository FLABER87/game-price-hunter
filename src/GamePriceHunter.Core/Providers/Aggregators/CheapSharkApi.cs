using System.Reflection;
using System.Text.Json;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.Aggregators;

/// <summary>What the CheapShark price provider and giveaway source share: the API, the client identity and the store list.</summary>
internal static class CheapSharkApi
{
    public const string Url = "https://www.cheapshark.com/api/1.0";

    public static readonly IReadOnlyDictionary<string, string> StoreSites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Steam"] = "https://store.steampowered.com/",
        ["GOG"] = "https://www.gog.com/",
        ["Fanatical"] = "https://www.fanatical.com/",
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

    private static readonly SemaphoreSlim StoresLock = new(1, 1);
    private static IReadOnlyDictionary<string, string>? _storeNames;

    // CheapShark asks API clients to identify themselves instead of posing as a browser.
    public static void Identify(HttpRequestMessage request)
    {
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd(UserAgent);
    }

    public static Platform PlatformOf(string storeName) => storeName switch
    {
        "Steam" => Platform.Steam,
        "GOG" => Platform.Gog,
        "Uplay" => Platform.Ubisoft,
        "Blizzard Shop" => Platform.BattleNet,
        "Epic Games Store" => Platform.Epic,
        _ => Platform.Unknown,
    };

    /// <summary>Store names by CheapShark store id, loaded once per run with <paramref name="load"/>.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> GetStoreNamesAsync(
        Func<CancellationToken, Task<JsonDocument>> load,
        CancellationToken cancellationToken)
    {
        await StoresLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_storeNames is not null)
                return _storeNames;

            using var doc = await load(cancellationToken).ConfigureAwait(false);
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
            StoresLock.Release();
        }
    }
}
