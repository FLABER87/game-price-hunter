namespace GamePriceHunter.Core.Models;

/// <summary>What kind of seller a store is. Drives badges and filters in the UI.</summary>
public enum StoreKind
{
    /// <summary>Publisher-authorized digital store (Steam, GOG, Humble, Fanatical...).</summary>
    Official,

    /// <summary>Shop that resells keys it buys in bulk (Instant Gaming, Zaka-Zaka...).</summary>
    KeyShop,

    /// <summary>Marketplace where third-party sellers list keys (Plati, Eneba, K4G...).</summary>
    Marketplace,
}

public enum OfferType
{
    Unknown,
    Game,
    Dlc,
    Bundle,
    Other,
}

/// <summary>Where the purchased key or game gets activated.</summary>
public enum Platform
{
    Unknown,
    Steam,
    Gog,
    Epic,
    Ubisoft,
    Ea,
    BattleNet,
    Rockstar,
    Xbox,
    PlayStation,
    Nintendo,
    DrmFree,
    Other,
}

/// <summary>Whether a key's region restriction is expected to work for the user's region.</summary>
public enum RegionCompatibility
{
    Unknown,
    Compatible,
    Incompatible,
}

public enum ProviderStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Blocked,
    TimedOut,
    Cancelled,
    Skipped,
}
