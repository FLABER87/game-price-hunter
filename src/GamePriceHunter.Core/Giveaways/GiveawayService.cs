using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using GamePriceHunter.Core.Diagnostics;
using GamePriceHunter.Core.Giveaways.Sources;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Providers;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Giveaways;

/// <summary>
/// Asks every giveaway source in parallel, merges what several of them report into one giveaway and
/// keeps the result on disk, so the list shows at once on the next start.
/// </summary>
public sealed class GiveawayService
{
    /// <summary>A feed younger than this is shown as it is; an older one is refreshed.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(30);

    /// <summary>A giveaway without an end listed longer ago is a permanent freebie rather than news.</summary>
    internal static readonly TimeSpan OpenEndedMaxAge = TimeSpan.FromDays(60);

    // TitleMatcher's score of a title that starts with the query; titles that only contain it score lower.
    private const double MinSearchRelevance = 0.7;

    // An end date without a time zone has passed everywhere a day later.
    private static readonly TimeSpan DateOnlyGrace = TimeSpan.FromDays(1);

    private static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly string? _cachePath;
    private readonly IAppLogger _logger;
    private readonly TimeProvider _time;

    public GiveawayService(IReadOnlyList<IGiveawaySource> sources, string? cachePath, IAppLogger logger, TimeProvider? time = null)
    {
        Sources = sources;
        _cachePath = cachePath;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The sources in priority order: when they report the same giveaway, the first one's details win.</summary>
    public static IReadOnlyList<IGiveawaySource> CreateSources(HttpClient http) =>
    [
        new EpicGiveawaySource(http),
        new SteamGiveawaySource(http),
        new CheapSharkGiveawaySource(http),
        new GamerPowerGiveawaySource(http),
    ];

    public IReadOnlyList<IGiveawaySource> Sources { get; }

    public DateTimeOffset Now => _time.GetUtcNow();

    public bool IsFresh(GiveawayFeed feed)
    {
        var age = Now - feed.FetchedAt;
        return age >= TimeSpan.Zero && age < FreshFor;
    }

    /// <summary>Giveaways running at <paramref name="now"/>: the ones ending soonest first, then those without an end, newest first.</summary>
    public static IReadOnlyList<Giveaway> Current(IEnumerable<Giveaway> giveaways, DateTimeOffset now) =>
        giveaways
            .Where(g => !(g.StartsAt > now) && !HasEnded(g, now) && !IsStale(g, now))
            .OrderBy(g => g.EndsAt is null)
            .ThenBy(g => g.EndsAt)
            .ThenByDescending(g => g.PublishedAt)
            .ToList();

    /// <summary>Announced giveaways that have not started yet, the nearest first.</summary>
    public static IReadOnlyList<Giveaway> Upcoming(IEnumerable<Giveaway> giveaways, DateTimeOffset now) =>
        giveaways.Where(g => g.StartsAt > now && !HasEnded(g, now)).OrderBy(g => g.StartsAt).ToList();

    /// <summary>
    /// Giveaways of the game searched for: titles that are the query or start with it ("Agent A: A puzzle in
    /// disguise" for "agent a"), not ones that merely contain it. Running ones come before announced ones, as
    /// they can be claimed now; within each, the best match first.
    /// </summary>
    public static IReadOnlyList<Giveaway> Matching(IEnumerable<Giveaway> giveaways, string query, DateTimeOffset now, MatchMode mode = MatchMode.AllWords)
    {
        var matcher = new TitleMatcher(query, mode);
        if (matcher.IsEmpty)
            return [];
        var list = giveaways as IReadOnlyCollection<Giveaway> ?? giveaways.ToList();
        return Ranked(Current(list, now)).Concat(Ranked(Upcoming(list, now))).ToList();

        IEnumerable<Giveaway> Ranked(IEnumerable<Giveaway> candidates) =>
            candidates
                .Select(g => (Giveaway: g, Score: matcher.Score(g.Title)))
                .Where(x => x.Score >= MinSearchRelevance)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Giveaway);
    }

    public static bool HasEnded(Giveaway giveaway, DateTimeOffset now) =>
        giveaway.EndsAt is { } ends && now >= (giveaway.EndsOnDateOnly ? ends + DateOnlyGrace : ends);

    private static bool IsStale(Giveaway giveaway, DateTimeOffset now) =>
        giveaway.EndsAt is null && giveaway.PublishedAt is { } published && now - published > OpenEndedMaxAge;

    /// <summary>The feed the last refresh saved, if it was made for this country.</summary>
    public GiveawayFeed? LoadCached(string countryCode)
    {
        try
        {
            if (_cachePath is null || !File.Exists(_cachePath))
                return null;
            var feed = JsonSerializer.Deserialize<GiveawayFeed>(File.ReadAllText(_cachePath), JsonOptions);
            return feed is not null && string.Equals(feed.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase) ? feed : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.Warn($"Could not read the giveaway cache: {ex.Message}");
            return null;
        }
    }

    public async Task<GiveawayFeed> RefreshAsync(UserRegion region, TimeSpan sourceTimeout, CancellationToken cancellationToken = default)
    {
        var runs = await Task.WhenAll(Sources.Select(source => RunSourceAsync(source, region, sourceTimeout, cancellationToken))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var feed = new GiveawayFeed(Now, region.CountryCode, Merge(runs.Select(r => r.Giveaways)), runs.Select(r => r.Result).ToList());
        _logger.Info($"Giveaways for {region.CountryCode}: {feed.Giveaways.Count} from {string.Join(", ", feed.Sources.Select(s => $"{s.SourceId} {s.Status} {s.Count}"))}.");
        // A refresh where every source failed must not replace a good list with an empty one.
        if (feed.Sources.Any(s => s.Status == ProviderStatus.Succeeded))
            Save(feed);
        return feed;
    }

    /// <summary>
    /// One giveaway per game and platform. <paramref name="bySource"/> comes in priority order: the
    /// first source's details win, later ones fill in what it lacks, like an exact end or a picture.
    /// </summary>
    internal static IReadOnlyList<Giveaway> Merge(IEnumerable<IReadOnlyList<Giveaway>> bySource)
    {
        var merged = new List<Giveaway>();
        var positions = new Dictionary<string, int>();
        foreach (var giveaway in bySource.SelectMany(list => list))
        {
            var key = Key(giveaway);
            if (positions.TryGetValue(key, out var position))
            {
                merged[position] = Combine(merged[position], giveaway);
            }
            else
            {
                positions[key] = merged.Count;
                merged.Add(giveaway);
            }
        }
        return merged;
    }

    private static string Key(Giveaway giveaway)
    {
        // Epic, Steam and GOG games are claimed in their own stores; anything else is told apart by title alone.
        var platform = giveaway.Platform is Platform.Epic or Platform.Steam or Platform.Gog ? giveaway.Platform : Platform.Other;
        return $"{platform}|{TextNormalizer.Normalize(giveaway.Title)}";
    }

    private static Giveaway Combine(Giveaway first, Giveaway other)
    {
        var takeEnd = first.EndsAt is null || (first.EndsOnDateOnly && other is { EndsAt: not null, EndsOnDateOnly: false });
        return first with
        {
            ImageUrl = first.ImageUrl ?? other.ImageUrl,
            Worth = first.Worth ?? other.Worth,
            StartsAt = first.StartsAt ?? other.StartsAt,
            EndsAt = takeEnd ? other.EndsAt : first.EndsAt,
            EndsOnDateOnly = takeEnd ? other.EndsOnDateOnly : first.EndsOnDateOnly,
            PublishedAt = first.PublishedAt is { } a && other.PublishedAt is { } b ? (a < b ? a : b) : first.PublishedAt ?? other.PublishedAt,
            IsRegionChecked = first.IsRegionChecked || other.IsRegionChecked,
            OtherSourceIds = other.SourceId == first.SourceId || first.OtherSourceIds.Contains(other.SourceId)
                ? first.OtherSourceIds
                : [.. first.OtherSourceIds, other.SourceId],
        };
    }

    private async Task<(GiveawaySourceResult Result, IReadOnlyList<Giveaway> Giveaways)> RunSourceAsync(
        IGiveawaySource source,
        UserRegion region,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var id = source.Info.Id;
        var stopwatch = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);
        try
        {
            var giveaways = await source.GetAsync(region, budget.Token).ConfigureAwait(false);
            return (new GiveawaySourceResult(id, ProviderStatus.Succeeded, giveaways.Count, stopwatch.Elapsed), giveaways);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (new GiveawaySourceResult(id, ProviderStatus.Cancelled, 0, stopwatch.Elapsed), []);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn($"Giveaways from {source.Info.Name}: timed out after {timeout.TotalSeconds:0} s.");
            return (new GiveawaySourceResult(id, ProviderStatus.TimedOut, 0, stopwatch.Elapsed, "Timed out"), []);
        }
        catch (ProviderBlockedException ex)
        {
            _logger.Warn($"Giveaways from {source.Info.Name}: blocked. {ex.Message}");
            return (new GiveawaySourceResult(id, ProviderStatus.Blocked, 0, stopwatch.Elapsed, ex.Message), []);
        }
        catch (Exception ex)
        {
            _logger.Error($"Giveaways from {source.Info.Name} failed.", ex);
            return (new GiveawaySourceResult(id, ProviderStatus.Failed, 0, stopwatch.Elapsed, ex.Message), []);
        }
    }

    private void Save(GiveawayFeed feed)
    {
        if (_cachePath is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temp = _cachePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(feed, JsonOptions));
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Could not write the giveaway cache: {ex.Message}");
        }
    }
}
