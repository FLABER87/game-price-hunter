using System.Text.Json;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.Marketplaces;

/// <summary>
/// Eneba marketplace. The server-rendered store page embeds the Algolia search response
/// (__ALGOLIA_STATE__) with the lowest offer per product in minor units of the requested currency.
/// </summary>
public sealed class EnebaProvider(HttpClient http) : StoreProvider(http)
{
    private const string Host = "https://www.eneba.com";
    private const string StateId = "__ALGOLIA_STATE__";

    public override ProviderInfo Info { get; } = new("eneba", "Eneba", StoreKind.Marketplace, "https://www.eneba.com/")
    {
        Note = ProviderNote.Marketplace,
    };

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        var currency = context.Region.CurrencyCode;
        var url = $"{Host}/store/all?text={Encode(context.Query)}&page=1&currency={Encode(currency)}";
        var html = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        if (Html.ScriptById(html, StateId) is null)
        {
            // Now and then a 200 page comes without the search state (not a challenge page);
            // the next request is fine.
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            html = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        }
        return Parse(html, currency);
    }

    internal IReadOnlyList<Offer> Parse(string html, string preferredCurrency)
    {
        var state = Html.ScriptById(html, StateId);
        if (state is null)
        {
            // Name the page that came instead, so the log tells what happened.
            var title = Html.Decode(Html.Match(html, "<title[^>]*>(.*?)</title>"));
            throw new InvalidDataException($"Eneba search results not found in the page (title \"{title}\", {html.Length} chars).");
        }
        using var doc = JsonDocument.Parse(state);

        var offers = new List<Offer>();
        foreach (var response in doc.RootElement.Obj("responses"))
        {
            foreach (var hit in response.Value.Arr("results.0.hits"))
            {
                var title = hit.Str("translations.en_US.name") ?? hit.Str("translations.en.name");
                var slug = hit.Str("slug");
                if (string.IsNullOrWhiteSpace(title) || slug is null || hit.Bool("stockAvailable") == false)
                    continue;

                var currency = hit.Path($"lowestPrice.{preferredCurrency}") is not null ? preferredCurrency
                    : hit.Path("lowestPrice.EUR") is not null ? "EUR"
                    : hit.Obj("lowestPrice").Select(p => p.Name).FirstOrDefault();
                var minor = currency is null ? null : hit.Long($"lowestPrice.{currency}");
                if (currency is null || minor is not > 0)
                    continue;

                var price = minor.Value / 100m;
                var before = (hit.Long($"lowestPriceBeforeDiscount.{currency}") ?? hit.Long($"msrp.{currency}")) / 100m;
                offers.Add(CreateOffer(title.Trim(), $"{Host}/{slug}", Money.Of(price, currency)) with
                {
                    RegularPrice = before > price ? Money.Of(before.Value, currency) : null,
                    ImageUrl = hit.Str("images.cover300.src"),
                    Platform = OfferClassifier.ParsePlatform(hit.Str("drmName")),
                    Region = RegionLabel(hit),
                    Type = hit.Str("productType") switch
                    {
                        "dlc" => OfferType.Dlc,
                        "game" or null => OfferType.Unknown,
                        // Gift cards, subscriptions, in-game currency, software.
                        _ => OfferType.Other,
                    },
                });
            }
        }
        return offers;
    }

    /// <summary>["europe"] → "europe", ["united_states"] → "united states" (RegionTags parses these labels).</summary>
    private static string? RegionLabel(JsonElement hit)
    {
        var regions = hit.Arr("productRegions")
            .Where(r => r.ValueKind == JsonValueKind.String)
            .Select(r => r.GetString()!.Replace('_', ' '))
            .Where(r => r.Length > 0)
            .ToList();
        return regions.Count == 0 ? null : string.Join(", ", regions);
    }
}
