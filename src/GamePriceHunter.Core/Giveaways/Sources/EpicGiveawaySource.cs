using System.Globalization;
using System.Text.Json;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Giveaways.Sources;

/// <summary>
/// The free games of Epic Games Store, this week's and next week's, as the store's own "Free games"
/// section lists them for the user's country. Served from Epic's CDN, which, unlike the store pages,
/// answers .NET clients.
/// </summary>
public sealed class EpicGiveawaySource(HttpClient http) : GiveawaySource(http)
{
    public const string SourceId = "epic";
    private const string StoreUrl = "https://store.epicgames.com/";

    public override ProviderInfo Info { get; } = new(SourceId, "Epic Games Store", StoreKind.Official, StoreUrl);

    public override async Task<IReadOnlyList<Giveaway>> GetAsync(UserRegion region, CancellationToken cancellationToken)
    {
        var country = region.CountryCode;
        using var doc = await GetJsonAsync(
            $"https://store-site-backend-static-ipv4.ak.epicgames.com/freeGamesPromotions?locale=en-US&country={country}&allowCountries={country}",
            cancellationToken).ConfigureAwait(false);
        return Parse(doc.RootElement);
    }

    internal IReadOnlyList<Giveaway> Parse(JsonElement root)
    {
        var giveaways = new List<Giveaway>();
        foreach (var element in root.Arr("data.Catalog.searchStore.elements"))
        {
            var title = element.Str("title")?.Trim();
            if (string.IsNullOrEmpty(title))
                continue;

            // The section also lists ordinary discounts; Epic gives the share of the price left to pay, so 0 is free.
            var promotions = element.Arr("promotions.promotionalOffers")
                .Concat(element.Arr("promotions.upcomingPromotionalOffers"))
                .SelectMany(group => group.Arr("promotionalOffers"))
                .Where(offer => offer.Dec("discountSetting.discountPercentage") == 0);

            foreach (var promotion in promotions)
            {
                var starts = Date(promotion.Str("startDate"));
                var ends = Date(promotion.Str("endDate"));
                if (starts is null || ends is null)
                    continue;

                giveaways.Add(new Giveaway
                {
                    SourceId = SourceId,
                    Title = title,
                    StoreName = Info.Name,
                    StoreUrl = StoreUrl,
                    Url = PageUrl(element),
                    ImageUrl = Image(element),
                    Kind = element.Str("offerType") is "ADD_ON" or "DLC" ? GiveawayKind.Dlc : GiveawayKind.Game,
                    Platform = Platform.Epic,
                    Worth = Worth(element),
                    StartsAt = starts,
                    EndsAt = ends,
                    IsRegionChecked = true,
                });
            }
        }
        return giveaways;
    }

    /// <summary>
    /// The offer's own page. An add-on has one under its offer mapping, while its catalog mapping points at
    /// the base game, so the offer mappings come first.
    /// </summary>
    internal static string PageUrl(JsonElement element)
    {
        var slug = element.Arr("offerMappings").Where(m => m.Str("pageType") is "productHome" or "offer")
                       .Concat(element.Arr("catalogNs.mappings").Where(m => m.Str("pageType") == "productHome"))
                       .Select(m => m.Str("pageSlug"))
                       .FirstOrDefault(s => !string.IsNullOrEmpty(s))
                   ?? element.Str("productSlug")?.Replace("/home", "", StringComparison.Ordinal);
        if (string.IsNullOrEmpty(slug))
            return StoreUrl + "free-games";
        return StoreUrl + (element.Str("offerType") == "BUNDLE" ? "bundles/" : "p/") + Uri.EscapeDataString(slug);
    }

    private static string? Image(JsonElement element)
    {
        var images = element.Arr("keyImages").ToList();
        foreach (var type in new[] { "OfferImageWide", "DieselStoreFrontWide", "Thumbnail" })
        {
            if (images.FirstOrDefault(i => i.Str("type") == type).Str("url") is { Length: > 0 } url)
                return url;
        }
        return images.Select(i => i.Str("url")).FirstOrDefault(u => !string.IsNullOrEmpty(u));
    }

    private static Money? Worth(JsonElement element)
    {
        var minor = element.Dec("price.totalPrice.originalPrice");
        var currency = element.Str("price.totalPrice.currencyCode");
        var decimals = element.Long("price.totalPrice.currencyInfo.decimals") ?? 2;
        if (minor is not > 0 || string.IsNullOrEmpty(currency) || decimals is < 0 or > 4)
            return null;
        return Money.Of(minor.Value / (decimal)Math.Pow(10, decimals), currency);
    }

    private static DateTimeOffset? Date(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
}
