using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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

// Diagnostics tool: runs the same search pipeline as the app and prints what each store returned.
//   gph search "witcher 3" [--country SK] [--currency EUR] [--providers steam,gog] [--top 40] [--details]
//   gph health [query]        exit code 1 when any provider fails
//   gph giveaways [--country SK] [--currency EUR] [--details]   what is free to keep right now
//   gph providers
//   gph update                the latest release, as the app's update check sees it
// Release signing (see build\publish.ps1 and build\new-signing-key.ps1):
//   gph sign <installer> --tag v1.2.3     writes <installer>.sig for the in-app updater, with the key in UPDATE_SIGNING_KEY
//   gph verify <installer> --tag v1.2.3   checks <installer>.sig against the app's release key; exit code 1 when it fails
//   gph new-key <owner/repository>        creates a key, stores it in the repository's UPDATE_SIGNING_KEY secret (gh)
//                                         and prints the public half for UpdateSignature.ReleasePublicKey
const string SigningKeyVariable = "UPDATE_SIGNING_KEY";
Console.OutputEncoding = Encoding.UTF8;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Usage: gph search <query> [--country XX] [--currency XXX] [--providers id1,id2] [--top N] [--details]");
    Console.WriteLine("       gph health [query]");
    Console.WriteLine("       gph giveaways [--country XX] [--currency XXX] [--details]");
    Console.WriteLine("       gph providers");
    Console.WriteLine("       gph update");
    Console.WriteLine("       gph sign|verify <installer> --tag v1.2.3");
    Console.WriteLine("       gph new-key <owner/repository>");
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

if (command == "update")
{
    using var updateHttp = UpdateChecker.CreateHttpClient("GamePriceHunter-Cli");
    var latest = await new UpdateChecker(updateHttp).GetLatestReleaseAsync();
    Console.WriteLine(latest is null ? "No release published." : $"Latest release {latest.Tag} ({latest.Version})\n  {latest.PageUrl}\n  {latest.InstallerUrl}\n  {latest.SignatureUrl}");
    return 0;
}

if (command == "new-key")
{
    if (args.Length != 2)
    {
        Console.Error.WriteLine("Usage: gph new-key <owner/repository>");
        return 2;
    }

    // The private key goes to gh on its standard input, so it never appears on screen, on disk or in a command line.
    using var key = UpdateSignature.CreateKey();
    var start = new ProcessStartInfo("gh")
    {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
    };
    foreach (var argument in new[] { "secret", "set", SigningKeyVariable, "--repo", args[1] })
        start.ArgumentList.Add(argument);

    using (var gh = Process.Start(start)!)
    {
        var output = gh.StandardOutput.ReadToEndAsync();
        await gh.StandardInput.WriteAsync(key.ExportPkcs8PrivateKeyPem());
        gh.StandardInput.Close();
        await gh.WaitForExitAsync();
        Console.Error.Write(await output);
        if (gh.ExitCode != 0)
        {
            Console.Error.WriteLine($"gh could not store the key in the {SigningKeyVariable} secret of {args[1]}.");
            return 1;
        }
    }

    Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    return 0;
}

if (command is "sign" or "verify")
{
    if (args.Length != 4 || args[2] != "--tag")
    {
        Console.Error.WriteLine($"Usage: gph {command} <installer> --tag v1.2.3");
        return 2;
    }

    var installer = args[1];
    var tag = args[3];
    var signaturePath = installer + UpdateSignature.FileExtension;
    if (command == "sign")
    {
        using var key = LoadSigningKey();
        if (key is null)
            return 2;
        await File.WriteAllTextAsync(signaturePath, await UpdateSignature.SignAsync(installer, tag, key));
        Console.WriteLine($"Signed {installer} for {tag}: {signaturePath}");
        return 0;
    }

    var valid = File.Exists(signaturePath) && await UpdateSignature.VerifyAsync(installer, tag, await File.ReadAllTextAsync(signaturePath));
    Console.WriteLine(valid
        ? $"{signaturePath} is valid for {tag}."
        : $"{signaturePath} is missing or does not match {installer} and {tag} under the app's release key.");
    return valid ? 0 : 1;
}

var options = ParseOptions(args.Skip(1).ToArray());
var query = options.Query ?? (command switch { "health" => "witcher", "giveaways" => "", _ => null });
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

if (command == "giveaways")
{
    var giveawayService = new GiveawayService(GiveawayService.CreateSources(http), null, logger);
    var feed = await giveawayService.RefreshAsync(region, TimeSpan.FromSeconds(20));
    foreach (var result in feed.Sources)
    {
        var name = giveawayService.Sources.First(s => s.Info.Id == result.SourceId).Info.Name;
        var status = result.Status == ProviderStatus.Succeeded ? $"{result.Count,3}" : result.Status.ToString();
        Console.WriteLine($"  {name,-22} {status,-12} {result.Elapsed.TotalMilliseconds,6:0} ms  {result.Error}");
    }

    var now = giveawayService.Now;
    PrintGiveaways("Free now", GiveawayService.Current(feed.Giveaways, now));
    PrintGiveaways("Coming soon", GiveawayService.Upcoming(feed.Giveaways, now));
    return feed.Sources.Any(s => s.Status is ProviderStatus.Failed or ProviderStatus.Blocked or ProviderStatus.TimedOut) ? 1 : 0;

    void PrintGiveaways(string header, IReadOnlyList<Giveaway> list)
    {
        Console.WriteLine();
        Console.WriteLine($"{header}: {list.Count}");
        foreach (var giveaway in list)
        {
            var worth = giveaway.Worth is { } w ? MoneyFormatter.Format(currency.Convert(w, displayCurrency) ?? w) : "?";
            var when = giveaway.StartsAt > now ? $"from {giveaway.StartsAt:yyyy-MM-dd HH:mm}"
                : giveaway.EndsAt is { } ends ? (giveaway.EndsOnDateOnly ? $"until {ends:yyyy-MM-dd}" : $"until {ends:yyyy-MM-dd HH:mm}")
                : "no end";
            var sources = string.Join("+", giveaway.OtherSourceIds.Prepend(giveaway.SourceId));
            Console.WriteLine($"{worth,10} {when,-23} {giveaway.StoreName,-18} {giveaway.Kind,-5} {giveaway.Platform,-8} {giveaway.Title}  [{sources}{(giveaway.IsRegionChecked ? "" : ", region?")}]");
            if (options.Details)
                Console.WriteLine($"{"",10} {giveaway.Url}  img={giveaway.ImageUrl}");
        }
    }
}

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
        {
            Console.WriteLine($"{"",12} {offer.Url}  rel={offer.Relevance:0.00}  img={offer.ImageUrl}");
            if (offer.Seller is not null)
            {
                var rating = offer.SellerRating is { } r ? $"  rating={r.Value:0.##}{(r.OutOf is { } max ? $"/{max:0.#}" : "")}" : "";
                var sales = offer.Sales is { } s ? $"  sold={s.Count}{(s.AtLeast ? "+" : "")}" : "";
                Console.WriteLine($"{"",12} seller={offer.Seller}{rating}{sales}");
            }
        }
    }
}

return command == "health" && failures > 0 ? 1 : 0;

static ECDsa? LoadSigningKey()
{
    var pem = Environment.GetEnvironmentVariable(SigningKeyVariable);
    if (string.IsNullOrWhiteSpace(pem))
    {
        Console.Error.WriteLine($"Set {SigningKeyVariable} to the release signing key (PEM).");
        return null;
    }

    var key = ECDsa.Create();
    key.ImportFromPem(pem);
    return key;
}

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
