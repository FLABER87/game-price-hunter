using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using GamePriceHunter.App.Infrastructure;
using GamePriceHunter.App.Localization;
using GamePriceHunter.App.Services;
using GamePriceHunter.Core.Currency;
using GamePriceHunter.Core.Geo;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Settings;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.App.ViewModels;

public sealed record Choice<T>(T Value, string Name);

/// <summary>Editable copy of the settings; nothing is applied until <see cref="Save"/>.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private Choice<string> _language;
    private Choice<AppTheme> _theme;
    private Choice<string?> _country;
    private Choice<string?> _currency;
    private Choice<MatchMode> _matchMode;
    private Choice<int> _maxResults;
    private int _timeoutSeconds;
    private bool _checkForUpdates;

    public SettingsViewModel(AppServices services, UpdateViewModel update)
    {
        _services = services;
        Update = update;
        var settings = services.Settings;

        var ui = Loc.Instance.Culture;
        Languages = [new Choice<string>(string.Empty, Loc.F("Settings_AutoValue", Loc.WindowsLanguage.NativeName)), .. Loc.Languages.Select(l => new Choice<string>(l.Code, l.NativeName))];
        _language = Languages.FirstOrDefault(l => string.Equals(l.Value, settings.Language, StringComparison.OrdinalIgnoreCase)) ?? Languages[0];

        Themes =
        [
            new(AppTheme.System, Loc.T("Theme_System")),
            new(AppTheme.Dark, Loc.T("Theme_Dark")),
            new(AppTheme.Light, Loc.T("Theme_Light")),
        ];
        _theme = Themes.First(t => t.Value == settings.Theme);

        var detected = services.DetectedRegion?.Region;
        var autoCountry = detected is null ? Loc.T("Settings_Auto") : Loc.F("Settings_AutoValue", Core.Geo.Countries.DisplayName(detected.CountryCode, ui));
        Countries = [new Choice<string?>(null, autoCountry), .. Core.Geo.Countries.Choices(ui).Select(c => new Choice<string?>(c.Code, c.Name))];
        _country = Countries.FirstOrDefault(c => c.Value == settings.CountryOverride) ?? Countries[0];

        var rates = services.Currency.Current;
        var currencies = MoneyFormatter.KnownCurrencies(ui).Where(c => rates is null || rates.Supports(c.Code));
        Currencies = [new Choice<string?>(null, Loc.F("Settings_AutoValue", services.EffectiveRegion.CurrencyCode)), .. currencies.Select(c => new Choice<string?>(c.Code, $"{c.Code} — {c.Name}"))];
        _currency = Currencies.FirstOrDefault(c => c.Value == settings.CurrencyOverride) ?? Currencies[0];

        MatchModes =
        [
            new(Core.Text.MatchMode.AllWords, Loc.T("Match_AllWords")),
            new(Core.Text.MatchMode.Exact, Loc.T("Match_Exact")),
        ];
        _matchMode = MatchModes.First(m => m.Value == settings.MatchMode);

        int[] maxResultsOptions = [20, 40, 60, 100, 200];
        var maxResults = maxResultsOptions.OrderBy(v => Math.Abs(v - settings.MaxResultsPerProvider)).First();
        MaxResultsChoices = maxResultsOptions.Select(v => new Choice<int>(v, v.ToString(Loc.Instance.FormatCulture))).ToList();
        _maxResults = MaxResultsChoices.First(c => c.Value == maxResults);

        _timeoutSeconds = Math.Clamp(settings.ProviderTimeoutSeconds, 5, 60);
        _checkForUpdates = settings.CheckForUpdates;

        Providers = new ObservableCollection<ProviderSettingViewModel>(
            services.Search.Providers.Select(p => new ProviderSettingViewModel(p.Info, settings.IsProviderEnabled(p.Info), settings.GetApiKey(p.Info.Id))));

        OpenDataFolderCommand = new RelayCommand(() => Shell.OpenFolder(services.Paths.SettingsDirectory));
        OpenLogsFolderCommand = new RelayCommand(() => Shell.OpenFolder(services.Paths.LogDirectory));
        ClearCacheCommand = new RelayCommand(ClearCache);
        OpenLinkCommand = new RelayCommand(p => Shell.OpenUrl(p as string));
    }

    public IReadOnlyList<Choice<string>> Languages { get; }
    public IReadOnlyList<Choice<AppTheme>> Themes { get; }
    public IReadOnlyList<Choice<string?>> Countries { get; }
    public IReadOnlyList<Choice<string?>> Currencies { get; }
    public IReadOnlyList<Choice<MatchMode>> MatchModes { get; }
    public IReadOnlyList<Choice<int>> MaxResultsChoices { get; }
    public ObservableCollection<ProviderSettingViewModel> Providers { get; }

    /// <summary>Shared with the main window, so an update found here shows up in its banner too.</summary>
    public UpdateViewModel Update { get; }

    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenLogsFolderCommand { get; }
    public ICommand ClearCacheCommand { get; }
    public ICommand OpenLinkCommand { get; }

    public Choice<string> Language { get => _language; set => Set(ref _language, value); }
    public Choice<AppTheme> Theme { get => _theme; set => Set(ref _theme, value); }
    public Choice<string?> Country { get => _country; set => Set(ref _country, value); }
    public Choice<string?> Currency { get => _currency; set => Set(ref _currency, value); }
    public Choice<MatchMode> MatchMode { get => _matchMode; set => Set(ref _matchMode, value); }
    public Choice<int> MaxResults { get => _maxResults; set => Set(ref _maxResults, value); }

    public int TimeoutSeconds
    {
        get => _timeoutSeconds;
        set
        {
            if (Set(ref _timeoutSeconds, Math.Clamp(value, 5, 60)))
                OnPropertyChanged(nameof(TimeoutText));
        }
    }

    public string TimeoutText => Loc.F("Settings_TimeoutValue", TimeoutSeconds);

    public bool CheckForUpdates
    {
        get => _checkForUpdates;
        set => Set(ref _checkForUpdates, value);
    }

    public string VersionText => Loc.F("About_Version", AppServices.Version);

    public string DataLocationText => _services.Paths.IsPortable ? Loc.T("About_Portable") : _services.Paths.SettingsDirectory;

    private string _cacheMessage = string.Empty;

    public string CacheMessage
    {
        get => _cacheMessage;
        private set => Set(ref _cacheMessage, value);
    }

    /// <summary>Writes the edited values back. Returns true when the search region changed.</summary>
    public bool Save()
    {
        var settings = _services.Settings;
        var oldRegion = _services.EffectiveRegion;

        settings.Language = Language.Value;
        settings.Theme = Theme.Value;
        settings.CountryOverride = Country.Value;
        settings.CurrencyOverride = Currency.Value;
        settings.MatchMode = MatchMode.Value;
        settings.MaxResultsPerProvider = MaxResults.Value;
        settings.ProviderTimeoutSeconds = TimeoutSeconds;
        settings.CheckForUpdates = CheckForUpdates;

        foreach (var provider in Providers)
        {
            settings.SetApiKey(provider.Info.Id, provider.ApiKey);
            settings.SetProviderEnabled(provider.Info, provider.IsEnabled);
        }

        _services.SaveSettingsNow();
        _services.Search.ClearCache();
        return oldRegion != _services.EffectiveRegion;
    }

    private void ClearCache()
    {
        _services.Search.ClearCache();
        var removed = 0;
        try
        {
            if (Directory.Exists(_services.Paths.ImageCacheDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(_services.Paths.ImageCacheDirectory))
                {
                    File.Delete(file);
                    removed++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Logger.Log(Core.Diagnostics.LogLevel.Warning, "Could not clear the image cache.", ex);
        }
        CacheMessage = Loc.P("Settings_CacheCleared", removed);
    }
}

public sealed class ProviderSettingViewModel(ProviderInfo info, bool isEnabled, string? apiKey) : ObservableObject
{
    private bool _isEnabled = isEnabled;
    private string? _apiKey = apiKey;

    public ProviderInfo Info { get; } = info;

    public string Name => Info.Name;

    public string? Note => ProviderViewModel.NoteText(Info);

    public string KindText => Info.IsAggregator ? Loc.T("Kind_Aggregator") : Loc.T($"Kind_{Info.Kind}");

    public string? IconUrl { get; } = StoreIcons.For(info.HomeUrl);

    public bool RequiresApiKey => Info.RequiresApiKey;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => Set(ref _isEnabled, value);
    }

    public string? ApiKey
    {
        get => _apiKey;
        set
        {
            if (!Set(ref _apiKey, value))
                return;
            // Entering a key is a clear signal the user wants the source on.
            if (!string.IsNullOrWhiteSpace(value))
                IsEnabled = true;
        }
    }
}
