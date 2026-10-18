using System.Globalization;
using System.Text;
using GamePriceHunter.Core.Currency;
using GamePriceHunter.Core.Diagnostics;
using GamePriceHunter.Core.Geo;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Net;
using GamePriceHunter.Core.Providers;
using GamePriceHunter.Core.Search;
using GamePriceHunter.Core.Settings;

// Diagnostics tool: runs the same search pipeline as the app and prints what each store returned.
//   gph search "witcher 3" [--country SK] [--currency EUR] [--providers steam,gog] [--top 40] [--details]
//   gph health [query]        exit code 1 when any provider fails
//   gph providers
Console.OutputEncoding = Encoding.UTF8;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Usage: gph search <query> [--country XX] [--currency XXX] [--providers id1,id2] [--top N] [--details]");
    Console.WriteLine("       gph health [query]");
    Console.WriteLine("       gph providers");
    return 0;
}

var http = HttpClientFactory.Create();
var providers = ProviderCatalog.CreateAll(http);
var command = args[0].ToLowerInvariant();

if (command == "providers")
{
    foreach (var provider in providers)
        Console.WriteLine($"{provider.Info.Id,-14} {provider.Info.Name,-22} {provider.Info.Kind,-12} {(provider.Info.EnabledByDefault ? "on" : "off")}");
    return 0;
}

var options = ParseOptions(args.Skip(1).ToArray());
var query = options.Query ?? (command == "health" ? "witcher" : null);
if (query is null)
{
    Console.Error.WriteLine("Missing query.");
    return 2;
}

var logger = new ConsoleLogger(options.Details);
var paths = AppPaths.Resolve();
var currency = new CurrencyService(http, paths.RatesCacheFile, logger);
var regionTask = options.Country is { } country && RegionDetector.TryCreate(country, out var manual)
    ? Task.FromResult(new DetectedRegion(manual, RegionSource.WindowsSettings))
    : new RegionDetector(http, logger).DetectAsync();
await Task.WhenAll(regionTask, currency.InitializeAsync());
var region = regionTask.Result.Region;
var displayCurrency = options.Currency ?? region.CurrencyCode;
Console.WriteLine($"Region {region.CountryCode}/{region.CurrencyCode} ({regionTask.Result.Source}); prices in {displayCurrency}; rates: {currency.Current?.Source ?? "none"}");

var searchOptions = new SearchOptions
{
    Providers = options.Providers ?? (command == "health" ? providers.Select(p => p.Info.Id).ToHashSet() : null),
};

var service = new SearchService(providers, logger);
var all = new List<Offer>();
var failures = 0;
await foreach (var result in service.SearchAsync(query, region, searchOptions))
{
    var status = result.Status == ProviderStatus.Succeeded ? $"{result.Offers.Count,3} of {result.RawCount,-3}" : result.Status.ToString();
    Console.WriteLine($"  {result.Provider.Name,-22} {status,-12} {result.Elapsed.TotalMilliseconds,6:0} ms  {result.Error}");
    if (result.Status is ProviderStatus.Failed or ProviderStatus.Blocked or ProviderStatus.TimedOut)
        failures++;
    all.AddRange(result.Offers);
}

if (command == "search")
{
    Console.WriteLine();
    var rows = all
        .Select(o => (Offer: o, Converted: currency.Convert(o.Price, displayCurrency)))
        .OrderBy(x => x.Converted?.Amount ?? decimal.MaxValue)
        .Take(options.Top);
    foreach (var (offer, converted) in rows)
    {
        var price = converted is { } c ? MoneyFormatter.Format(c) : "?";
        var original = offer.Price.Currency != displayCurrency ? $" ({MoneyFormatter.Format(offer.Price)})" : "";
        var discount = offer.DiscountPercent is { } d ? $" -{d}%" : "";
        var region2 = offer.Region is null ? "" : $" [{offer.Region}:{offer.RegionCompatibility}]";
        Console.WriteLine($"{price,12}{original,-14} {offer.StoreName,-18} {offer.Type,-7} {offer.Platform,-8} {offer.Title}{discount}{region2}");
        if (options.Details)
            Console.WriteLine($"{"",12} {offer.Url}  rel={offer.Relevance:0.00}  img={offer.ImageUrl}");
    }
}

return command == "health" && failures > 0 ? 1 : 0;

static CliOptions ParseOptions(string[] args)
{
    var options = new CliOptions();
    var free = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--country": options.Country = args[++i]; break;
            case "--currency": options.Currency = args[++i].ToUpperInvariant(); break;
            case "--providers": options.Providers = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase); break;
            case "--top": options.Top = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--details": options.Details = true; break;
            default: free.Add(args[i]); break;
        }
    }
    options.Query = free.Count > 0 ? string.Join(' ', free) : null;
    return options;
}

sealed class CliOptions
{
    public string? Query { get; set; }
    public string? Country { get; set; }
    public string? Currency { get; set; }
    public HashSet<string>? Providers { get; set; }
    public int Top { get; set; } = 40;
    public bool Details { get; set; }
}

sealed class ConsoleLogger(bool verbose) : IAppLogger
{
    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (level < LogLevel.Warning && !verbose)
            return;
        Console.Error.WriteLine($"[{level}] {message}{(exception is null ? "" : " " + exception.Message)}");
    }
}
