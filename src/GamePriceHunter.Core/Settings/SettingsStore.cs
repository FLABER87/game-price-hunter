using System.Text.Json;
using System.Text.Json.Serialization;
using GamePriceHunter.Core.Diagnostics;

namespace GamePriceHunter.Core.Settings;

/// <summary>Loads and atomically saves <see cref="AppSettings"/>.</summary>
public sealed class SettingsStore(string path, IAppLogger logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock _gate = new();

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
                if (settings is not null)
                {
                    settings.Upgrade();
                    return settings;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.Error("Settings file is unreadable; starting with defaults.", ex);
            TryBackupCorruptFile();
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.Error("Could not save settings.", ex);
            }
        }
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            File.Copy(path, path + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
