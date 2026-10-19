namespace GamePriceHunter.Core.Settings;

/// <summary>
/// Locations of user data. A file named "portable" next to the executable switches the app
/// to keep everything in a "Data" folder beside it.
/// </summary>
public sealed class AppPaths
{
    public const string AppFolderName = "GamePriceHunter";

    private AppPaths(string settingsDirectory, string cacheDirectory, bool isPortable)
    {
        SettingsDirectory = settingsDirectory;
        CacheDirectory = cacheDirectory;
        IsPortable = isPortable;
    }

    public string SettingsDirectory { get; }

    public string CacheDirectory { get; }

    public bool IsPortable { get; }

    public string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");

    public string RatesCacheFile => Path.Combine(CacheDirectory, "exchange-rates.json");

    public string GiveawaysCacheFile => Path.Combine(CacheDirectory, "giveaways.json");

    public string ImageCacheDirectory => Path.Combine(CacheDirectory, "images");

    public string LogDirectory => Path.Combine(CacheDirectory, "logs");

    /// <summary>Where downloaded installers wait to be run; emptied on the next start.</summary>
    public static string UpdateDownloadDirectory => Path.Combine(Path.GetTempPath(), AppFolderName + "-Update");

    public static AppPaths Resolve()
    {
        var baseDirectory = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(baseDirectory, "portable")))
        {
            var data = Path.Combine(baseDirectory, "Data");
            return new AppPaths(data, Path.Combine(data, "Cache"), isPortable: true);
        }

        return new AppPaths(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName),
            isPortable: false);
    }
}
