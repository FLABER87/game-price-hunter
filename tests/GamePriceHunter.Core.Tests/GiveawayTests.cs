using System.Text.Json;
using GamePriceHunter.Core.Diagnostics;
using GamePriceHunter.Core.Giveaways;
using GamePriceHunter.Core.Giveaways.Sources;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Providers;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Tests;

/// <summary>Giveaway sources against responses captured on 2026-10-19, and the service that merges them.</summary>
public class GiveawayTests
{
    // The evening the fixtures were captured.
    private static readonly DateTimeOffset CaptureTime = new(2026, 10, 19, 20, 0, 0, TimeSpan.Zero);

    private static readonly HttpClient Http = new();

    private static JsonElement Json(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name))).RootElement;

    private static IReadOnlyList<Giveaway> Epic() => new EpicGiveawaySource(Http).Parse(Json("epic_freegames.json"));

    private static IReadOnlyList<Giveaway> CheapShark() =>
        new CheapSharkGiveawaySource(Http).Parse(
            Json("cheapshark_free.json"),
            Json("cheapshark_stores.json").EnumerateArray().ToDictionary(s => s.Str("storeID")!, s => s.Str("storeName")!));

    private static IReadOnlyList<Giveaway> GamerPower() => new GamerPowerGiveawaySource(Http).Parse(Json("gamerpower_giveaways.json"));

    [Fact]
    public void Epic_lists_this_and_next_weeks_free_games_but_not_its_discounts()
    {
        var giveaways = Epic();
        Assert.Equal(
            new[] { "Agent A: A puzzle in disguise", "Bad Cheese", "Home - A Unique Horror Adventure", "Sker Ritual" },
            giveaways.Select(g => g.Title).Order());

        var agent = giveaways.Single(g => g.Title == "Agent A: A puzzle in disguise");
        Assert.Equal("https://store.epicgames.com/p/agent-a-a-puzzle-in-disguise-42fe5f", agent.Url);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 15, 0, 0, TimeSpan.Zero), agent.StartsAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 15, 0, 0, TimeSpan.Zero), agent.EndsAt);
        Assert.False(agent.EndsOnDateOnly);
        Assert.Equal(Money.Of(17.99m, "EUR"), agent.Worth);
        Assert.Equal(Platform.Epic, agent.Platform);
        Assert.Equal(GiveawayKind.Game, agent.Kind);
        Assert.True(agent.IsRegionChecked);
        Assert.StartsWith("https://cdn1.epicgames.com/", agent.ImageUrl);

        var upcoming = giveaways.Single(g => g.Title == "Sker Ritual");
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 15, 0, 0, TimeSpan.Zero), upcoming.StartsAt);
    }

    [Fact]
    public void Steam_search_rows_give_the_app_ids()
    {
        var html = Json("steam_free_search.json").Str("results_html")!;
        Assert.Equal(new[] { 4841320L, 4114140L }, SteamGiveawaySource.ParseSearchIds(html));
    }

    [Fact]
    public void Steam_items_tell_the_end_and_the_regular_price()
    {
        var giveaways = new SteamGiveawaySource(Http).ParseItems(Json("steam_free_items.json"), "EUR");
        Assert.Equal(2, giveaways.Count);

        var squad = giveaways.Single(g => g.Title == "Squad Weapon Skins - Recon Team Pack");
        Assert.Equal("https://store.steampowered.com/app/4841320/", squad.Url);
        Assert.Equal(GiveawayKind.Dlc, squad.Kind);
        Assert.Equal(Money.Of(12.25m, "EUR"), squad.Worth);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792951200), squad.EndsAt);
        Assert.Equal(
            "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/4841320/9b41c1a520089e393fefb359f312b9e5cdaaea84/capsule_616x353.jpg?t=1792087188",
            squad.ImageUrl);
    }

    [Fact]
    public void CheapShark_deals_at_100_percent_off()
    {
        var giveaways = CheapShark();
        Assert.Equal(3, giveaways.Count);
        Assert.All(giveaways, g =>
        {
            Assert.Equal("Epic Games Store", g.StoreName);
            Assert.Equal(Platform.Epic, g.Platform);
            Assert.StartsWith("https://www.cheapshark.com/redirect?dealID=", g.Url);
            Assert.False(g.IsRegionChecked);
        });
        Assert.Equal(Money.Of(12.99m, "USD"), giveaways.Single(g => g.Title == "Bad Cheese").Worth);
    }

    [Theory]
    [InlineData("Last Room (IndieGala) Giveaway", "Last Room", "IndieGala")]
    [InlineData("Sausage Hunter (Indiegala) Giveaway", "Sausage Hunter", "IndieGala")]
    [InlineData("GetHigh.EXE (Epic Games) Giveaway", "GetHigh.EXE", "Epic Games Store")]
    [InlineData("Chop Shop Playtest (Steam) Key Giveaway", "Chop Shop Playtest", "Steam")]
    [InlineData("Fathom Reach Playtest Steam Key Giveaway", "Fathom Reach Playtest", null)]
    [InlineData("Gear.Club Unlimited 3 - Gear.Club Signature Pack Steam Giveaway", "Gear.Club Unlimited 3 - Gear.Club Signature Pack", null)]
    [InlineData("Zero King (Beta) Steam Key Giveaway", "Zero King (Beta)", null)]
    [InlineData("Crystal Saga Nova: Free Gift Pack Keys", "Crystal Saga Nova: Free Gift Pack", null)]
    [InlineData("World of Tanks: Halloween Gift Packs", "World of Tanks: Halloween Gift Packs", null)]
    public void GamerPower_titles_lose_the_giveaway_words(string raw, string title, string? store)
    {
        var (clean, tagged) = GamerPowerGiveawaySource.SplitTitle(raw);
        Assert.Equal(title, clean);
        Assert.Equal(store, tagged?.Name);
    }

    [Fact]
    public void GamerPower_names_the_site_where_the_giveaway_is_claimed()
    {
        var giveaways = GamerPower();
        Assert.Equal(105, giveaways.Count);

        var lastRoom = giveaways.Single(g => g.Title == "Last Room");
        Assert.Equal("IndieGala", lastRoom.StoreName);
        Assert.Equal(Platform.DrmFree, lastRoom.Platform);
        Assert.Equal(GiveawayKind.Game, lastRoom.Kind);
        Assert.Equal(Money.Of(29.99m, "USD"), lastRoom.Worth);
        Assert.Null(lastRoom.EndsAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 11, 58, 10, TimeSpan.Zero), lastRoom.PublishedAt);
        Assert.Equal("https://www.gamerpower.com/open/last-room-pc-giveaway", lastRoom.Url);

        // A Steam key handed out by Alienware Arena is not a Steam store giveaway.
        var playtest = giveaways.Single(g => g.Title == "Fathom Reach Playtest");
        Assert.Equal("Alienware Arena", playtest.StoreName);
        Assert.Equal(Platform.Steam, playtest.Platform);
        Assert.Equal(GiveawayKind.Beta, playtest.Kind);

        var black = giveaways.Single(g => g.Title == "The Black Within");
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 23, 59, 59, TimeSpan.Zero), black.EndsAt);
        Assert.True(black.EndsOnDateOnly);
        Assert.All(giveaways, g => Assert.False(g.IsRegionChecked));
    }

    [Fact]
    public void GamerPower_without_giveaways_answers_with_a_status_object()
    {
        using var doc = JsonDocument.Parse("""{"status":0,"status_message":"No active giveaways available at the moment, please try again later."}""");
        Assert.Empty(new GamerPowerGiveawaySource(Http).Parse(doc.RootElement));
    }

    [Fact]
    public void The_first_source_wins_and_the_others_fill_in()
    {
        var merged = GiveawayService.Merge([Epic(), CheapShark(), GamerPower()]);

        var cheese = Assert.Single(merged, g => g.Title == "Bad Cheese");
        Assert.Equal(EpicGiveawaySource.SourceId, cheese.SourceId);
        Assert.Equal(new[] { CheapSharkGiveawaySource.SourceId, GamerPowerGiveawaySource.SourceId }, cheese.OtherSourceIds);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 15, 0, 0, TimeSpan.Zero), cheese.EndsAt);
        Assert.False(cheese.EndsOnDateOnly);
        Assert.Equal(Money.Of(11.69m, "EUR"), cheese.Worth);
        Assert.True(cheese.IsRegionChecked);

        // Missing from Epic's list: CheapShark's details, with GamerPower's end date.
        var getHigh = Assert.Single(merged, g => g.Title == "GetHigh.EXE");
        Assert.Equal(CheapSharkGiveawaySource.SourceId, getHigh.SourceId);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 23, 59, 0, TimeSpan.Zero), getHigh.EndsAt);
        Assert.True(getHigh.EndsOnDateOnly);
    }

    [Fact]
    public void Same_title_on_different_platforms_stays_apart()
    {
        Giveaway Make(string source, Platform platform) => new()
        {
            SourceId = source, Title = "Some Game", StoreName = "Store", Url = "https://example.com/" + source, Platform = platform,
        };
        var merged = GiveawayService.Merge([[Make("a", Platform.Epic)], [Make("b", Platform.Steam), Make("c", Platform.DrmFree)]]);
        Assert.Equal(3, merged.Count);
    }

    [Fact]
    public void Shows_running_giveaways_ending_soonest_first()
    {
        var merged = GiveawayService.Merge([Epic(), CheapShark(), GamerPower()]);
        var games = GiveawayService.Current(merged, CaptureTime).Where(g => g.Kind == GiveawayKind.Game).Select(g => g.Title).ToList();

        Assert.Equal(new[] { "Agent A: A puzzle in disguise", "Bad Cheese" }, games.Take(2).Order());
        Assert.DoesNotContain("Sker Ritual", games);
        // Without an end and listed over 60 days ago: a permanent freebie, not news.
        Assert.DoesNotContain("Dual Chroma: Academy Carols", games);
        Assert.DoesNotContain("GigaBash", games);
        Assert.Contains("Leaper", games);
        Assert.True(games.IndexOf("Last Room") < games.IndexOf("Leaper"), "Open-ended giveaways go newest first.");

        Assert.Equal(
            new[] { "Home - A Unique Horror Adventure", "Sker Ritual" },
            GiveawayService.Upcoming(merged, CaptureTime).Select(g => g.Title).Order());
    }

    [Fact]
    public void A_date_without_a_time_zone_ends_a_day_late()
    {
        var giveaway = new Giveaway
        {
            SourceId = "x", Title = "T", StoreName = "S", Url = "https://example.com/",
            EndsAt = new DateTimeOffset(2026, 10, 19, 23, 59, 0, TimeSpan.Zero), EndsOnDateOnly = true,
        };
        Assert.False(GiveawayService.HasEnded(giveaway, new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero)));
        Assert.True(GiveawayService.HasEnded(giveaway, new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero)));
        Assert.True(GiveawayService.HasEnded(giveaway with { EndsOnDateOnly = false }, new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task Refresh_saves_the_feed_for_the_country_and_reports_each_source()
    {
        var cache = Path.Combine(Path.GetTempPath(), $"gph-giveaways-{Guid.NewGuid():N}.json");
        try
        {
            var game = new Giveaway { SourceId = "ok", Title = "Free Game", StoreName = "Store", Url = "https://example.com/game" };
            var service = new GiveawayService([new FakeSource("ok", [game]), new FakeSource("broken", null)], cache, NullLogger.Instance, new FixedTime(CaptureTime));

            var feed = await service.RefreshAsync(new UserRegion("SK", "EUR"), TimeSpan.FromSeconds(5));
            Assert.Equal(CaptureTime, feed.FetchedAt);
            Assert.Equal("Free Game", Assert.Single(feed.Giveaways).Title);
            Assert.Equal(ProviderStatus.Succeeded, feed.Sources.Single(s => s.SourceId == "ok").Status);
            Assert.Equal(ProviderStatus.Failed, feed.Sources.Single(s => s.SourceId == "broken").Status);
            Assert.True(service.IsFresh(feed));

            var cached = service.LoadCached("SK");
            Assert.NotNull(cached);
            Assert.Equal(feed.Giveaways, cached.Giveaways, (a, b) => a.Title == b.Title && a.Url == b.Url);
            Assert.Null(service.LoadCached("PL"));

            // A refresh where every source fails keeps the saved list.
            var offline = new GiveawayService([new FakeSource("broken", null)], cache, NullLogger.Instance, new FixedTime(CaptureTime.AddHours(1)));
            Assert.Empty((await offline.RefreshAsync(new UserRegion("SK", "EUR"), TimeSpan.FromSeconds(5))).Giveaways);
            Assert.Single(offline.LoadCached("SK")!.Giveaways);
            Assert.False(offline.IsFresh(cached));
        }
        finally
        {
            File.Delete(cache);
        }
    }

    private sealed class FakeSource(string id, IReadOnlyList<Giveaway>? giveaways) : IGiveawaySource
    {
        public ProviderInfo Info { get; } = new(id, id, StoreKind.Official, "https://example.com/");

        public Task<IReadOnlyList<Giveaway>> GetAsync(UserRegion region, CancellationToken cancellationToken) =>
            giveaways is null ? throw new HttpRequestException("offline") : Task.FromResult(giveaways);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
