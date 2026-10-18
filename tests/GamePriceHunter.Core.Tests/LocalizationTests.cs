using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GamePriceHunter.Core.Currency;
using GamePriceHunter.Core.Geo;
using GamePriceHunter.Core.Globalization;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Tests;

public class MoneyFormatterTests
{
    private const string Nbsp = " ";

    [Theory]
    [InlineData("en-US", 1234.56, "EUR", "€1,234.56")]
    [InlineData("en-US", 19.99, "USD", "$19.99")]
    [InlineData("en-US", 1499, "RUB", "₽1,499")]
    [InlineData("en-US", 10, "CAD", "CA$10.00")]
    [InlineData("en-US", 99, "SEK", "SEK" + Nbsp + "99.00")]
    [InlineData("ru-RU", 1234.56, "EUR", "1" + Nbsp + "234,56" + Nbsp + "€")]
    [InlineData("ru-RU", 19.99, "USD", "19,99" + Nbsp + "$")]
    [InlineData("ru-RU", 1499.49, "RUB", "1" + Nbsp + "499" + Nbsp + "₽")]
    [InlineData("ru-RU", 1499.5, "RUB", "1" + Nbsp + "500" + Nbsp + "₽")]
    [InlineData("uk-UA", 479, "UAH", "479" + Nbsp + "₴")]
    [InlineData("ru-KZ", 9990, "KZT", "9" + Nbsp + "990" + Nbsp + "₸")]
    [InlineData("de-DE", 1234.56, "EUR", "1.234,56" + Nbsp + "€")]
    [InlineData("de-DE", 19.99, "USD", "19,99" + Nbsp + "$")]
    [InlineData("fr-FR", 1234.56, "EUR", "1 234,56" + Nbsp + "€")]
    [InlineData("es-MX", 19.99, "USD", "US$19.99")]
    [InlineData("es-MX", 349, "MXN", "$349.00")]
    [InlineData("pt-BR", 19.99, "USD", "US$" + Nbsp + "19,99")]
    [InlineData("pt-BR", 19.99, "BRL", "R$" + Nbsp + "19,99")]
    [InlineData("tr-TR", 1234.56, "EUR", "€1.234,56")]
    [InlineData("pl-PL", 59.99, "PLN", "59,99" + Nbsp + "zł")]
    [InlineData("zh-CN", 68, "CNY", "¥68.00")]
    [InlineData("zh-CN", 1980, "JPY", "JP¥1,980")]
    [InlineData("ja-JP", 68, "CNY", "CN¥68.00")]
    public void Formats_prices_the_way_the_culture_writes_them(string culture, double amount, string currency, string expected) =>
        Assert.Equal(expected, MoneyFormatter.Format(Money.Of((decimal)amount, currency), CultureInfo.GetCultureInfo(culture)));

    [Fact]
    public void Writes_the_home_currency_with_the_culture_symbol() =>
        Assert.Equal(CultureInfo.GetCultureInfo("ja-JP").NumberFormat.CurrencySymbol + "1,980",
            MoneyFormatter.Format(Money.Of(1980m, "JPY"), CultureInfo.GetCultureInfo("ja-JP")));

    [Fact]
    public void Falls_back_when_the_windows_format_has_no_currency_symbol()
    {
        // Windows lets the user clear the symbol, and the format culture keeps the user's changes.
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        culture.NumberFormat.CurrencySymbol = string.Empty;
        Assert.Equal("$19.99", MoneyFormatter.Format(Money.Of(19.99m, "USD"), culture));
        Assert.Equal("€5.00", MoneyFormatter.Format(Money.Of(5m, "EUR"), culture));
    }

    [Fact]
    public void Formats_without_a_country_using_plain_symbols()
    {
        var culture = CultureInfo.InvariantCulture;
        Assert.Equal("€12.99", MoneyFormatter.Format(Money.Of(12.99m, "EUR"), culture));
        Assert.Equal("₽1,299", MoneyFormatter.Format(Money.Of(1299m, "RUB"), culture));
        Assert.Equal("¥1,980", MoneyFormatter.Format(Money.Of(1980m, "JPY"), culture));
    }

    [Theory]
    [InlineData("en-US", "−25%")]
    [InlineData("de-DE", "−25" + Nbsp + "%")]
    [InlineData("fr-FR", "−25" + Nbsp + "%")]
    [InlineData("tr-TR", "−%25")]
    public void Formats_discounts_in_the_culture_style(string culture, string expected) =>
        Assert.Equal(expected, MoneyFormatter.Discount(25, CultureInfo.GetCultureInfo(culture)));

    [Fact]
    public void Names_currencies_in_the_ui_language()
    {
        var currencies = MoneyFormatter.KnownCurrencies(CultureInfo.GetCultureInfo("ru")).ToDictionary(c => c.Code, c => c.Name);
        Assert.Equal("Евро", currencies["EUR"]);
        Assert.Equal("Российский рубль", currencies["RUB"]);
    }
}

public class FormatCultureTests
{
    [Theory]
    [InlineData("ru", "ru-RU", "SK", "ru-RU")] // Windows format in the UI language wins
    [InlineData("pt-BR", "pt-PT", "PT", "pt-PT")]
    [InlineData("en", "ru-RU", "DE", "en-DE")] // else the language as written in the user's country
    [InlineData("de", "en-US", "CH", "de-CH")]
    [InlineData("es", "en-US", "MX", "es-MX")]
    [InlineData("en", "ru-RU", "SK", "en-US")] // else the language's main format
    [InlineData("zh-Hans", "en-US", null, "zh-CN")]
    [InlineData("uk", "ru-RU", "SK", "uk-UA")]
    public void Picks_the_format_culture(string ui, string windows, string? country, string expected) =>
        Assert.Equal(expected, FormatCultures.Select(CultureInfo.GetCultureInfo(ui), CultureInfo.GetCultureInfo(windows), country).Name);
}

public class PluralRulesTests
{
    [Theory]
    [InlineData("en", 0, PluralCategory.Other)]
    [InlineData("en", 1, PluralCategory.One)]
    [InlineData("en", 2, PluralCategory.Other)]
    [InlineData("fr", 0, PluralCategory.One)]
    [InlineData("fr", 2, PluralCategory.Other)]
    [InlineData("pt-BR", 0, PluralCategory.One)]
    [InlineData("pt-PT", 0, PluralCategory.Other)]
    [InlineData("ru", 1, PluralCategory.One)]
    [InlineData("ru", 21, PluralCategory.One)]
    [InlineData("ru", 3, PluralCategory.Few)]
    [InlineData("ru", 22, PluralCategory.Few)]
    [InlineData("ru", 0, PluralCategory.Many)]
    [InlineData("ru", 11, PluralCategory.Many)]
    [InlineData("ru", 12, PluralCategory.Many)]
    [InlineData("ru", 25, PluralCategory.Many)]
    [InlineData("ru", 111, PluralCategory.Many)]
    [InlineData("uk", 2, PluralCategory.Few)]
    [InlineData("pl", 1, PluralCategory.One)]
    [InlineData("pl", 21, PluralCategory.Many)]
    [InlineData("pl", 22, PluralCategory.Few)]
    [InlineData("pl", 13, PluralCategory.Many)]
    [InlineData("ja", 1, PluralCategory.Other)]
    [InlineData("zh-Hans", 1, PluralCategory.Other)]
    public void Picks_the_plural_category(string culture, long count, PluralCategory expected) =>
        Assert.Equal(expected, PluralRules.Select(culture, count));
}

public class CountryNameTests
{
    [Theory]
    [InlineData("DE", "ru", "Германия")]
    [InlineData("DE", "ja", "ドイツ")]
    [InlineData("DE", "en", "Germany")]
    [InlineData("SK", "de", "Slowakei")]
    public void Names_countries_in_the_ui_language(string code, string ui, string expected) =>
        Assert.Equal(expected, Countries.DisplayName(code, CultureInfo.GetCultureInfo(ui)));

    [Fact]
    public void Sorts_countries_in_the_alphabet_of_the_ui_language()
    {
        var ru = CultureInfo.GetCultureInfo("ru");
        var names = Countries.Choices(ru).Select(c => c.Name).ToList();
        Assert.Contains("Германия", names);
        Assert.Equal(names.OrderBy(n => n, StringComparer.Create(ru, ignoreCase: true)), names);
    }
}

public class LanguageSettingTests
{
    [Theory]
    [InlineData(1, "en", "")] // English was the only language and saved for everyone
    [InlineData(1, "ru", "ru")]
    [InlineData(2, "en", "en")] // chosen in Settings
    public void Old_settings_follow_the_windows_language(int version, string saved, string expected)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gph-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, $$"""{ "Version": {{version}}, "Language": "{{saved}}" }""");
            var settings = new Settings.SettingsStore(path, Diagnostics.NullLogger.Instance).Load();
            Assert.Equal(expected, settings.Language);
            Assert.Equal(Settings.AppSettings.CurrentVersion, settings.Version);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void New_settings_follow_the_windows_language() =>
        Assert.Equal(string.Empty, new Settings.AppSettings().Language);
}

/// <summary>Store pages are parsed the same whatever the Windows format and the UI language are.</summary>
public class CultureIndependenceTests
{
    private static readonly string[] Prices = ["$19.99", "1 499,50 ₽", "€1.234,56", "12,99 €", "1,299 руб.", "₴479"];

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    [InlineData("ru-RU")]
    [InlineData("fr-FR")]
    public void Parses_and_matches_alike_in_every_culture(string culture)
    {
        var expected = Run();
        var (current, currentUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(expected, Run());
            Assert.True(new TitleMatcher("witcher").Score("THE WITCHER 3: WILD HUNT") > 0);
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (current, currentUi);
        }

        static List<string> Run() =>
        [
            .. Prices.Select(p => PriceParser.TryParse(p, "EUR", out var money) ? money.ToString() : "-"),
            TextNormalizer.Normalize("ISTANBUL Kids: İzmir"),
        ];
    }
}

/// <summary>Every translation has all strings, the plural forms its language needs and the same placeholders.</summary>
public partial class TranslationTests
{
    private static readonly string[] PluralSuffixes = ["_one", "_few", "_many"];

    public static TheoryData<string> Languages()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(LocalizationFolder, "Strings.*.resx"))
            data.Add(Path.GetFileNameWithoutExtension(file)["Strings.".Length..]);
        return data;
    }

    [Fact]
    public void All_languages_of_the_app_are_translated() =>
        Assert.Equal(12, Languages().Count);

    [Theory]
    [MemberData(nameof(Languages))]
    public void Translation_is_complete(string language)
    {
        var english = Load("Strings.resx");
        var translated = Load($"Strings.{language}.resx");
        var categories = Enumerable.Range(0, 1000)
            .Select(n => PluralRules.Select(language, n))
            .Where(c => c != PluralCategory.Other)
            .Distinct()
            .Select(c => "_" + c.ToString().ToLowerInvariant())
            .ToList();

        var baseKeys = english.Keys.Where(k => !PluralSuffixes.Any(k.EndsWith)).ToList();
        var pluralKeys = english.Keys.Where(k => k.EndsWith("_one", StringComparison.Ordinal)).Select(k => k[..^4]);
        var expected = baseKeys.Concat(pluralKeys.SelectMany(k => categories.Select(c => k + c))).ToHashSet();

        Assert.Empty(expected.Except(translated.Keys).Select(k => $"missing {k}"));
        Assert.Empty(translated.Keys.Except(expected).Select(k => $"unexpected {k}"));
        foreach (var (key, value) in translated)
        {
            var source = english[PluralSuffixes.FirstOrDefault(key.EndsWith) is { } suffix ? key[..^suffix.Length] : key];
            Assert.False(string.IsNullOrWhiteSpace(value), $"{key} is empty");
            Assert.True(Placeholders(source).SetEquals(Placeholders(value)), $"{key}: placeholders differ from English: {value}");
            // string.Format must accept it: no stray braces.
            _ = string.Format(CultureInfo.InvariantCulture, value, 1, 2, 3.5);
        }
    }

    private static string LocalizationFolder
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "GamePriceHunter.slnx")))
                    return Path.Combine(dir.FullName, "src", "GamePriceHunter.App", "Localization");
            }
            throw new DirectoryNotFoundException("Repository root not found.");
        }
    }

    private static Dictionary<string, string> Load(string fileName) =>
        XDocument.Load(Path.Combine(LocalizationFolder, fileName)).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty);

    private static HashSet<string> Placeholders(string text) => [.. PlaceholderRegex().Matches(text).Select(m => m.Value)];

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex PlaceholderRegex();
}
