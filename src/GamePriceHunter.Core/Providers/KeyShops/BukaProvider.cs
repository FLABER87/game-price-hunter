using System.Text.Json;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Providers.KeyShops;

/// <summary>Buka: Russian publisher and authorized distributor; PC games area of its store API.</summary>
public sealed class BukaProvider(HttpClient http) : StoreProvider(http)
{
    private const string Host = "https://shop.buka.ru";
    private const int WindowsGamesArea = 2002;

    public override ProviderInfo Info { get; } = new("buka", "Buka", StoreKind.Official, "https://shop.buka.ru/") { Note = RegionalNotes.Russia };

    public override async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            pageIndex = 0,
            filter = new { term = context.Query, area_id = WindowsGamesArea, channel = "WEB" },
        });
        using var doc = await PostJsonAsync($"{Host}/s/f/v3/search/get-page", body, cancellationToken).ConfigureAwait(false);
        return Parse(doc.RootElement);
    }

    internal IReadOnlyList<Offer> Parse(JsonElement root)
    {
        var offers = new List<Offer>();
        foreach (var row in root.Arr("page.rows"))
        {
            var title = row.Str("title");
            var alias = row.Str("alias");
            var price = row.Dec("price.actual");
            if (title is null || alias is null || price is not > 0 || row.Str("saleState") is not ("available" or "pre-order"))
                continue;

            var old = row.Dec("price.old");
            var activation = row.Arr("filters")
                .FirstOrDefault(f => f.Str("field") == "activation_system")
                .Str("values.0.title");

            offers.Add(CreateOffer(title.Replace(" (PC)", "", StringComparison.Ordinal), $"{Host}/item/{alias}", Money.Of(price.Value, "RUB")) with
            {
                RegularPrice = old > price ? Money.Of(old.Value, "RUB") : null,
                ImageUrl = row.Str("images.main.resized.296x") ?? row.Str("images.main.resized.x"),
                Platform = OfferClassifier.ParsePlatform(activation),
                Region = "RU",
            });
        }
        return offers;
    }
}
