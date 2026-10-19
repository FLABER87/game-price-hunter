using System.Globalization;
using System.Net.Http;
using System.Reflection;
using GamePriceHunter.App.Infrastructure;
using GamePriceHunter.Core.Currency;
using GamePriceHunter.Core.Diagnostics;
using GamePriceHunter.Core.Geo;
using GamePriceHunter.Core.Giveaways;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Net;
using GamePriceHunter.Core.Providers;
using GamePriceHunter.Core.Search;
using GamePriceHunter.Core.Settings;
using GamePriceHunter.Core.Updates;

namespace GamePriceHunter.App.Services;

/// <summary>Composition root: every long-lived service of the app.</summary>
public sealed class AppServices : IDisposable
{
    private readonly System.Windows.Threading.DispatcherTimer _saveTimer;
    private readonly HttpClient _updateHttp;

    private AppServices(AppPaths paths, FileLogger logger, SettingsStore store, AppSettings settings, HttpClient http)
    {
        Paths = paths;
        Logger = logger;
        SettingsStore = store;
        Settings = settings;
        Http = http;
        Currency = new CurrencyService(http, paths.RatesCacheFile, logger);
        RegionDetector = new RegionDetector(http, logger);
        Search = new SearchService(ProviderCatalog.CreateAll(http), logger);
        Suggestions = new SuggestionService(http, logger);
        Giveaways = new GiveawayService(GiveawayService.CreateSources(http), paths.GiveawaysCacheFile, logger);
        _updateHttp = UpdateChecker.CreateHttpClient($"GamePriceHunter/{Version} (+{UpdateChecker.RepositoryUrl})");
        Updates = new UpdateChecker(_updateHttp);
        CanInstallUpdates = !paths.IsPortable && UpdateChecker.IsInstalledCopy(AppContext.BaseDirectory);

        _saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SettingsStore.Save(Settings);
        };
    }

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "0.0.0";

    public AppPaths Paths { get; }

    public IAppLogger Logger { get; }

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings { get; }

    public HttpClient Http { get; }

    public CurrencyService Currency { get; }

    public RegionDetector RegionDetector { get; }

    public SearchService Search { get; }

    public SuggestionService Suggestions { get; }

    public GiveawayService Giveaways { get; }

    public UpdateChecker Updates { get; }

    /// <summary>
    /// True for copies installed by the setup, which can download and run the next installer.
    /// Portable and stand-alone copies only point the user to the release page.
    /// </summary>
    public bool CanInstallUpdates { get; }

    /// <summary>Region detected at startup (before any user override).</summary>
    public DetectedRegion? DetectedRegion { get; set; }

    public static AppServices Create()
    {
        var paths = AppPaths.Resolve();
        var logger = new FileLogger(paths.LogDirectory);
        logger.Info($"Game Price Hunter {Version} starting (portable: {paths.IsPortable}).");

        var store = new SettingsStore(paths.SettingsFile, logger);
        var settings = store.Load();
        var http = HttpClientFactory.Create();
        ImageLoader.Initialize(http, paths.ImageCacheDirectory);
        return new AppServices(paths, logger, store, settings, http);
    }

    /// <summary>Saves settings shortly after the last change, coalescing bursts of edits.</summary>
    public void SaveSettingsSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveSettingsNow()
    {
        _saveTimer.Stop();
        SettingsStore.Save(Settings);
    }

    /// <summary>The region searches use: the user's override, else the detected one.</summary>
    public UserRegion EffectiveRegion
    {
        get
        {
            if (Settings.CountryOverride is { } country && Core.Geo.RegionDetector.TryCreate(country, out var manual))
                return manual;
            return DetectedRegion?.Region ?? UserRegion.Default;
        }
    }

    /// <summary>
    /// The country that picks the number format (Loc.FormatCulture): the search region once it is known,
    /// the Windows one until then.
    /// </summary>
    public string FormatCountry =>
        Settings.CountryOverride is not null || DetectedRegion is not null
            ? EffectiveRegion.CountryCode
            : RegionInfo.CurrentRegion.TwoLetterISORegionName;

    public string DisplayCurrency
    {
        get
        {
            var currency = Settings.CurrencyOverride ?? EffectiveRegion.CurrencyCode;
            var rates = Currency.Current;
            return rates is null || rates.Supports(currency) ? currency : "USD";
        }
    }

    public SearchOptions BuildSearchOptions() => new()
    {
        Providers = Search.Providers.Where(p => Settings.IsProviderEnabled(p.Info)).Select(p => p.Info.Id).ToHashSet(),
        ProviderTimeout = Settings.ProviderTimeout,
        MatchMode = Settings.MatchMode,
        MaxResultsPerProvider = Math.Clamp(Settings.MaxResultsPerProvider, 10, 200),
        ApiKeys = Search.Providers
            .Where(p => p.Info.RequiresApiKey)
            .Select(p => (p.Info.Id, Key: Settings.GetApiKey(p.Info.Id)))
            .Where(x => !string.IsNullOrEmpty(x.Key))
            .ToDictionary(x => x.Id, x => x.Key!),
    };

    public void Dispose()
    {
        SaveSettingsNow();
        Http.Dispose();
        _updateHttp.Dispose();
        (Logger as IDisposable)?.Dispose();
    }
}
