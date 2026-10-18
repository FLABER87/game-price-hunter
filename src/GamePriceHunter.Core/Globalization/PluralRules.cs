namespace GamePriceHunter.Core.Globalization;

/// <summary>CLDR plural categories; whole numbers in the app's languages only use these four.</summary>
public enum PluralCategory
{
    One,
    Few,
    Many,
    Other,
}

/// <summary>
/// Which plural form a count takes in a language ("1 offer / 2 offers", "1 предложение / 2 предложения /
/// 5 предложений"), after the CLDR rules for whole numbers. Languages not listed use the English rule.
/// </summary>
public static class PluralRules
{
    /// <param name="culture">Culture name of the UI language: "ru", "pt-BR", "zh-Hans".</param>
    public static PluralCategory Select(string culture, long count)
    {
        var n = Math.Abs(count);
        var mod10 = n % 10;
        var mod100 = n % 100;
        var language = culture.Split('-')[0].ToLowerInvariant();

        switch (language)
        {
            case "ja" or "zh" or "ko" or "th" or "vi" or "id":
                return PluralCategory.Other;

            // 0 and 1 are both singular ("0 offre", "0 oferta" in Brazil; Portugal says "0 ofertas")
            case "fr":
            case "pt" when !culture.Equals("pt-PT", StringComparison.OrdinalIgnoreCase):
                return n <= 1 ? PluralCategory.One : PluralCategory.Other;

            case "ru" or "uk" or "be":
                if (mod10 == 1 && mod100 != 11)
                    return PluralCategory.One;
                return IsFew(mod10, mod100) ? PluralCategory.Few : PluralCategory.Many;

            case "pl":
                if (n == 1)
                    return PluralCategory.One;
                return IsFew(mod10, mod100) ? PluralCategory.Few : PluralCategory.Many;

            case "cs" or "sk":
                return n == 1 ? PluralCategory.One : n is >= 2 and <= 4 ? PluralCategory.Few : PluralCategory.Other;

            default:
                return n == 1 ? PluralCategory.One : PluralCategory.Other;
        }
    }

    private static bool IsFew(long mod10, long mod100) => mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14;
}
