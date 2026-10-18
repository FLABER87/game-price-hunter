using System.Globalization;
using System.Text.RegularExpressions;
using GamePriceHunter.Core.Models;

namespace GamePriceHunter.Core.Geo;

/// <summary>Activation region of a key and whether it suits the user's country.</summary>
public sealed record RegionTag(string Label, RegionCompatibility Compatibility);

/// <summary>
/// Recognizes key region markers ("Steam Key GLOBAL", "(EU)", "RU/CIS/KZ") in store titles
/// and region labels, and checks them against the user's country.
/// </summary>
public static partial class RegionTags
{
    private const string GlobalLabel = "Global";

    private static readonly string[] EuropeCountries =
    [
        "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
        "PL", "PT", "RO", "SK", "SI", "ES", "SE", "IS", "LI", "NO", "CH", "GB", "RS", "ME", "MK", "AL", "BA", "XK", "AD", "MC",
        "SM", "VA",
    ];

    private static readonly string[] CisCountries = ["RU", "BY", "KZ", "AM", "AZ", "KG", "MD", "TJ", "TM", "UZ"];

    private static readonly string[] LatamCountries =
    [
        "MX", "BR", "AR", "CL", "CO", "PE", "UY", "EC", "VE", "BO", "PY", "CR", "PA", "GT", "HN", "SV", "NI", "DO", "PR",
    ];

    private static readonly string[] AsiaCountries =
    [
        "CN", "JP", "KR", "IN", "ID", "TH", "VN", "MY", "PH", "SG", "HK", "TW", "PK", "BD", "LK", "MO", "KH", "MM", "NP",
    ];

    private static readonly string[] MenaCountries = ["AE", "SA", "QA", "KW", "BH", "OM", "EG", "JO", "LB", "MA", "TN", "DZ", "IQ", "IL"];

    /// <summary>Region words → label and countries. A null country list means "works everywhere".</summary>
    private static readonly Dictionary<string, (string Label, string[]? Countries)> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GLOBAL"] = (GlobalLabel, null),
        ["WORLDWIDE"] = (GlobalLabel, null),
        ["WW"] = (GlobalLabel, null),
        ["REGIONFREE"] = (GlobalLabel, null),
        ["ВЕСЬМИР"] = (GlobalLabel, null),
        ["EU"] = ("EU", EuropeCountries),
        ["EUROPE"] = ("EU", EuropeCountries),
        ["ЕВРОПА"] = ("EU", EuropeCountries),
        ["CIS"] = ("CIS", CisCountries),
        ["СНГ"] = ("CIS", CisCountries),
        ["РФ"] = ("RU", ["RU"]),
        ["РОССИЯ"] = ("RU", ["RU"]),
        ["RUSSIA"] = ("RU", ["RU"]),
        ["UK"] = ("UK", ["GB"]),
        ["UNITEDKINGDOM"] = ("UK", ["GB"]),
        ["USA"] = ("US", ["US"]),
        ["UNITEDSTATES"] = ("US", ["US"]),
        ["NA"] = ("NA", ["US", "CA"]),
        ["NORTHAMERICA"] = ("NA", ["US", "CA"]),
        ["LATAM"] = ("LATAM", LatamCountries),
        ["LATINAMERICA"] = ("LATAM", LatamCountries),
        ["SOUTHAMERICA"] = ("LATAM", LatamCountries),
        ["ASIA"] = ("Asia", AsiaCountries),
        ["SEA"] = ("SEA", ["ID", "TH", "VN", "MY", "PH", "SG"]),
        ["MENA"] = ("MENA", MenaCountries),
        ["TURKEY"] = ("TR", ["TR"]),
        ["ARGENTINA"] = ("AR", ["AR"]),
        ["BRAZIL"] = ("BR", ["BR"]),
        ["INDIA"] = ("IN", ["IN"]),
        ["CHINA"] = ("CN", ["CN"]),
        ["POLAND"] = ("PL", ["PL"]),
        ["GERMANY"] = ("DE", ["DE"]),
        ["UKRAINE"] = ("UA", ["UA"]),
        ["УКРАИНА"] = ("UA", ["UA"]),
        ["KAZAKHSTAN"] = ("KZ", ["KZ"]),
        ["КАЗАХСТАН"] = ("KZ", ["KZ"]),
    };

    /// <summary>"Rest of world" keys exclude unknown regions, so they stay "unknown".</summary>
    private static readonly HashSet<string> UnknownWords = new(StringComparer.OrdinalIgnoreCase) { "ROW", "RESTOFWORLD", "RESTOFTHEWORLD" };

    /// <summary>Two-letter country codes that far more often mean something else in game titles.</summary>
    private static readonly HashSet<string> AmbiguousCodes =
    [
        "PS", "TV", "AI", "VR", "HD", "PC", "DX", "OS", "CD", "DL", "GO", "ME", "IT", "AM", "PM", "NO", "IS", "SE", "MS", "NG",
        "BE", "TO", "AT", "GE", "ST", "SO", "MY", "AS", "BY",
    ];

    public static RegionTag? FromTitle(string title, string userCountry)
    {
        var tokens = new List<string>();
        foreach (var segment in ContextSegments(title))
            tokens.AddRange(TokenRegex().Matches(segment).Select(m => m.Value));

        // Long, unambiguous words count anywhere if written in capitals ("Steam Key GLOBAL"),
        // but not in all-caps titles where "GLOBAL OFFENSIVE" would be misread.
        if (!IsMostlyUpperCase(title))
        {
            foreach (Match match in CapsWordRegex().Matches(title))
                tokens.Add(match.Value);
            if (title.Contains("REGION FREE", StringComparison.Ordinal))
                tokens.Add("REGIONFREE");
            if (title.Contains("NORTH AMERICA", StringComparison.OrdinalIgnoreCase))
                tokens.Add("NORTHAMERICA");
        }

        // Marketplace sellers write "RU+World" / "РУ + МИР" for keys that work everywhere.
        if (PlusWorldRegex().IsMatch(title))
            tokens.Add("GLOBAL");

        return Evaluate(tokens, userCountry);
    }

    /// <summary>Parses an explicit region label from a store API ("Europe", "Region free", "RU/CIS").</summary>
    public static RegionTag? FromLabel(string? label, string userCountry)
    {
        if (string.IsNullOrWhiteSpace(label))
            return null;

        // Lists ("united states, canada", "RU/CIS") are read item by item, so multi-word names keep their meaning.
        var tokens = LabelListRegex().Split(label).SelectMany(LabelTokens).ToList();
        return Evaluate(tokens, userCountry, trustCodes: true) ?? new RegionTag(label.Trim(), RegionCompatibility.Unknown);
    }

    private static IEnumerable<string> LabelTokens(string item)
    {
        var compact = item.Replace(" ", "").Replace("-", "");
        if (Words.ContainsKey(compact) || UnknownWords.Contains(compact))
            return [compact];

        // Stores also label keys with plain country names: "France", "United States".
        if (CountryNames.Value.TryGetValue(compact, out var countryCode))
            return [countryCode];

        // "RU+World" style labels: "World" alone means everywhere.
        return TokenRegex().Matches(item)
            .Select(m => m.Value)
            .Select(t => t.Equals("World", StringComparison.OrdinalIgnoreCase) ? "GLOBAL"
                : CountryNames.Value.TryGetValue(t, out var code) ? code
                : t.Length == 2 ? t.ToUpperInvariant()
                : t);
    }

    private static readonly Lazy<Dictionary<string, string>> CountryNames = new(() =>
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var region in Countries.List)
            names.TryAdd(region.EnglishName.Replace(" ", "").Replace("-", ""), region.TwoLetterISORegionName.ToUpperInvariant());
        return names;
    });

    /// <param name="trustCodes">Labels from store APIs: every valid two-letter code is a country.</param>
    private static RegionTag? Evaluate(IEnumerable<string> tokens, string userCountry, bool trustCodes = false)
    {
        var labels = new List<string>();
        var countries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var isGlobal = false;
        var hasUnknown = false;

        foreach (var raw in tokens)
        {
            var token = raw.Trim();
            if (Words.TryGetValue(token, out var entry))
            {
                AddLabel(labels, entry.Label);
                if (entry.Countries is null)
                    isGlobal = true;
                else
                    countries.UnionWith(entry.Countries);
            }
            else if (UnknownWords.Contains(token))
            {
                AddLabel(labels, "ROW");
                hasUnknown = true;
            }
            else if (token.Length == 2 && IsCountryCode(token, trustCodes))
            {
                var code = token.ToUpperInvariant();
                AddLabel(labels, code);
                countries.Add(code);
            }
        }

        if (labels.Count == 0)
            return null;

        if (isGlobal)
            return new RegionTag(GlobalLabel, RegionCompatibility.Compatible);

        var label = string.Join("/", labels);
        if (countries.Contains(userCountry))
            return new RegionTag(label, RegionCompatibility.Compatible);
        if (hasUnknown)
            return new RegionTag(label, RegionCompatibility.Unknown);
        return new RegionTag(label, countries.Count > 0 ? RegionCompatibility.Incompatible : RegionCompatibility.Unknown);
    }

    private static IEnumerable<string> ContextSegments(string title)
    {
        foreach (Match match in BracketRegex().Matches(title))
            yield return match.Groups[1].Value;

        var star = title.IndexOf(" * ", StringComparison.Ordinal);
        if (star >= 0)
            yield return title[(star + 3)..];

        foreach (Match match in SlashListRegex().Matches(title))
            yield return match.Value;

        var tail = KeyTailRegex().Match(title);
        if (tail.Success)
            yield return tail.Groups[1].Value;

        // Marketplace titles list delivery details after separators: "Hollow Knight STEAM•RU AUTO".
        var parts = SeparatorRegex().Split(title);
        for (var i = 1; i < parts.Length; i++)
            yield return parts[i];
    }

    private static bool IsCountryCode(string token, bool trusted)
    {
        // In titles only upper-case codes count: "IN", "US" and "AR" are also English words.
        if (!token.All(char.IsUpper) || (!trusted && AmbiguousCodes.Contains(token)))
            return false;
        try
        {
            _ = new RegionInfo(token);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsMostlyUpperCase(string text)
    {
        var letters = text.Count(char.IsLetter);
        return letters > 0 && text.Count(char.IsUpper) > letters * 0.7;
    }

    private static void AddLabel(List<string> labels, string label)
    {
        if (!labels.Contains(label, StringComparer.OrdinalIgnoreCase))
            labels.Add(label);
    }

    [GeneratedRegex(@"[\(\[]([^\)\]]{1,60})[\)\]]")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"\b[A-ZА-ЯЁ]{2,}(?:\s*/\s*[A-ZА-ЯЁ]{2,})+\b")]
    private static partial Regex SlashListRegex();

    [GeneratedRegex(@"\b(?:key|gift|account|ключ|гифт)\b\s*[-:]?\s*(.{1,40})$", RegexOptions.IgnoreCase)]
    private static partial Regex KeyTailRegex();

    [GeneratedRegex(@"[\p{L}]+")]
    private static partial Regex TokenRegex();

    // Not "&": it is part of country names ("Bosnia & Herzegovina").
    [GeneratedRegex(@"[,;/|+]")]
    private static partial Regex LabelListRegex();

    [GeneratedRegex(@"\b(GLOBAL|WORLDWIDE|EUROPE|LATAM|ROW|CIS|СНГ|EU)\b")]
    private static partial Regex CapsWordRegex();

    [GeneratedRegex(@"\+\s*(world|мир)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PlusWorldRegex();

    [GeneratedRegex(@"[•·|✅✔]")]
    private static partial Regex SeparatorRegex();
}
