using System.Text.Json;
using GamePriceHunter.Core.Currency;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Search;

public sealed record GameSuggestion(string Title, string? ImageUrl, string? PriceText);

/// <summary>Type-ahead game titles from the Steam store search API.</summary>
public sealed class SuggestionService(HttpClient http)
{
    public async Task<IReadOnlyList<GameSuggestion>> GetAsync(string query, UserRegion region, CancellationToken cancellationToken)
    {
        var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(query)}&l=english&cc={region.CountryCode}";
        await using var stream = await http.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Parse(doc.RootElement);
    }

    internal static IReadOnlyList<GameSuggestion> Parse(JsonElement root)
    {
        var suggestions = new List<GameSuggestion>();
        foreach (var item in root.Arr("items"))
        {
            if (item.Str("name") is not { Length: > 0 } name)
                continue;

            string? price = null;
            if (item.Dec("price.final") is { } final && item.Str("price.currency") is { } currency)
                price = final == 0 ? null : MoneyFormatter.Format(Money.Of(final / 100m, currency));

            suggestions.Add(new GameSuggestion(name, item.Str("tiny_image"), price));
        }
        return suggestions;
    }
}
