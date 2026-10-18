using GamePriceHunter.Core.Currency;
using GamePriceHunter.Core.Geo;
using GamePriceHunter.Core.Models;

namespace GamePriceHunter.Core.Tests;

public class RegionTagsTests
{
    [Theory]
    [InlineData("Cyberpunk 2077 (PC) Steam Key GLOBAL", "SK", "Global", RegionCompatibility.Compatible)]
    [InlineData("The Witcher 3: Wild Hunt * RU/KZ/СНГ/TR/CN/AR/IN/BR", "SK", "RU/KZ/CIS/TR/CN/AR/IN/BR", RegionCompatibility.Incompatible)]
    [InlineData("The Witcher 3: Wild Hunt * RU/KZ/СНГ/TR/CN/AR/IN/BR", "KZ", "RU/KZ/CIS/TR/CN/AR/IN/BR", RegionCompatibility.Compatible)]
    [InlineData("Elden Ring (EU) Steam Key", "SK", "EU", RegionCompatibility.Compatible)]
    [InlineData("Elden Ring (EU) Steam Key", "US", "EU", RegionCompatibility.Incompatible)]
    [InlineData("Hades Steam Key ROW", "SK", "ROW", RegionCompatibility.Unknown)]
    [InlineData("Hollow Knight: Silksong / Steam AUTO / RU+WORLD", "SK", "Global", RegionCompatibility.Compatible)]
    [InlineData("Hollow Knight STEAM•RU AUTODELIVERY", "SK", "RU", RegionCompatibility.Incompatible)]
    [InlineData("Hollow Knight✅STEAM GIFT AUTO✅RU+World", "SK", "Global", RegionCompatibility.Compatible)]
    public void Recognizes_regions_in_titles(string title, string country, string label, RegionCompatibility compatibility)
    {
        var tag = RegionTags.FromTitle(title, country);
        Assert.NotNull(tag);
        Assert.Equal(label, tag.Label);
        Assert.Equal(compatibility, tag.Compatibility);
    }

    [Theory]
    [InlineData("Counter-Strike: Global Offensive")]
    [InlineData("Among Us")]
    [InlineData("Hollow Knight")]
    [InlineData("Final Fantasy VII Remake Intergrade (PC)")]
    public void Ignores_titles_without_region(string title) =>
        Assert.Null(RegionTags.FromTitle(title, "SK"));

    [Theory]
    [InlineData("Europe", "SK", RegionCompatibility.Compatible)]
    [InlineData("Region free", "SK", RegionCompatibility.Compatible)]
    [InlineData("United States", "SK", RegionCompatibility.Incompatible)]
    [InlineData("Rest of the World", "SK", RegionCompatibility.Unknown)]
    [InlineData("RU", "RU", RegionCompatibility.Compatible)]
    [InlineData("France", "FR", RegionCompatibility.Compatible)]
    [InlineData("Italy", "SK", RegionCompatibility.Incompatible)]
    [InlineData("RU+World", "SK", RegionCompatibility.Compatible)]
    [InlineData("XBOX Europe", "SK", RegionCompatibility.Compatible)]
    [InlineData("united states, canada", "US", RegionCompatibility.Compatible)]
    [InlineData("united states, canada", "CA", RegionCompatibility.Compatible)]
    [InlineData("united states, canada", "SK", RegionCompatibility.Incompatible)]
    [InlineData("europe, united kingdom", "GB", RegionCompatibility.Compatible)]
    [InlineData("RU/CIS", "KZ", RegionCompatibility.Compatible)]
    [InlineData("Bosnia & Herzegovina", "BA", RegionCompatibility.Compatible)]
    public void Parses_store_region_labels(string label, string country, RegionCompatibility expected) =>
        Assert.Equal(expected, RegionTags.FromLabel(label, country)?.Compatibility);

    [Theory]
    [InlineData("SK", "EUR")]
    [InlineData("UA", "UAH")]
    [InlineData("US", "USD")]
    public void Region_uses_the_country_currency(string country, string currency)
    {
        Assert.True(RegionDetector.TryCreate(country, out var region));
        Assert.Equal(currency, region.CurrencyCode);
    }

    [Theory]
    [InlineData("XX")]
    [InlineData("T1")]
    [InlineData("")]
    public void Rejects_pseudo_countries(string country) =>
        Assert.False(RegionDetector.TryCreate(country, out _));

    [Fact]
    public void Parses_cloudflare_trace() =>
        Assert.Equal("SK", RegionDetector.ParseTrace("fl=1\nip=1.2.3.4\nloc=SK\ntls=TLSv1.3\n"));
}

public class CurrencyTests
{
    private static readonly ExchangeRates Rates = new()
    {
        Base = "USD",
        Rates = new Dictionary<string, decimal> { ["EUR"] = 0.9m, ["RUB"] = 90m, ["UAH"] = 41m },
        Timestamp = DateTimeOffset.UtcNow,
        Source = "test",
    };

    [Fact]
    public void Converts_through_the_base_currency()
    {
        Assert.Equal(100m, Rates.Convert(1m, "EUR", "RUB"));
        Assert.Equal(1m, Rates.Convert(0.9m, "EUR", "USD"));
        Assert.Equal(5m, Rates.Convert(5m, "eur", "EUR"));
    }

    [Fact]
    public void Unknown_currency_cannot_be_converted() =>
        Assert.Null(Rates.Convert(1m, "XYZ", "USD"));

    [Fact]
    public void Formats_with_currency_symbols()
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo("en");
        Assert.Equal("€12.99", MoneyFormatter.Format(Money.Of(12.99m, "EUR"), culture));
        Assert.Equal("1,299.00 ₽", MoneyFormatter.Format(Money.Of(1299m, "RUB"), culture));
        Assert.Equal("¥1,980", MoneyFormatter.Format(Money.Of(1980m, "JPY"), culture));
    }
}
