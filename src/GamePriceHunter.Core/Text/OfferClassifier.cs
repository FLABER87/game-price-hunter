using System.Text.RegularExpressions;
using GamePriceHunter.Core.Models;

namespace GamePriceHunter.Core.Text;

/// <summary>Guesses offer type and activation platform from a store title.</summary>
public static partial class OfferClassifier
{
    public static OfferType DetectType(string title)
    {
        if (OtherRegex().IsMatch(title))
            return OfferType.Other;
        if (DlcRegex().IsMatch(title))
            return OfferType.Dlc;
        if (BundleRegex().IsMatch(title))
            return OfferType.Bundle;
        return OfferType.Game;
    }

    public static Platform DetectPlatform(string title)
    {
        // Order matters: "Xbox ... Windows" is Xbox, "Steam" mentioned with "Epic" is ambiguous → first hit wins.
        if (XboxRegex().IsMatch(title)) return Platform.Xbox;
        if (PlayStationRegex().IsMatch(title)) return Platform.PlayStation;
        if (NintendoRegex().IsMatch(title)) return Platform.Nintendo;
        if (SteamRegex().IsMatch(title)) return Platform.Steam;
        if (GogRegex().IsMatch(title)) return Platform.Gog;
        if (EpicRegex().IsMatch(title)) return Platform.Epic;
        if (UbisoftRegex().IsMatch(title)) return Platform.Ubisoft;
        if (EaRegex().IsMatch(title)) return Platform.Ea;
        if (BattleNetRegex().IsMatch(title)) return Platform.BattleNet;
        if (RockstarRegex().IsMatch(title)) return Platform.Rockstar;
        return Platform.Unknown;
    }

    /// <summary>Maps a store's DRM/platform label ("steam", "Ubisoft Connect", "drm-free") to a platform.</summary>
    public static Platform ParsePlatform(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return Platform.Unknown;
        if (DrmFreeRegex().IsMatch(label))
            return Platform.DrmFree;
        var detected = DetectPlatform(label);
        return detected == Platform.Unknown ? Platform.Other : detected;
    }

    [GeneratedRegex(@"\b(dlc|soundtrack|ost|season pass|expansion pass|expansion|add[- ]?on|upgrade|costume|skin pack|artbook|art book|cosmetic|booster pack|currency pack|pre-?order bonus|bonus content|дополнени\w*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DlcRegex();

    [GeneratedRegex(@"\b(bundle|collection|trilogy|anthology|franchise pack|complete pack|\d+\s*games?\s*in\s*1|\d+-in-1|комплект|сборник)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BundleRegex();

    [GeneratedRegex(@"\b(gift ?card|wallet|top[- ]?up|accounts?|аккаунт\w*|rental|аренд\w*|subscription|game pass|membership|random( \d+)?( steam)? keys?|случайн\w*|пополнени\w*|офлайн|offline)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OtherRegex();

    [GeneratedRegex(@"\b(xbox|microsoft store|windows (10|11) store)\b", RegexOptions.IgnoreCase)]
    private static partial Regex XboxRegex();

    [GeneratedRegex(@"\b(psn|playstation|ps[45])\b", RegexOptions.IgnoreCase)]
    private static partial Regex PlayStationRegex();

    [GeneratedRegex(@"\b(nintendo|switch eshop|eshop)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NintendoRegex();

    [GeneratedRegex(@"\bsteam\b", RegexOptions.IgnoreCase)]
    private static partial Regex SteamRegex();

    [GeneratedRegex(@"\bgog(\.com)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex GogRegex();

    [GeneratedRegex(@"\bepic( games)?( store)?\b|\begs\b", RegexOptions.IgnoreCase)]
    private static partial Regex EpicRegex();

    [GeneratedRegex(@"\b(uplay|ubisoft( connect)?|ubi connect)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UbisoftRegex();

    [GeneratedRegex(@"\b(origin|ea app|ea play)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EaRegex();

    [GeneratedRegex(@"\b(battle\.?net|blizzard)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BattleNetRegex();

    [GeneratedRegex(@"\brockstar( games)?( launcher)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex RockstarRegex();

    [GeneratedRegex(@"drm[- ]?free", RegexOptions.IgnoreCase)]
    private static partial Regex DrmFreeRegex();
}
