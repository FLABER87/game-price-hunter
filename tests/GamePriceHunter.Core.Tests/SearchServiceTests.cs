using GamePriceHunter.Core.Diagnostics;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Providers;
using GamePriceHunter.Core.Search;
using GamePriceHunter.Core.Settings;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Tests;

public class SearchServiceTests
{
    private static readonly SearchContext Context = new("hollow knight", new UserRegion("SK", "EUR"));

    private static Offer MakeOffer(string title, decimal price, StoreKind kind = StoreKind.Official, string url = "https://example.com/a") => new()
    {
        ProviderId = "test",
        StoreName = "Test",
        StoreKind = kind,
        Title = title,
        Url = url,
        Price = Money.Of(price, "EUR"),
    };

    [Fact]
    public void Refine_filters_irrelevant_and_free_extras()
    {
        Offer[] raw =
        [
            MakeOffer("Hollow Knight", 14.79m, url: "https://example.com/1"),
            MakeOffer("Hollow Knight - Official Soundtrack", 0m, url: "https://example.com/2"),
            MakeOffer("Hollow Knight", 0m, StoreKind.Marketplace, "https://example.com/3"),
            MakeOffer("Celeste", 4.99m, url: "https://example.com/4"),
            MakeOffer("Hollow Knight", 9.99m, url: "javascript:alert(1)"),
        ];

        var refined = SearchService.Refine(raw, new TitleMatcher(Context.Query), Context);

        var offer = Assert.Single(refined);
        Assert.Equal("https://example.com/1", offer.Url);
        Assert.True(offer.Relevance >= 0.95);
    }

    [Fact]
    public void Refine_keeps_the_cheapest_duplicate_and_classifies()
    {
        Offer[] raw =
        [
            MakeOffer("Hollow Knight (PC) Steam Key GLOBAL", 9m, StoreKind.Marketplace),
            MakeOffer("Hollow Knight (PC) Steam Key GLOBAL", 7m, StoreKind.Marketplace),
        ];

        var offer = Assert.Single(SearchService.Refine(raw, new TitleMatcher(Context.Query), Context));
        Assert.Equal(7m, offer.Price.Amount);
        Assert.Equal(Platform.Steam, offer.Platform);
        Assert.Equal("Global", offer.Region);
        Assert.Equal(RegionCompatibility.Compatible, offer.RegionCompatibility);
    }

    [Fact]
    public async Task SearchAsync_reports_each_provider_status()
    {
        IStoreProvider[] providers = [new FakeProvider("ok", [MakeOffer("Hollow Knight", 10m)]), new FakeProvider("blocked", null)];
        var service = new SearchService(providers, NullLogger.Instance);
        var options = new SearchOptions { Providers = new HashSet<string> { "ok", "blocked" } };

        var results = new List<ProviderSearchResult>();
        await foreach (var result in service.SearchAsync("hollow knight", Context.Region, options))
            results.Add(result);

        Assert.Equal(ProviderStatus.Succeeded, results.Single(r => r.Provider.Id == "ok").Status);
        Assert.Equal(ProviderStatus.Blocked, results.Single(r => r.Provider.Id == "blocked").Status);
    }

    [Fact]
    public async Task SearchAsync_times_out_slow_providers()
    {
        var service = new SearchService([new FakeProvider("slow", [], TimeSpan.FromSeconds(10))], NullLogger.Instance);
        var options = new SearchOptions { Providers = new HashSet<string> { "slow" }, ProviderTimeout = TimeSpan.FromMilliseconds(100) };

        await foreach (var result in service.SearchAsync("hollow knight", Context.Region, options))
            Assert.Equal(ProviderStatus.TimedOut, result.Status);
    }

    private sealed class FakeProvider(string id, IReadOnlyList<Offer>? offers, TimeSpan delay = default) : IStoreProvider
    {
        public ProviderInfo Info { get; } = new(id, id, StoreKind.Official, "https://example.com/");

        public async Task<IReadOnlyList<Offer>> SearchAsync(SearchContext context, CancellationToken cancellationToken)
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);
            return offers ?? throw new ProviderBlockedException("blocked", 403);
        }
    }
}

public class SettingsTests
{
    [Fact]
    public void Provider_toggles_store_only_deviations_from_defaults()
    {
        var settings = new AppSettings();
        var onByDefault = new ProviderInfo("a", "A", StoreKind.Official, "https://a.example/");
        var offByDefault = new ProviderInfo("b", "B", StoreKind.Official, "https://b.example/") { EnabledByDefault = false };

        Assert.True(settings.IsProviderEnabled(onByDefault));
        Assert.False(settings.IsProviderEnabled(offByDefault));

        settings.SetProviderEnabled(onByDefault, false);
        settings.SetProviderEnabled(offByDefault, true);
        Assert.False(settings.IsProviderEnabled(onByDefault));
        Assert.True(settings.IsProviderEnabled(offByDefault));

        settings.SetProviderEnabled(onByDefault, true);
        Assert.Empty(settings.DisabledProviders);
    }

    [Fact]
    public void Api_keys_are_encrypted_at_rest()
    {
        var settings = new AppSettings();
        settings.SetApiKey("itad", "secret-key");
        Assert.DoesNotContain("secret-key", settings.ProtectedApiKeys["itad"]);
        Assert.Equal("secret-key", settings.GetApiKey("itad"));
    }

    [Fact]
    public void History_is_deduplicated_and_capped()
    {
        var settings = new AppSettings();
        for (var i = 0; i < 30; i++)
            settings.AddToHistory($"game {i}");
        settings.AddToHistory("GAME 29");
        Assert.Equal(20, settings.SearchHistory.Count);
        Assert.Equal("GAME 29", settings.SearchHistory[0]);
    }
}
