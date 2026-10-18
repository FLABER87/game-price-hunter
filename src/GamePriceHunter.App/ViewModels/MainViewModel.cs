using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GamePriceHunter.App.Infrastructure;
using GamePriceHunter.App.Localization;
using GamePriceHunter.App.Services;
using GamePriceHunter.Core.Diagnostics;
using GamePriceHunter.Core.Geo;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Search;
using GamePriceHunter.Core.Settings;
using GamePriceHunter.Core.Text;

namespace GamePriceHunter.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const int MinQueryLength = 2;

    private readonly AppServices _services;
    private readonly List<OfferViewModel> _all = [];
    private readonly DispatcherTimer _suggestTimer;
    private readonly DispatcherTimer _filterTimer;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _suggestCts;
    private Task? _regionTask;
    private bool _suppressSuggestions;
    private bool _syncingProviders;

    private string _query = string.Empty;
    private string? _lastQuery;
    private string _quickFilter = string.Empty;
    private bool _isSearching;
    private bool _hasSearched;
    private bool _isSuggestionsOpen;
    private int _completedProviders;
    private int _totalProviders;
    private TimeSpan _lastElapsed;
    private SortOption _selectedSort;

    public MainViewModel(AppServices services)
    {
        _services = services;
        Update = new UpdateViewModel(services);
        Providers = new ObservableCollection<ProviderViewModel>(
            services.Search.Providers.Select(p => new ProviderViewModel(p.Info, services.Settings.IsProviderEnabled(p.Info), OnProviderToggled)));
        SortOptions = new ObservableCollection<SortOption>(BuildSortOptions());
        _selectedSort = SortOptions.FirstOrDefault(o => o.Mode == services.Settings.SortMode) ?? SortOptions[0];

        _suggestTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _suggestTimer.Tick += async (_, _) => await LoadSuggestionsAsync();
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            Rebuild();
        };

        // Fire-and-forget so a new search can start while the previous one is still running.
        SearchCommand = new RelayCommand(() => Fire(SearchAsync(Query)), () => Query.Trim().Length >= MinQueryLength);
        CancelCommand = new RelayCommand(CancelSearch, () => IsSearching);
        ClearQueryCommand = new RelayCommand(() => Query = string.Empty);
        ApplySuggestionCommand = new RelayCommand(p => { if (p is SuggestionViewModel s) Fire(ApplySuggestionAsync(s)); });
        OpenOfferCommand = new RelayCommand(p => Shell.OpenUrl((p as OfferViewModel)?.Url));
        CopyLinkCommand = new RelayCommand(p => Shell.CopyText((p as OfferViewModel)?.Url));
        CopyTitleCommand = new RelayCommand(p => Shell.CopyText((p as OfferViewModel)?.Title));
        SearchTitleCommand = new RelayCommand(p => { if (p is OfferViewModel o) Fire(SearchForAsync(TitleMatcher.CleanTitle(o.Title))); });
        OpenStoreCommand = new RelayCommand(p => Shell.OpenUrl((p as ProviderViewModel)?.Info.HomeUrl));
        RetryProviderCommand = new AsyncRelayCommand(p => p is ProviderViewModel provider ? RetryProviderAsync(provider) : Task.CompletedTask);
        EnableAllProvidersCommand = new RelayCommand(() => SetAllProviders(true));
        DisableAllProvidersCommand = new RelayCommand(() => SetAllProviders(false));
        OpenSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
        ResetFiltersCommand = new RelayCommand(ResetFilters);

        services.Currency.RatesChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(OnRatesChanged);
        Loc.Instance.LanguageChanged += (_, _) => OnLanguageChanged();
    }

    public event EventHandler? SettingsRequested;

    public UpdateViewModel Update { get; }

    public RangeObservableCollection<OfferViewModel> Offers { get; } = [];

    public ObservableCollection<ProviderViewModel> Providers { get; }

    public ObservableCollection<SuggestionViewModel> Suggestions { get; } = [];

    public ObservableCollection<SortOption> SortOptions { get; }

    public ICommand SearchCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ClearQueryCommand { get; }
    public ICommand ApplySuggestionCommand { get; }
    public ICommand OpenOfferCommand { get; }
    public ICommand CopyLinkCommand { get; }
    public ICommand CopyTitleCommand { get; }
    public ICommand SearchTitleCommand { get; }
    public ICommand OpenStoreCommand { get; }
    public ICommand RetryProviderCommand { get; }
    public ICommand EnableAllProvidersCommand { get; }
    public ICommand DisableAllProvidersCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand ResetFiltersCommand { get; }

    public string Query
    {
        get => _query;
        set
        {
            if (!Set(ref _query, value ?? string.Empty))
                return;
            // Text can change without keyboard input (paste, UI automation), so re-check commands explicitly.
            CommandManager.InvalidateRequerySuggested();
            if (!_suppressSuggestions)
            {
                _suggestTimer.Stop();
                _suggestTimer.Start();
            }
        }
    }

    public string QuickFilter
    {
        get => _quickFilter;
        set
        {
            if (!Set(ref _quickFilter, value ?? string.Empty))
                return;
            _filterTimer.Stop();
            _filterTimer.Start();
        }
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (Set(ref _isSearching, value))
                RaiseStateProperties();
        }
    }

    public bool HasSearched
    {
        get => _hasSearched;
        private set
        {
            if (Set(ref _hasSearched, value))
                RaiseStateProperties();
        }
    }

    public bool IsSuggestionsOpen
    {
        get => _isSuggestionsOpen;
        set => Set(ref _isSuggestionsOpen, value);
    }

    public double Progress => _totalProviders == 0 ? 0 : (double)_completedProviders / _totalProviders * 100;

    public bool IsWelcomeVisible => !HasSearched;

    public bool IsEmptyVisible => HasSearched && !IsSearching && Offers.Count == 0;

    public bool AreResultsVisible => Offers.Count > 0;

    public bool IsSearchingWithoutResults => IsSearching && Offers.Count == 0;

    /// <summary>"Nothing found" vs "everything is filtered out".</summary>
    public bool HasHiddenOffers => _all.Count > Offers.Count;

    public string EmptyTitle => _all.Count > 0 ? Loc.T("Empty_FilteredTitle") : Loc.F("Empty_NothingTitle", _lastQuery);

    public string EmptyText => _all.Count > 0 ? Loc.P("Empty_FilteredText", _all.Count) : Loc.T("Empty_NothingText");

    public string SummaryText
    {
        get
        {
            if (!HasSearched)
                return Loc.P("Summary_Ready", Providers.Count(p => p.IsEnabled));
            if (IsSearching)
                return Loc.P("Summary_Searching", _totalProviders, _completedProviders, _totalProviders);

            var stores = Offers.Select(o => o.StoreName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var text = Loc.F("Summary_Done", Loc.P("Summary_Offers", Offers.Count), Loc.P("Summary_FromStores", stores), _lastElapsed.TotalSeconds);
            return HasHiddenOffers ? text + Loc.F("Summary_Hidden", _all.Count - Offers.Count) : text;
        }
    }

    public SortOption SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (value is null || !Set(ref _selectedSort, value))
                return;
            _services.Settings.SortMode = value.Mode;
            _services.SaveSettingsSoon();
            Rebuild();
        }
    }

    // Filters (persisted)
    public bool ShowOfficial { get => Filters.ShowOfficial; set => SetFilter(v => Filters.ShowOfficial = v, value); }
    public bool ShowKeyShops { get => Filters.ShowKeyShops; set => SetFilter(v => Filters.ShowKeyShops = v, value); }
    public bool ShowMarketplaces { get => Filters.ShowMarketplaces; set => SetFilter(v => Filters.ShowMarketplaces = v, value); }
    public bool ShowGames { get => Filters.ShowGames; set => SetFilter(v => Filters.ShowGames = v, value); }
    public bool ShowDlc { get => Filters.ShowDlc; set => SetFilter(v => Filters.ShowDlc = v, value); }
    public bool ShowBundles { get => Filters.ShowBundles; set => SetFilter(v => Filters.ShowBundles = v, value); }
    public bool ShowOther { get => Filters.ShowOther; set => SetFilter(v => Filters.ShowOther = v, value); }
    public bool ShowConsoles { get => Filters.ShowConsoles; set => SetFilter(v => Filters.ShowConsoles = v, value); }
    public bool HideIncompatibleRegions { get => Filters.HideIncompatibleRegions; set => SetFilter(v => Filters.HideIncompatibleRegions = v, value); }

    public string RegionText
    {
        get
        {
            var region = _services.EffectiveRegion;
            return $"{Countries.DisplayName(region.CountryCode, Loc.Instance.Culture)} · {_services.DisplayCurrency}";
        }
    }

    public string RegionTooltip
    {
        get
        {
            if (_services.Settings.CountryOverride is not null)
                return Loc.T("Region_SourceManual");
            return _services.DetectedRegion?.Source switch
            {
                RegionSource.IpAddress => Loc.T("Region_SourceIp"),
                RegionSource.WindowsSettings => Loc.T("Region_SourceWindows"),
                _ => Loc.T("Region_SourceDetecting"),
            };
        }
    }

    public string RatesText
    {
        get
        {
            var rates = _services.Currency.Current;
            if (rates is null)
                return Loc.T("Rates_None");
            var age = DateTimeOffset.UtcNow - rates.Timestamp;
            var when = age.TotalHours < 1 ? Loc.T("Time_JustNow")
                : age.TotalHours < 48 ? Loc.F("Time_HoursAgo", (int)age.TotalHours)
                : Loc.P("Time_DaysAgo", (int)age.TotalDays);
            return Loc.F("Rates_Info", rates.Source, when);
        }
    }

    public bool AreRatesStale => _services.Currency.IsStale;

    public string VersionText => $"v{AppServices.Version}";

    public string WindowTitle => Loc.F("App_TitleWithVersion", AppServices.Version);

    private ResultFilters Filters => _services.Settings.Filters;

    public async Task InitializeAsync()
    {
        _regionTask = DetectRegionAsync();
        try
        {
            await Task.WhenAll(_regionTask, _services.Currency.InitializeAsync());
        }
        catch (Exception ex)
        {
            _services.Logger.Error("Startup initialization failed.", ex);
        }
        OnPropertyChanged(nameof(RatesText));
        OnPropertyChanged(nameof(AreRatesStale));
        OnPropertyChanged(nameof(SummaryText));

        await Update.CheckOnStartupAsync();
    }

    /// <summary>Shows recent searches when the search box is focused and empty.</summary>
    public void ShowHistory()
    {
        if (Query.Trim().Length > 0 || _services.Settings.SearchHistory.Count == 0)
            return;
        Suggestions.Clear();
        foreach (var item in _services.Settings.SearchHistory.Take(8))
            Suggestions.Add(new SuggestionViewModel(item, null, null, isHistory: true));
        IsSuggestionsOpen = true;
    }

    public void CloseSuggestions()
    {
        _suggestTimer.Stop();
        _suggestCts?.Cancel();
        IsSuggestionsOpen = false;
    }

    /// <summary>Called after the settings dialog was confirmed.</summary>
    public void OnSettingsChanged(bool regionChanged)
    {
        _syncingProviders = true;
        foreach (var provider in Providers)
            provider.IsEnabled = _services.Settings.IsProviderEnabled(provider.Info);
        _syncingProviders = false;

        OnPropertyChanged(nameof(RegionText));
        OnPropertyChanged(nameof(RegionTooltip));
        RecalculatePrices();

        if (regionChanged && _lastQuery is not null)
            Fire(SearchForAsync(_lastQuery));
    }

    private static async void Fire(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
        }
    }

    private Task SearchForAsync(string query)
    {
        _suppressSuggestions = true;
        Query = query;
        _suppressSuggestions = false;
        return SearchAsync(query);
    }

    private async Task SearchAsync(string rawQuery)
    {
        var query = rawQuery.Trim();
        if (query.Length < MinQueryLength)
            return;

        CloseSuggestions();
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();

        _services.Settings.AddToHistory(query);
        _services.SaveSettingsSoon();
        _lastQuery = query;
        _all.Clear();
        Offers.ReplaceAll([]);
        _totalProviders = 0;
        _completedProviders = 0;
        IsSearching = true;
        HasSearched = true;
        _ = RefreshRatesIfDueAsync();

        if (_regionTask is not null)
            await _regionTask;
        if (cts.IsCancellationRequested)
        {
            if (_searchCts == cts)
                IsSearching = false;
            return;
        }

        var options = _services.BuildSearchOptions();
        foreach (var provider in Providers)
        {
            if (options.Providers?.Contains(provider.Id) == true)
                provider.SetRunning();
            else
                provider.Reset();
        }

        _totalProviders = options.Providers?.Count ?? 0;
        RaiseProgress();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await foreach (var result in _services.Search.SearchAsync(query, _services.EffectiveRegion, options, cts.Token))
            {
                // A result read just before a newer search started must not leak into its list.
                if (!IsCurrentSearch(cts))
                    break;
                ApplyResult(result);
                _completedProviders++;
                RaiseProgress();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_searchCts == cts)
            {
                _lastElapsed = stopwatch.Elapsed;
                IsSearching = false;
                foreach (var provider in Providers.Where(p => p.IsRunning))
                    provider.Reset();
                RaiseStateProperties();
            }
        }
    }

    private async Task RetryProviderAsync(ProviderViewModel provider)
    {
        if (_lastQuery is null || IsSearching || !provider.IsEnabled)
            return;

        _all.RemoveAll(o => o.Offer.ProviderId == provider.Id);
        Rebuild();

        var cts = _searchCts = new CancellationTokenSource();
        var options = _services.BuildSearchOptions() with { Providers = new HashSet<string> { provider.Id } };
        provider.SetRunning();
        IsSearching = true;
        _totalProviders = 1;
        _completedProviders = 0;
        RaiseProgress();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await foreach (var result in _services.Search.SearchAsync(_lastQuery, _services.EffectiveRegion, options, cts.Token))
            {
                if (!IsCurrentSearch(cts))
                    break;
                ApplyResult(result);
                _completedProviders++;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_searchCts == cts)
            {
                _lastElapsed = stopwatch.Elapsed;
                IsSearching = false;
                if (provider.IsRunning)
                    provider.Reset();
                RaiseStateProperties();
            }
        }
    }

    private void CancelSearch()
    {
        _searchCts?.Cancel();
    }

    private bool IsCurrentSearch(CancellationTokenSource cts) => _searchCts == cts && !cts.IsCancellationRequested;

    private void ApplyResult(ProviderSearchResult result)
    {
        var displayCurrency = _services.DisplayCurrency;
        var added = new List<OfferViewModel>(result.Offers.Count);
        foreach (var offer in result.Offers)
        {
            var item = new OfferViewModel(offer);
            item.UpdatePrice(_services.Currency, displayCurrency);
            added.Add(item);
        }

        _all.AddRange(added);
        foreach (var item in added.Where(Passes))
            InsertSorted(item);

        Providers.FirstOrDefault(p => p.Id == result.Provider.Id)?.Apply(result, added.Count);
        UpdateBest();
        RaiseStateProperties();
    }

    private bool Passes(OfferViewModel item)
    {
        var offer = item.Offer;
        var kindVisible = offer.StoreKind switch
        {
            StoreKind.Official => Filters.ShowOfficial,
            StoreKind.KeyShop => Filters.ShowKeyShops,
            StoreKind.Marketplace => Filters.ShowMarketplaces,
            _ => true,
        };
        if (!kindVisible)
            return false;

        var typeVisible = offer.Type switch
        {
            OfferType.Dlc => Filters.ShowDlc,
            OfferType.Bundle => Filters.ShowBundles,
            OfferType.Other => Filters.ShowOther,
            _ => Filters.ShowGames,
        };
        if (!typeVisible)
            return false;

        if (!Filters.ShowConsoles && offer.Platform is Platform.Xbox or Platform.PlayStation or Platform.Nintendo)
            return false;

        if (Filters.HideIncompatibleRegions && offer.RegionCompatibility == RegionCompatibility.Incompatible)
            return false;

        if (Providers.FirstOrDefault(p => p.Id == offer.ProviderId) is { IsEnabled: false })
            return false;

        var filter = QuickFilter.Trim();
        return filter.Length == 0
            // Not the current culture: in Turkish "i" and "I" are different letters, and titles are mostly English.
            || offer.Title.Contains(filter, StringComparison.InvariantCultureIgnoreCase)
            || offer.StoreName.Contains(filter, StringComparison.InvariantCultureIgnoreCase);
    }

    private int Compare(OfferViewModel a, OfferViewModel b)
    {
        static int ByPrice(OfferViewModel x, OfferViewModel y) =>
            (x.SortPrice ?? decimal.MaxValue).CompareTo(y.SortPrice ?? decimal.MaxValue);

        var result = SelectedSort.Mode switch
        {
            SortMode.PriceLowToHigh => ByPrice(a, b),
            SortMode.PriceHighToLow => -ByPrice(a, b),
            SortMode.BestMatch => MatchTier(b).CompareTo(MatchTier(a)),
            SortMode.Discount => (b.DiscountPercent ?? -1).CompareTo(a.DiscountPercent ?? -1),
            SortMode.Store => string.Compare(a.StoreName, b.StoreName, StringComparison.CurrentCultureIgnoreCase),
            SortMode.Title => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase),
            _ => 0,
        };
        if (result != 0)
            return result;
        result = ByPrice(a, b);
        return result != 0 ? result : -a.Relevance.CompareTo(b.Relevance);
    }

    /// <summary>
    /// Coarse relevance groups for "Best match": the game itself (with or without store noise),
    /// close variants, then anything else that matched. Within a group offers sort by price.
    /// </summary>
    private static int MatchTier(OfferViewModel offer) => offer.Relevance switch
    {
        >= 0.95 => 3,
        >= 0.75 => 2,
        > 0.5 => 1,
        _ => 0,
    };

    private void InsertSorted(OfferViewModel item)
    {
        int low = 0, high = Offers.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (Compare(Offers[middle], item) <= 0)
                low = middle + 1;
            else
                high = middle;
        }
        Offers.Insert(low, item);
    }

    private void Rebuild()
    {
        var visible = _all.Where(Passes).ToList();
        visible.Sort(Compare);
        Offers.ReplaceAll(visible);
        UpdateBest();
        RaiseStateProperties();
    }

    /// <summary>Marks the cheapest offer among the best-matching, region-compatible games.</summary>
    private void UpdateBest()
    {
        var candidates = Offers
            .Where(o => o.SortPrice is not null
                        && o.Offer.Type is OfferType.Game or OfferType.Unknown
                        && o.RegionCompatibility != RegionCompatibility.Incompatible)
            .ToList();

        OfferViewModel? best = null;
        if (candidates.Count > 0)
        {
            var topRelevance = candidates.Max(o => o.Relevance);
            best = candidates
                .Where(o => o.Relevance >= topRelevance - 0.05)
                .OrderBy(o => o.SortPrice)
                .First();
        }

        foreach (var offer in _all)
            offer.IsBest = ReferenceEquals(offer, best);
    }

    private void RecalculatePrices()
    {
        var currency = _services.DisplayCurrency;
        foreach (var offer in _all)
            offer.UpdatePrice(_services.Currency, currency);
        OnPropertyChanged(nameof(RegionText));
        Rebuild();
    }

    /// <summary>
    /// The app may stay open for days, so every search renews rates that got old, without waiting
    /// for them: <see cref="OnRatesChanged"/> re-prices the offers when new ones arrive.
    /// </summary>
    private async Task RefreshRatesIfDueAsync()
    {
        OnPropertyChanged(nameof(RatesText));
        OnPropertyChanged(nameof(AreRatesStale));
        try
        {
            await _services.Currency.RefreshIfDueAsync();
        }
        catch (Exception ex)
        {
            _services.Logger.Warn($"Exchange rate refresh failed: {ex.Message}");
        }
    }

    private void OnRatesChanged()
    {
        RecalculatePrices();
        OnPropertyChanged(nameof(RatesText));
        OnPropertyChanged(nameof(AreRatesStale));
    }

    private void OnLanguageChanged()
    {
        var mode = SelectedSort.Mode;
        SortOptions.Clear();
        foreach (var option in BuildSortOptions())
            SortOptions.Add(option);
        _selectedSort = SortOptions.First(o => o.Mode == mode);
        OnPropertyChanged(nameof(SelectedSort));

        foreach (var provider in Providers)
            provider.RefreshTexts();
        foreach (var offer in _all)
        {
            offer.RefreshTexts();
            offer.UpdatePrice(_services.Currency, _services.DisplayCurrency);
        }
        OnPropertyChanged(nameof(RegionText));
        OnPropertyChanged(nameof(RegionTooltip));
        OnPropertyChanged(nameof(RatesText));
        OnPropertyChanged(nameof(WindowTitle));
        RaiseStateProperties();
    }

    private async Task LoadSuggestionsAsync()
    {
        _suggestTimer.Stop();
        var query = Query.Trim();
        if (query.Length < MinQueryLength)
        {
            IsSuggestionsOpen = false;
            return;
        }

        _suggestCts?.Cancel();
        var cts = _suggestCts = new CancellationTokenSource();
        try
        {
            if (_regionTask is not null)
                await _regionTask;
            var items = await _services.Suggestions.GetAsync(query, _services.EffectiveRegion, cts.Token);
            if (cts.IsCancellationRequested || Query.Trim() != query)
                return;

            Suggestions.Clear();
            foreach (var history in _services.Settings.SearchHistory
                         .Where(h => h.Contains(query, StringComparison.InvariantCultureIgnoreCase) && !string.Equals(h, query, StringComparison.InvariantCultureIgnoreCase))
                         .Take(3))
            {
                Suggestions.Add(new SuggestionViewModel(history, null, null, isHistory: true));
            }
            foreach (var item in items.Take(8))
                Suggestions.Add(new SuggestionViewModel(item.Title, item.ImageUrl, item.Price, isHistory: false));

            IsSuggestionsOpen = Suggestions.Count > 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.Text.Json.JsonException)
        {
            _services.Logger.Warn($"Suggestions failed: {ex.Message}");
        }
    }

    private Task ApplySuggestionAsync(SuggestionViewModel suggestion) => SearchForAsync(suggestion.Title);

    private async Task DetectRegionAsync()
    {
        try
        {
            _services.DetectedRegion = await _services.RegionDetector.DetectAsync();
            _services.Logger.Info($"Region: {_services.DetectedRegion.Region.CountryCode} ({_services.DetectedRegion.Source}).");
        }
        catch (Exception ex)
        {
            _services.Logger.Error("Region detection failed.", ex);
        }
        Loc.Instance.SetCountry(_services.FormatCountry);
        OnPropertyChanged(nameof(RegionText));
        OnPropertyChanged(nameof(RegionTooltip));
    }

    private void OnProviderToggled(ProviderViewModel provider)
    {
        if (_syncingProviders)
            return;
        _services.Settings.SetProviderEnabled(provider.Info, provider.IsEnabled);
        _services.SaveSettingsSoon();
        Rebuild();
    }

    private void SetAllProviders(bool enabled)
    {
        _syncingProviders = true;
        foreach (var provider in Providers)
        {
            // Key-based sources stay off until configured.
            if (enabled && provider.Info.RequiresApiKey && string.IsNullOrEmpty(_services.Settings.GetApiKey(provider.Id)))
                continue;
            provider.IsEnabled = enabled;
            _services.Settings.SetProviderEnabled(provider.Info, enabled);
        }
        _syncingProviders = false;
        _services.SaveSettingsSoon();
        Rebuild();
    }

    private void SetFilter(Action<bool> apply, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        apply(value);
        OnPropertyChanged(propertyName);
        _services.SaveSettingsSoon();
        Rebuild();
    }

    private void ResetFilters()
    {
        var defaults = new ResultFilters();
        _services.Settings.Filters = defaults;
        QuickFilter = string.Empty;
        foreach (var name in new[] { nameof(ShowOfficial), nameof(ShowKeyShops), nameof(ShowMarketplaces), nameof(ShowGames), nameof(ShowDlc), nameof(ShowBundles), nameof(ShowOther), nameof(ShowConsoles), nameof(HideIncompatibleRegions) })
            OnPropertyChanged(name);
        _services.SaveSettingsSoon();
        Rebuild();
    }

    private void RaiseProgress()
    {
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(SummaryText));
    }

    private void RaiseStateProperties()
    {
        OnPropertyChanged(nameof(IsWelcomeVisible));
        OnPropertyChanged(nameof(IsEmptyVisible));
        OnPropertyChanged(nameof(AreResultsVisible));
        OnPropertyChanged(nameof(IsSearchingWithoutResults));
        OnPropertyChanged(nameof(HasHiddenOffers));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(SummaryText));
        CommandManager.InvalidateRequerySuggested();
    }

    private static IEnumerable<SortOption> BuildSortOptions() =>
    [
        new(SortMode.BestMatch, Loc.T("Sort_BestMatch")),
        new(SortMode.PriceLowToHigh, Loc.T("Sort_PriceLowToHigh")),
        new(SortMode.PriceHighToLow, Loc.T("Sort_PriceHighToLow")),
        new(SortMode.Discount, Loc.T("Sort_Discount")),
        new(SortMode.Store, Loc.T("Sort_Store")),
        new(SortMode.Title, Loc.T("Sort_Title")),
    ];
}
