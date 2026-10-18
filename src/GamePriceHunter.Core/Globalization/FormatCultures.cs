using System.Globalization;

namespace GamePriceHunter.Core.Globalization;

/// <summary>Picks the culture the app writes prices and numbers in.</summary>
public static class FormatCultures
{
    /// <summary>
    /// The Windows regional format when it is in the UI language, with the user's own changes to it; else
    /// the UI language as written in the user's country when Windows knows that pair (German in Switzerland
    /// is de-CH); else the main format of the UI language (de-DE for German, zh-CN for Simplified Chinese).
    /// </summary>
    /// <param name="uiCulture">The app's UI language.</param>
    /// <param name="windowsFormat">The Windows regional format (CultureInfo.CurrentCulture at startup).</param>
    /// <param name="countryCode">The user's country, if known.</param>
    public static CultureInfo Select(CultureInfo uiCulture, CultureInfo windowsFormat, string? countryCode)
    {
        var language = uiCulture.TwoLetterISOLanguageName;
        if (!windowsFormat.IsNeutralCulture && windowsFormat.Name.Length > 0
            && string.Equals(windowsFormat.TwoLetterISOLanguageName, language, StringComparison.OrdinalIgnoreCase))
        {
            return windowsFormat;
        }

        if (countryCode is { Length: 2 } && TryGetPredefined($"{language}-{countryCode.ToUpperInvariant()}") is { } local)
            return local;

        return CultureInfo.CreateSpecificCulture(uiCulture.Name);
    }

    private static CultureInfo? TryGetPredefined(string name)
    {
        try
        {
            return CultureInfo.GetCultureInfo(name, predefinedOnly: true);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }
}
