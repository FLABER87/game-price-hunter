using System.Globalization;
using GamePriceHunter.Core.Diagnostics;
using GamePriceHunter.Core.Globalization;
using GamePriceHunter.Core.Models;

namespace GamePriceHunter.Core.Geo;

public enum RegionSource
{
    IpAddress,
    WindowsSettings,
    Fallback,
}

public sealed record DetectedRegion(UserRegion Region, RegionSource Source);

/// <summary>
/// Detects the user's country from the public IP (what stores see, which is what decides
/// regional prices) and falls back to the Windows region setting when offline.
/// </summary>
public sealed class RegionDetector(HttpClient http, IAppLogger logger)
{
    private static readonly string[] TraceUrls =
    [
        "https://www.cloudflare.com/cdn-cgi/trace",
        "https://1.1.1.1/cdn-cgi/trace",
    ];

    public async Task<DetectedRegion> DetectAsync(CancellationToken cancellationToken = default)
    {
        foreach (var url in TraceUrls)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                var text = await http.GetStringAsync(url, timeout.Token).ConfigureAwait(false);
                var country = ParseTrace(text);
                if (country is not null && TryCreate(country, out var region))
                    return new DetectedRegion(region, RegionSource.IpAddress);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.Warn($"Region lookup via {url} failed: {ex.Message}");
            }
        }

        if (TryCreate(RegionInfo.CurrentRegion.TwoLetterISORegionName, out var system))
            return new DetectedRegion(system, RegionSource.WindowsSettings);

        return new DetectedRegion(UserRegion.Default, RegionSource.Fallback);
    }

    /// <summary>Builds a region with the country's official currency.</summary>
    public static bool TryCreate(string countryCode, out UserRegion region)
    {
        region = UserRegion.Default;
        if (string.IsNullOrWhiteSpace(countryCode) || countryCode.Length != 2)
            return false;

        try
        {
            var info = new RegionInfo(countryCode.ToUpperInvariant());
            region = new UserRegion(info.TwoLetterISORegionName.ToUpperInvariant(), info.ISOCurrencySymbol.ToUpperInvariant());
            return true;
        }
        catch (ArgumentException)
        {
            // "XX", "T1" (Tor) and other pseudo-codes.
            return false;
        }
    }

    internal static string? ParseTrace(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("loc=", StringComparison.Ordinal))
                return line[4..].Trim();
        }
        return null;
    }
}

/// <summary>Country list for the settings UI.</summary>
public static class Countries
{
    private static readonly Lazy<IReadOnlyList<RegionInfo>> All = new(() =>
        CultureInfo.GetCultures(CultureTypes.SpecificCultures)
            .Select(c =>
            {
                try { return new RegionInfo(c.Name); }
                catch (ArgumentException) { return null; }
            })
            .OfType<RegionInfo>()
            .Where(r => r.TwoLetterISORegionName.Length == 2 && char.IsLetter(r.TwoLetterISORegionName[0]))
            .DistinctBy(r => r.TwoLetterISORegionName)
            .OrderBy(r => r.EnglishName, StringComparer.OrdinalIgnoreCase)
            .ToList());

    public static IReadOnlyList<RegionInfo> List => All.Value;

    /// <summary>The country's name in the language of <paramref name="uiCulture"/>, English if Windows has none.</summary>
    public static string DisplayName(string countryCode, CultureInfo uiCulture)
    {
        if (IcuNames.Country(countryCode, uiCulture.Name) is { Length: > 0 } name)
            return name;
        try
        {
            return new RegionInfo(countryCode).EnglishName;
        }
        catch (ArgumentException)
        {
            return countryCode;
        }
    }

    /// <summary>Every country with its name in the language of <paramref name="uiCulture"/>, in that language's alphabetical order.</summary>
    public static IReadOnlyList<(string Code, string Name)> Choices(CultureInfo uiCulture)
    {
        var comparer = StringComparer.Create(uiCulture, ignoreCase: true);
        return List
            .Select(r => (Code: r.TwoLetterISORegionName, Name: DisplayName(r.TwoLetterISORegionName, uiCulture)))
            .OrderBy(c => c.Name, comparer)
            .ToList();
    }
}
