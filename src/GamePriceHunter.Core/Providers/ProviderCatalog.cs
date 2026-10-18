using GamePriceHunter.Core.Providers.Aggregators;
using GamePriceHunter.Core.Providers.KeyShops;
using GamePriceHunter.Core.Providers.Marketplaces;
using GamePriceHunter.Core.Providers.Official;

namespace GamePriceHunter.Core.Providers;

/// <summary>All built-in providers, in the order they are listed in the UI.</summary>
public static class ProviderCatalog
{
    public static IReadOnlyList<IStoreProvider> CreateAll(HttpClient http) =>
    [
        // Official stores and aggregators of official stores
        new SteamProvider(http),
        new GogProvider(http),
        new FanaticalProvider(http),
        new CheapSharkProvider(http),
        new IsThereAnyDealProvider(http),
        new BukaProvider(http),
        new GamersGateProvider(http),
        new TwoGameProvider(http),
        new VoiduProvider(http),
        new YuplayProvider(http),
        new GamesplanetProvider(http),

        // Key shops (Russia / CIS)
        new SteamBuyProvider(http),
        new GabeStoreProvider(http),
        new ZakaZakaProvider(http),
        new SteamPayProvider(http),
        new IgroMagazProvider(http),
        new GamazavrProvider(http),
        new SteamkeyProvider(http),
        new IgroShopProvider(http),

        // Marketplaces and key shops (global)
        new PlatiProvider(http),
        new GgselProvider(http),
        new K4GProvider(http),
        new EnebaProvider(http),
        new DriffleProvider(http),
        new DifmarkProvider(http),
        new InstantGamingProvider(http),
        new MmogaProvider(http),
    ];
}
