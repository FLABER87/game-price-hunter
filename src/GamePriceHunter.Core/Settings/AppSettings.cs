using System.Text.Json.Serialization;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Settings;

public enum AppTheme
{
    System,
    Dark,
    Light,
}

public enum SortMode
{
    BestMatch,
    PriceLowToHigh,
    PriceHighToLow,
    Discount,
    Store,
    Title,
}

/// <summary>Which offers the results list shows.</summary>
public sealed class ResultFilters
{
    public bool ShowOfficial { get; set; } = true;
    public bool ShowKeyShops { get; set; } = true;
    public bool ShowMarketplaces { get; set; } = true;
    public bool ShowGames { get; set; } = true;
    public bool ShowDlc { get; set; } = true;
    public bool ShowBundles { get; set; } = true;
    public bool ShowOther { get; set; }

    /// <summary>Xbox, PlayStation and Nintendo keys; off by default since most searches are for PC.</summary>
    public bool ShowConsoles { get; set; }

    public bool HideIncompatibleRegions { get; set; }
}

/// <summary>Which giveaways the giveaway list shows.</summary>
public sealed class GiveawayFilters
{
    public bool ShowGames { get; set; } = true;

    /// <summary>DLC and in-game items.</summary>
    public bool ShowDlc { get; set; }

    public bool ShowBetas { get; set; }
}

public sealed class WindowBounds
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsMaximized { get; set; }
}

/// <summary>User settings persisted as JSON.</summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>UI culture name ("en", "pt-BR"); empty means "follow Windows".</summary>
    public string Language { get; set; } = string.Empty;

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>ISO country code chosen by the user; null means detect automatically.</summary>
    public string? CountryOverride { get; set; }

    /// <summary>Display currency chosen by the user; null means the region's currency.</summary>
    public string? CurrencyOverride { get; set; }

    /// <summary>Default-on providers the user switched off.</summary>
    public List<string> DisabledProviders { get; set; } = [];

    /// <summary>Default-off providers the user switched on.</summary>
    public List<string> EnabledProviders { get; set; } = [];

    public int ProviderTimeoutSeconds { get; set; } = 20;

    public MatchMode MatchMode { get; set; } = MatchMode.AllWords;

    public int MaxResultsPerProvider { get; set; } = 60;

    public SortMode SortMode { get; set; } = SortMode.BestMatch;

    public ResultFilters Filters { get; set; } = new();

    public GiveawayFilters GiveawayFilters { get; set; } = new();

    /// <summary>Provider API keys, encrypted for the current Windows user.</summary>
    public Dictionary<string, string> ProtectedApiKeys { get; set; } = [];

    public List<string> SearchHistory { get; set; } = [];

    public WindowBounds? Window { get; set; }

    /// <summary>Look for a new version on every start.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Tag of the newest release seen by the last check ("v1.2.0"), so an update offer survives restarts.</summary>
    public string? LatestReleaseTag { get; set; }

    /// <summary>Brings settings saved by an older version up to date.</summary>
    public void Upgrade()
    {
        // Version 1 had English only and saved it for everyone, so "en" was nobody's choice.
        if (Version < 2 && string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase))
            Language = string.Empty;
        Version = CurrentVersion;
    }

    public bool IsProviderEnabled(ProviderInfo provider) =>
        provider.EnabledByDefault
            ? !DisabledProviders.Contains(provider.Id, StringComparer.OrdinalIgnoreCase)
            : EnabledProviders.Contains(provider.Id, StringComparer.OrdinalIgnoreCase);

    public void SetProviderEnabled(ProviderInfo provider, bool enabled)
    {
        // Only deviations from the provider's default are stored, so new providers pick up their default.
        var list = provider.EnabledByDefault ? DisabledProviders : EnabledProviders;
        list.RemoveAll(id => string.Equals(id, provider.Id, StringComparison.OrdinalIgnoreCase));
        if (enabled != provider.EnabledByDefault)
            list.Add(provider.Id);
    }

    [JsonIgnore]
    public TimeSpan ProviderTimeout => TimeSpan.FromSeconds(Math.Clamp(ProviderTimeoutSeconds, 5, 120));

    public string? GetApiKey(string providerId) =>
        ProtectedApiKeys.TryGetValue(providerId, out var value) ? SecretProtector.Unprotect(value) : null;

    public void SetApiKey(string providerId, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            ProtectedApiKeys.Remove(providerId);
        else
            ProtectedApiKeys[providerId] = SecretProtector.Protect(key.Trim());
    }

    public void AddToHistory(string query, int limit = 20)
    {
        query = query.Trim();
        if (query.Length == 0)
            return;
        SearchHistory.RemoveAll(q => string.Equals(q, query, StringComparison.OrdinalIgnoreCase));
        SearchHistory.Insert(0, query);
        if (SearchHistory.Count > limit)
            SearchHistory.RemoveRange(limit, SearchHistory.Count - limit);
    }
}
