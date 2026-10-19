using System.Text.Json;
using System.Text.RegularExpressions;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Providers.Official;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Giveaways.Sources;

/// <summary>
/// Steam's "free to keep" promotions: the store search finds the specials that cost nothing, and
/// IStoreBrowseService/GetItems confirms each one and tells when it ends.
/// </summary>
public sealed partial class SteamGiveawaySource(HttpClient http) : GiveawaySource(http)
{
    public const string SourceId = "steam";
    private const string Host = "https://store.steampowered.com";
    private const string AssetHost = "https://shared.akamai.steamstatic.com/store_item_assets/";

    public override ProviderInfo Info { get; } = new(SourceId, "Steam", StoreKind.Official, Host + "/");

    public override async Task<IReadOnlyList<Giveaway>> GetAsync(UserRegion region, CancellationToken cancellationToken)
    {
        var cc = region.CountryCode;
        using var search = await GetJsonAsync(
            $"{Host}/search/results/?query&start=0&count=50&maxprice=free&specials=1&infinite=1&cc={cc}&l=english",
            cancellationToken).ConfigureAwait(false);
        var ids = ParseSearchIds(search.RootElement.Str("results_html") ?? string.Empty);
        if (ids.Count == 0)
            return [];

        var input = JsonSerializer.Serialize(new
        {
            ids = ids.Select(id => new { appid = id }),
            context = new { language = "english", country_code = cc },
            data_request = new { include_assets = true },
        });
        using var items = await GetJsonAsync(
            $"https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json={Encode(input)}",
            cancellationToken).ConfigureAwait(false);
        return ParseItems(items.RootElement, region.CurrencyCode);
    }

    /// <summary>App ids of the search rows; rows of packages and bundles name several apps and are left out.</summary>
    internal static IReadOnlyList<long> ParseSearchIds(string html) =>
        AppIdRegex().Matches(html).Select(m => long.Parse(m.Groups[1].Value)).Distinct().ToList();

    internal IReadOnlyList<Giveaway> ParseItems(JsonElement root, string regionCurrency)
    {
        var giveaways = new List<Giveaway>();
        foreach (var item in root.Arr("response.store_items"))
        {
            var id = item.Long("id");
            var name = item.Str("name")?.Trim();
            var option = item.Path("best_purchase_option");
            if (id is null || string.IsNullOrEmpty(name) || option is not { } purchase || purchase.Bool("is_free_to_keep") != true)
                continue;

            Money? worth = null;
            var originalText = purchase.Str("formatted_original_price") ?? string.Empty;
            if (purchase.Dec("original_price_in_cents") is > 0 and var cents
                && SteamProvider.CurrencyFromText(originalText, regionCurrency) is { } currency)
                worth = Money.Of(cents / 100m, currency);

            giveaways.Add(new Giveaway
            {
                SourceId = SourceId,
                Title = name,
                StoreName = Info.Name,
                StoreUrl = Info.HomeUrl,
                Url = $"{Host}/app/{id}/",
                ImageUrl = Image(item),
                // Steam's app type 4 is DLC.
                Kind = item.Long("type") == 4 ? GiveawayKind.Dlc : GiveawayKind.Game,
                Platform = Platform.Steam,
                Worth = worth,
                EndsAt = purchase.Long("free_to_keep_ends") is > 0 and var ends ? DateTimeOffset.FromUnixTimeSeconds(ends) : null,
                IsRegionChecked = true,
            });
        }
        return giveaways;
    }

    private static string? Image(JsonElement item)
    {
        var format = item.Str("assets.asset_url_format");
        var file = item.Str("assets.main_capsule") ?? item.Str("assets.header");
        return format is null || file is null ? null : AssetHost + format.Replace("${FILENAME}", file, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"data-ds-appid=""(\d+)""")]
    private static partial Regex AppIdRegex();
}
