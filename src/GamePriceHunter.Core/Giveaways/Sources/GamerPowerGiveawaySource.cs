using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Giveaways.Sources;

/// <summary>
/// GamerPower (gamerpower.com/api-read): giveaways on sites without an API of their own, like IndieGala,
/// itch.io, STOVE and the key giveaways of Alienware Arena. Free to use with a link back to GamerPower,
/// which the app shows. The site gives no regions, and end dates without a time zone.
/// </summary>
public sealed partial class GamerPowerGiveawaySource(HttpClient http) : GiveawaySource(http)
{
    public const string SourceId = "gamerpower";
    public const string SiteUrl = "https://www.gamerpower.com/";

    /// <summary>Stores named in titles like "Last Room (IndieGala) Giveaway".</summary>
    private static readonly Dictionary<string, (string Name, string Url, Platform Platform)> TaggedStores = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Epic Games"] = ("Epic Games Store", "https://store.epicgames.com/", Platform.Epic),
        ["Epic Games Store"] = ("Epic Games Store", "https://store.epicgames.com/", Platform.Epic),
        ["Steam"] = ("Steam", "https://store.steampowered.com/", Platform.Steam),
        ["GOG"] = ("GOG", "https://www.gog.com/", Platform.Gog),
        ["IndieGala"] = ("IndieGala", "https://www.indiegala.com/", Platform.DrmFree),
        ["itch.io"] = ("itch.io", "https://itch.io/", Platform.DrmFree),
        ["Stove"] = ("STOVE", "https://store.onstove.com/", Platform.Other),
        ["Prime Gaming"] = ("Prime Gaming", "https://gaming.amazon.com/", Platform.Other),
        ["Ubisoft"] = ("Ubisoft Store", "https://store.ubisoft.com/", Platform.Ubisoft),
        ["Fanatical"] = ("Fanatical", "https://www.fanatical.com/", Platform.Unknown),
        ["Humble Bundle"] = ("Humble Store", "https://www.humblebundle.com/store", Platform.Unknown),
    };

    /// <summary>Sites that hand out keys, named in the instructions ("Log in to your free Alienware Arena account").</summary>
    private static readonly (string Marker, string Name, string Url)[] KeySites =
    [
        ("Alienware", "Alienware Arena", "https://www.alienwarearena.com/"),
        ("SteelSeries", "SteelSeries", "https://games.steelseries.com/"),
    ];

    public override ProviderInfo Info { get; } = new(SourceId, "GamerPower", StoreKind.Official, SiteUrl)
    {
        IsAggregator = true,
        Note = ProviderNote.GamerPower,
    };

    public override async Task<IReadOnlyList<Giveaway>> GetAsync(UserRegion region, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync("https://www.gamerpower.com/api/giveaways?platform=pc", cancellationToken).ConfigureAwait(false);
        return Parse(doc.RootElement);
    }

    internal IReadOnlyList<Giveaway> Parse(JsonElement root)
    {
        var giveaways = new List<Giveaway>();
        // With nothing to give away the API answers with a status object instead of a list.
        if (root.ValueKind != JsonValueKind.Array)
            return giveaways;

        foreach (var item in root.EnumerateArray())
        {
            var rawTitle = item.Str("title")?.Trim();
            var url = item.Str("open_giveaway_url") ?? item.Str("open_giveaway") ?? item.Str("gamerpower_url");
            if (string.IsNullOrEmpty(rawTitle) || url is null || !string.Equals(item.Str("status"), "Active", StringComparison.OrdinalIgnoreCase))
                continue;

            var (title, tag) = SplitTitle(rawTitle);
            var platform = PlatformOf(item.Str("platforms"), tag);
            var (storeName, storeUrl) = Host(rawTitle, tag, item.Str("instructions"));

            giveaways.Add(new Giveaway
            {
                SourceId = SourceId,
                Title = title,
                StoreName = storeName,
                StoreUrl = storeUrl,
                Url = url,
                ImageUrl = item.Str("image") ?? item.Str("thumbnail"),
                Kind = item.Str("type") switch
                {
                    "Game" => GiveawayKind.Game,
                    "Early Access" or "Beta" => GiveawayKind.Beta,
                    _ => GiveawayKind.Dlc,
                },
                Platform = platform,
                Worth = Worth(item.Str("worth")),
                EndsAt = Date(item.Str("end_date")),
                EndsOnDateOnly = true,
                PublishedAt = Date(item.Str("published_date")),
            });
        }
        return giveaways;
    }

    /// <summary>"Last Room (IndieGala) Giveaway" → ("Last Room", IndieGala); "Fathom Reach Playtest Steam Key Giveaway" → ("Fathom Reach Playtest", none).</summary>
    internal static (string Title, (string Name, string Url, Platform Platform)? Store) SplitTitle(string rawTitle)
    {
        var tagged = TaggedTitleRegex().Match(rawTitle);
        if (tagged.Success && TaggedStores.TryGetValue(tagged.Groups["tag"].Value.Trim(), out var store))
            return (tagged.Groups["title"].Value.Trim(), store);

        // A bracket that names no store is part of the name: "Zero King (Beta) Steam Key Giveaway".
        var title = GiveawaySuffixRegex().Replace(rawTitle, string.Empty).Trim();
        return (title.Length > 0 ? title : rawTitle, null);
    }

    private static Platform PlatformOf(string? platforms, (string Name, string Url, Platform Platform)? tag)
    {
        var list = platforms ?? string.Empty;
        if (list.Contains("Epic Games Store", StringComparison.OrdinalIgnoreCase))
            return Platform.Epic;
        if (list.Contains("Steam", StringComparison.OrdinalIgnoreCase))
            return Platform.Steam;
        if (list.Contains("GOG", StringComparison.OrdinalIgnoreCase))
            return Platform.Gog;
        if (list.Contains("DRM-Free", StringComparison.OrdinalIgnoreCase) || list.Contains("Itch.io", StringComparison.OrdinalIgnoreCase))
            return Platform.DrmFree;
        return tag?.Platform is { } platform and not Platform.Unknown ? platform : Platform.Other;
    }

    /// <summary>The site where the giveaway is claimed: a key site the instructions name, else the store in the title, else GamerPower.</summary>
    private static (string Name, string Url) Host(string rawTitle, (string Name, string Url, Platform Platform)? tag, string? instructions)
    {
        foreach (var (marker, name, url) in KeySites)
        {
            if (instructions?.Contains(marker, StringComparison.OrdinalIgnoreCase) == true)
                return (name, url);
        }
        // "(Steam) Key Giveaway" is a Steam key handed out somewhere else, not a Steam store giveaway.
        if (tag is { } store && !KeyRegex().IsMatch(rawTitle))
            return (store.Name, store.Url);
        return ("GamerPower", SiteUrl);
    }

    private static Money? Worth(string? text) =>
        text is not null && text.StartsWith('$')
        && decimal.TryParse(text[1..], NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) && amount > 0
            ? Money.Of(amount, "USD")
            : null;

    private static DateTimeOffset? Date(string? text) =>
        DateTimeOffset.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;

    [GeneratedRegex(@"^(?<title>.+?)\s*\((?<tag>[^()]+)\)\s*(?:Steam\s+)?(?:Keys?\s+)?Giveaway\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TaggedTitleRegex();

    [GeneratedRegex(@"\s+(?:Steam\s+)?(?:Keys?\s+)?Giveaway\s*$|\s+(?:Steam\s+)?Keys?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex GiveawaySuffixRegex();

    [GeneratedRegex(@"\bKeys?\b", RegexOptions.IgnoreCase)]
    private static partial Regex KeyRegex();
}
