using System.Text.Json;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.Aggregators;

/// <summary>
/// IsThereAnyDeal API v2: regional prices from 50+ authorized stores. Needs a free API key
/// (isthereanydeal.com/apps/my/), so it is off until the user adds one.
/// </summary>
public sealed class IsThereAnyDealProvider(HttpClient http) : StoreProvider(http)
{
    public const string ProviderId = "itad";
    private const string Api = "https://api.isthereanydeal.com";

    /// <summary>
    /// ITAD shop ids of stores that have direct regional providers: Steam, GOG, Fanatical,
    /// GamersGate and the four GamesPlanet shops (UK, DE, FR, US).
    /// </summary>
    private static readonly HashSet<long> CoveredShops = [61, 35, 6, 24, 26, 27, 28, 29];

    public override ProviderInfo Info { get; } = new(ProviderId, "IsThereAnyDeal", StoreKind.Official, "https://isthereanydeal.com/")
    {
        IsAggregator = true,
        RequiresApiKey = true,
        EnabledByDefault = false,
        Note = ProviderNote.IsThereAnyDeal,
    };

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        if (!context.ApiKeys.TryGetValue(ProviderId, out var key) || string.IsNullOrWhiteSpace(key))
            throw new ProviderSkippedException("Add an IsThereAnyDeal API key in Settings to use this source.");

        using var search = await GetJsonAsync($"{Api}/games/search/v1?title={Encode(context.Query)}&results=20&key={Encode(key)}", cancellationToken).ConfigureAwait(false);
        var matcher = new TitleMatcher(context.Query);
        var games = new Dictionary<string, (string Title, string? Image, string? Type)>();
        if (search.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var game in search.RootElement.EnumerateArray())
            {
                if (game.Str("id") is { } id && game.Str("title") is { } title && matcher.Score(title) > 0)
                    games[id] = (title, game.Str("assets.banner400") ?? game.Str("assets.banner300"), game.Str("type"));
            }
        }
        if (games.Count == 0)
            return [];

        var body = JsonSerializer.Serialize(games.Keys);
        using var prices = await PostJsonAsync(
            $"{Api}/games/prices/v3?key={Encode(key)}&country={context.Region.CountryCode}&deals=false&vouchers=false&capacity=0",
            body,
            cancellationToken).ConfigureAwait(false);
        return Parse(prices.RootElement, games);
    }

    internal IReadOnlyList<Offer> Parse(JsonElement root, IReadOnlyDictionary<string, (string Title, string? Image, string? Type)> games)
    {
        var offers = new List<Offer>();
        if (root.ValueKind != JsonValueKind.Array)
            return offers;

        foreach (var entry in root.EnumerateArray())
        {
            if (entry.Str("id") is not { } id || !games.TryGetValue(id, out var game))
                continue;

            foreach (var deal in entry.Arr("deals"))
            {
                var shopId = deal.Long("shop.id");
                var shopName = deal.Str("shop.name");
                var url = deal.Str("url");
                var amount = deal.Dec("price.amount");
                var currency = deal.Str("price.currency");
                if (shopId is null || CoveredShops.Contains(shopId.Value) || shopName is null || url is null || amount is null || currency is null)
                    continue;

                var regular = deal.Dec("regular.amount");
                offers.Add(CreateOffer(game.Title, url, Money.Of(amount.Value, currency)) with
                {
                    StoreName = shopName,
                    // Deal links go through ITAD's redirector, so its host says nothing about the store.
                    StoreUrl = Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.Host.Contains("itad", StringComparison.OrdinalIgnoreCase)
                        ? $"{uri.Scheme}://{uri.Host}/"
                        : null,
                    RegularPrice = regular > amount ? Money.Of(regular.Value, currency) : null,
                    ImageUrl = game.Image,
                    Type = game.Type switch
                    {
                        "game" => OfferType.Game,
                        "dlc" => OfferType.Dlc,
                        "package" => OfferType.Bundle,
                        _ => OfferType.Unknown,
                    },
                    Platform = OfferClassifier.ParsePlatform(deal.Str("drm.0.name")),
                });
            }
        }
        return offers;
    }
}
