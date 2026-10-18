using System.Collections.Concurrent;
using System.Text.Json;
using GamePriceHunter.Core.Diagnostics;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Search;

public sealed record GameSuggestion(string Title, string? ImageUrl, Money? Price, bool IsFree = false);

/// <summary>What Steam says about an app the store search lists without a price.</summary>
internal enum PricelessApp
{
    Unknown,
    Free,
    ComingSoon,
}

/// <summary>Type-ahead game titles from the Steam store search API.</summary>
public sealed class SuggestionService(HttpClient http, IAppLogger logger)
{
    private static readonly TimeSpan LookupBudget = TimeSpan.FromSeconds(3);

    // Free and not-yet-released apps look the same in the store search (no price). The answer
    // rarely changes, so each app is asked about once per run.
    private readonly ConcurrentDictionary<long, PricelessApp> _priceless = new();

    public async Task<IReadOnlyList<GameSuggestion>> GetAsync(string query, UserRegion region, CancellationToken cancellationToken)
    {
        var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(query)}&l=english&cc={region.CountryCode}";
        IReadOnlyList<SearchHit> hits;
        await using (var stream = await http.GetStreamAsync(url, cancellationToken).ConfigureAwait(false))
        using (var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false))
            hits = Parse(doc.RootElement);

        var unknown = hits.Where(h => NeedsLookup(h) && !_priceless.ContainsKey(h.AppId)).Select(h => h.AppId).ToList();
        if (unknown.Count > 0)
            await LookUpAsync(unknown, region, cancellationToken).ConfigureAwait(false);
        return Resolve(hits, _priceless);
    }

    /// <summary>Asks Steam which of the apps are free and which are not out yet; on failure they stay as they are.</summary>
    private async Task LookUpAsync(IReadOnlyList<long> appIds, UserRegion region, CancellationToken cancellationToken)
    {
        var ids = string.Join(",", appIds.Select(id => $"{{\"appid\":{id}}}"));
        var input = $"{{\"ids\":[{ids}],\"context\":{{\"country_code\":\"{region.CountryCode.ToUpperInvariant()}\"}}}}";
        var url = $"https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json={Uri.EscapeDataString(input)}";

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(LookupBudget);
        try
        {
            await using var stream = await http.GetStreamAsync(url, budget.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: budget.Token).ConfigureAwait(false);
            // Apps Steam could not describe this time are left out, so the next search asks again.
            foreach (var (id, state) in ParseItems(doc.RootElement))
                _priceless[id] = state;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.Warn($"Steam app details for suggestions failed: {ex.Message}");
        }
    }

    internal sealed record SearchHit(long AppId, GameSuggestion Suggestion);

    private static bool NeedsLookup(SearchHit hit) => hit.AppId > 0 && hit.Suggestion is { Price: null, IsFree: false };

    internal static IReadOnlyList<SearchHit> Parse(JsonElement root)
    {
        var hits = new List<SearchHit>();
        foreach (var item in root.Arr("items"))
        {
            if (item.Str("name") is not { Length: > 0 } name)
                continue;

            Money? price = null;
            var isFree = false;
            if (item.Dec("price.final") is { } final && item.Str("price.currency") is { } currency)
            {
                if (final == 0)
                    isFree = true;
                else
                    price = Money.Of(final / 100m, currency);
            }

            hits.Add(new SearchHit(item.Long("id") ?? 0, new GameSuggestion(name, item.Str("tiny_image"), price, isFree)));
        }
        return hits;
    }

    /// <summary>Reads an IStoreBrowseService/GetItems response: the apps Steam described.</summary>
    internal static Dictionary<long, PricelessApp> ParseItems(JsonElement root)
    {
        var states = new Dictionary<long, PricelessApp>();
        foreach (var item in root.Arr("response.store_items"))
        {
            if (item.Long("success") != 1 || item.Long("appid") is not { } id)
                continue;
            states[id] = item.Bool("is_coming_soon") == true || item.Bool("release.is_coming_soon") == true ? PricelessApp.ComingSoon
                : item.Bool("is_free") == true ? PricelessApp.Free
                : PricelessApp.Unknown;
        }
        return states;
    }

    /// <summary>Drops apps that are not out yet and marks free ones; apps with a price are left alone.</summary>
    internal static IReadOnlyList<GameSuggestion> Resolve(IEnumerable<SearchHit> hits, IReadOnlyDictionary<long, PricelessApp> priceless)
    {
        var suggestions = new List<GameSuggestion>();
        foreach (var hit in hits)
        {
            var state = NeedsLookup(hit) ? priceless.GetValueOrDefault(hit.AppId) : PricelessApp.Unknown;
            if (state == PricelessApp.ComingSoon)
                continue;
            suggestions.Add(state == PricelessApp.Free ? hit.Suggestion with { IsFree = true } : hit.Suggestion);
        }
        return suggestions;
    }
}
