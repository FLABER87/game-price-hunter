using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.Core.Tests;

public class TextNormalizerTests
{
    [Theory]
    [InlineData("Assassin’s Creed® II: Deluxe", "assassins creed ii deluxe")]
    [InlineData("  Half-Life   2 ", "half life 2")]
    [InlineData("Ori & the Blind Forest", "ori and the blind forest")]
    [InlineData("Pokémon™", "pokemon")]
    [InlineData("Ведьмак 3: Дикая Охота", "ведьмак 3 дикая охота")]
    public void Normalize_produces_comparable_text(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.Normalize(input));
}

public class TitleMatcherTests
{
    [Theory]
    [InlineData("witcher", "The Witcher 3: Wild Hunt")]
    [InlineData("witcher 3", "The Witcher 3: Wild Hunt - Complete Edition")]
    [InlineData("witcher 3", "The Witcher III Wild Hunt")]
    [InlineData("witch", "The Witcher 3: Wild Hunt")]
    [InlineData("hollowknight", "Hollow Knight")]
    [InlineData("half life", "Half-Life 2")]
    [InlineData("the witcher", "Witcher 3 (PC) Steam Key")]
    [InlineData("gta 5", "Grand Theft Auto V: GTA 5 Premium")]
    public void Matches_relevant_titles(string query, string title) =>
        Assert.True(new TitleMatcher(query).Score(title) > 0);

    [Theory]
    [InlineData("witcher 3", "The Witcher 2: Assassins of Kings")]
    [InlineData("witcher", "Train Sim World 6: MP15DC Switcher Loco Add-On")]
    [InlineData("dark souls 3", "Dark Souls II")]
    [InlineData("assassin creed", "Assassination Classroom: Creed")]
    [InlineData("knight hollow", "Knightfall: Hollow Earth")]
    public void Rejects_unrelated_titles(string query, string title) =>
        Assert.Equal(0, new TitleMatcher(query).Score(title));

    [Fact]
    public void Exact_title_scores_higher_than_dlc()
    {
        var matcher = new TitleMatcher("hollow knight");
        Assert.True(matcher.Score("Hollow Knight") > matcher.Score("Hollow Knight - Official Soundtrack"));
    }

    [Fact]
    public void Store_noise_counts_as_nearly_exact()
    {
        var matcher = new TitleMatcher("cyberpunk 2077");
        Assert.True(matcher.Score("Cyberpunk 2077 (PC) Steam Key GLOBAL") >= 0.95);
    }

    [Fact]
    public void Exact_mode_requires_the_same_title()
    {
        var matcher = new TitleMatcher("hollow knight", MatchMode.Exact);
        Assert.Equal(1.0, matcher.Score("Hollow Knight (PC) Steam Key GLOBAL"));
        Assert.Equal(0, matcher.Score("Hollow Knight: Silksong"));
    }

    [Theory]
    [InlineData("The Witcher 3 (PC) Steam Key GLOBAL", "The Witcher 3")]
    [InlineData("Elden Ring [EU] - Steam Gift", "Elden Ring")]
    [InlineData("Hollow Knight | Steam Gift PC | AUTO", "Hollow Knight")]
    [InlineData("HOLLOW KNIGHT · WORLDWIDE", "HOLLOW KNIGHT")]
    [InlineData("Hollow Knight - Gods & Nightmares✅STEAM GIFT AUTO✅RU+World", "Hollow Knight - Gods & Nightmares")]
    [InlineData("Grand Theft Auto V - Premium Edition", "Grand Theft Auto V - Premium Edition")]
    public void CleanTitle_removes_store_noise(string title, string expected) =>
        Assert.Equal(expected, TitleMatcher.CleanTitle(title));

    [Fact]
    public void Dlc_with_store_noise_is_not_an_exact_match()
    {
        var matcher = new TitleMatcher("hollow knight");
        Assert.True(matcher.Score("Hollow Knight - Gods & Nightmares✅STEAM GIFT AUTO✅RU+World") < 0.95);
        Assert.True(matcher.Score("Hollow Knight | Steam Gift PC | AUTO") >= 0.95);
    }
}

public class PriceParserTests
{
    [Theory]
    [InlineData("€12.99", null, 12.99, "EUR")]
    [InlineData("13,49€", null, 13.49, "EUR")]
    [InlineData("1 299,00 ₽", null, 1299.00, "RUB")]
    [InlineData("3395 ₽", null, 3395, "RUB")]
    [InlineData("$1,299.99", null, 1299.99, "USD")]
    [InlineData("199 грн", null, 199, "UAH")]
    [InlineData("R$ 59,90", null, 59.90, "BRL")]
    [InlineData("24.99 USD", null, 24.99, "USD")]
    [InlineData("1.299", "EUR", 1299, "EUR")]
    [InlineData("12,5", "EUR", 12.5, "EUR")]
    [InlineData("&#8364; 17.99", null, 17.99, "EUR")]
    public void Parses_formatted_prices(string text, string? fallback, double amount, string currency)
    {
        var decoded = Html.Decode(text);
        Assert.True(PriceParser.TryParse(decoded, fallback, out var money));
        Assert.Equal((decimal)amount, money.Amount);
        Assert.Equal(currency, money.Currency);
    }

    [Theory]
    [InlineData("СКОРО")]
    [InlineData("—")]
    [InlineData("")]
    public void Rejects_non_prices(string text) =>
        Assert.False(PriceParser.TryParse(text, "RUB", out _));
}

public class OfferClassifierTests
{
    [Theory]
    [InlineData("Hollow Knight - Official Soundtrack", OfferType.Dlc)]
    [InlineData("The Witcher 3: Wild Hunt - Expansion Pass", OfferType.Dlc)]
    [InlineData("Batman: Arkham Collection", OfferType.Bundle)]
    [InlineData("Steam Gift Card 20 EUR", OfferType.Other)]
    [InlineData("Elden Ring [Offline activation]", OfferType.Other)]
    [InlineData("Random Steam Key", OfferType.Other)]
    [InlineData("Aim To Get The Witcher 3 - Wild Hunt: Random 1 Steam Key", OfferType.Other)]
    [InlineData("Cyberpunk 2077 - Steam (offline)", OfferType.Other)]
    [InlineData("Elden Ring - Account Rental", OfferType.Other)]
    [InlineData("Assassin's Creed Origins", OfferType.Game)]
    [InlineData("The Banner Saga", OfferType.Game)]
    public void Detects_offer_type(string title, OfferType expected) =>
        Assert.Equal(expected, OfferClassifier.DetectType(title));

    [Theory]
    [InlineData("Cyberpunk 2077 (PC) Steam Key GLOBAL", Platform.Steam)]
    [InlineData("Forza Horizon 5 (Xbox Series X|S / Windows 10) Xbox Live Key", Platform.Xbox)]
    [InlineData("Far Cry 6 Ubisoft Connect Key EUROPE", Platform.Ubisoft)]
    [InlineData("Assassin's Creed Origins", Platform.Unknown)]
    public void Detects_platform(string title, Platform expected) =>
        Assert.Equal(expected, OfferClassifier.DetectPlatform(title));
}
