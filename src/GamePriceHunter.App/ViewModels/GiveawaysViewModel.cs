using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using GamePriceHunter.App.Infrastructure;
using GamePriceHunter.App.Localization;
using GamePriceHunter.App.Services;
using GamePriceHunter.Core.Giveaways;
using GamePriceHunter.Core.Giveaways.Sources;
using GamePriceHunter.Core.Models;
using GamePriceHunter.Core.Settings;

namespace GamePriceHunter.App.ViewModels;

/// <summary>The "Giveaways" page and the strip of free games on the welcome screen.</summary>
public sealed class GiveawaysViewModel : ObservableObject
{
    private const int HighlightCount = 4;

    private readonly AppServices _services;
    private readonly Dictionary<Giveaway, GiveawayViewModel> _items = new(ReferenceEqualityComparer.Instance);
    private GiveawayFeed? _feed;
    private CancellationTokenSource? _refreshCts;
    private bool _isLoading;
    private int _gameCount;
    private int _hiddenCount;

    public GiveawaysViewModel(AppServices services)
    {
        _services = services;
        Sources = new ObservableCollection<GiveawaySourceViewModel>(services.Giveaways.Sources.Select(s => new GiveawaySourceViewModel(s.Info)));
        RefreshCommand = new RelayCommand(() => Fire(RefreshAsync(force: true)), () => !IsLoading);
        OpenCommand = new RelayCommand(p => Shell.OpenUrl((p as GiveawayViewModel)?.Url));
        CopyLinkCommand = new RelayCommand(p => Shell.CopyText((p as GiveawayViewModel)?.Url));
        CopyTitleCommand = new RelayCommand(p => Shell.CopyText((p as GiveawayViewModel)?.Title));
        OpenAttributionCommand = new RelayCommand(() => Shell.OpenUrl(GamerPowerGiveawaySource.SiteUrl));

        // Countdowns tick, giveaways end and announced ones start while the app stays open.
        var clock = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        clock.Tick += (_, _) => Rebuild();
        clock.Start();
    }

    /// <summary>Running giveaways that pass the filters.</summary>
    public RangeObservableCollection<GiveawayViewModel> Items { get; } = [];

    public RangeObservableCollection<GiveawayViewModel> Upcoming { get; } = [];

    /// <summary>The first running games, for the welcome screen.</summary>
    public RangeObservableCollection<GiveawayViewModel> Highlights { get; } = [];

    public ObservableCollection<GiveawaySourceViewModel> Sources { get; }

    public ICommand RefreshCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand CopyLinkCommand { get; }
    public ICommand CopyTitleCommand { get; }
    public ICommand OpenAttributionCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (Set(ref _isLoading, value))
            {
                RaiseStateProperties();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>Running games, whatever the filters: the number on the mode switch.</summary>
    public int GameCount
    {
        get => _gameCount;
        private set
        {
            if (Set(ref _gameCount, value))
                OnPropertyChanged(nameof(HasGames));
        }
    }

    public bool HasGames => GameCount > 0;

    public bool AreHighlightsVisible => Highlights.Count > 0;

    public bool IsListVisible => Items.Count > 0 || Upcoming.Count > 0;

    public bool HasUpcoming => Upcoming.Count > 0;

    public bool IsLoadingWithoutItems => IsLoading && !IsListVisible;

    /// <summary>No source answered and nothing was saved from before.</summary>
    public bool IsFailedVisible => !IsLoading && _feed is { Giveaways.Count: 0 } feed && feed.Sources.All(s => s.Status != ProviderStatus.Succeeded);

    public bool IsEmptyVisible => !IsLoading && _feed is not null && !IsListVisible && !IsFailedVisible;

    public string EmptyText => _hiddenCount > 0 ? Loc.P("Giveaways_HiddenText", _hiddenCount) : Loc.T("Giveaways_EmptyText");

    public string SummaryText
    {
        get
        {
            if (_feed is null)
                return IsLoading ? Loc.T("Giveaways_Loading") : string.Empty;
            var summary = Loc.F("Giveaways_Summary", Items.Count);
            var age = _services.Giveaways.Now - _feed.FetchedAt;
            var when = age.TotalMinutes < 1 ? Loc.T("Time_JustNow")
                : age.TotalHours < 1 ? Loc.F("Time_MinutesAgo", (int)age.TotalMinutes)
                : age.TotalHours < 48 ? Loc.F("Time_HoursAgo", (int)age.TotalHours)
                : Loc.P("Time_DaysAgo", (int)age.TotalDays);
            return $"{summary} · {Loc.F("Giveaways_Updated", when)}";
        }
    }

    // Filters (persisted)
    public bool ShowGames { get => Filters.ShowGames; set => SetFilter(v => Filters.ShowGames = v, value); }
    public bool ShowDlc { get => Filters.ShowDlc; set => SetFilter(v => Filters.ShowDlc = v, value); }
    public bool ShowBetas { get => Filters.ShowBetas; set => SetFilter(v => Filters.ShowBetas = v, value); }

    private GiveawayFilters Filters => _services.Settings.GiveawayFilters;

    /// <summary>Shows the saved list at once, then refreshes it if it is old. Call once the region is known.</summary>
    public Task StartAsync() => RefreshAsync(force: false);

    /// <summary>The page was opened: a list that got old while the app stayed open is refreshed.</summary>
    public void EnsureFresh() => Fire(RefreshAsync(force: false));

    public void OnRegionChanged() => Fire(RefreshAsync(force: false));

    public void UpdatePrices()
    {
        foreach (var item in _items.Values)
            item.UpdatePrice(_services.Currency, _services.DisplayCurrency);
    }

    public void RefreshTexts()
    {
        foreach (var source in Sources)
            source.RefreshTexts();
        foreach (var item in _items.Values)
            item.RefreshTexts(_services.Giveaways.Now);
        UpdatePrices();
        RaiseStateProperties();
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

    private async Task RefreshAsync(bool force)
    {
        var region = _services.EffectiveRegion;
        var countryChanged = _feed is null || !string.Equals(_feed.CountryCode, region.CountryCode, StringComparison.OrdinalIgnoreCase);
        if (countryChanged)
        {
            _feed = _services.Giveaways.LoadCached(region.CountryCode);
            ApplySourceResults();
            Rebuild();
        }
        if (!force && _feed is not null && _services.Giveaways.IsFresh(_feed))
            return;
        // The same list is on its way already.
        if (!force && !countryChanged && IsLoading)
            return;

        _refreshCts?.Cancel();
        var cts = _refreshCts = new CancellationTokenSource();
        IsLoading = true;
        foreach (var source in Sources)
            source.SetRunning();
        try
        {
            var feed = await _services.Giveaways.RefreshAsync(region, _services.Settings.ProviderTimeout, cts.Token);
            if (_refreshCts != cts)
                return;
            // When every source fails, the list from before stays, with the failures shown next to the sources.
            _feed = _feed is not null && feed.Sources.All(s => s.Status != ProviderStatus.Succeeded) ? _feed with { Sources = feed.Sources } : feed;
        }
        finally
        {
            if (_refreshCts == cts)
                IsLoading = false;
        }
        ApplySourceResults();
        Rebuild();
    }

    private void ApplySourceResults()
    {
        foreach (var source in Sources)
        {
            var result = _feed?.Sources.FirstOrDefault(r => r.SourceId == source.Id);
            if (result is null)
                source.Reset();
            else
                source.Apply(result);
        }
    }

    private bool Passes(Giveaway giveaway) => giveaway.Kind switch
    {
        GiveawayKind.Dlc => Filters.ShowDlc,
        GiveawayKind.Beta => Filters.ShowBetas,
        _ => Filters.ShowGames,
    };

    private void Rebuild()
    {
        var now = _services.Giveaways.Now;
        var all = _feed?.Giveaways ?? [];
        var current = GiveawayService.Current(all, now);
        var upcoming = GiveawayService.Upcoming(all, now);

        Sync(Items, current.Where(Passes).ToList());
        Sync(Upcoming, upcoming.Where(Passes).ToList());
        Sync(Highlights, current.Where(g => g.Kind == GiveawayKind.Game).Take(HighlightCount).ToList());
        GameCount = current.Count(g => g.Kind == GiveawayKind.Game);
        _hiddenCount = current.Count + upcoming.Count - Items.Count - Upcoming.Count;

        // Forget giveaways that left every list, so the cache doesn't grow while the app stays open.
        var shown = Items.Concat(Upcoming).Concat(Highlights).Select(i => i.Giveaway).ToHashSet<Giveaway>(ReferenceEqualityComparer.Instance);
        foreach (var gone in _items.Keys.Where(g => !shown.Contains(g)).ToList())
            _items.Remove(gone);
        foreach (var item in _items.Values)
            item.RefreshTimes(now);

        foreach (var source in Sources)
            source.Count = Items.Count(i => i.Giveaway.SourceId == source.Id || i.Giveaway.OtherSourceIds.Contains(source.Id));
        RaiseStateProperties();
    }

    /// <summary>Replaces the list only when it changed, so the selection and scroll position survive the clock ticks.</summary>
    private void Sync(RangeObservableCollection<GiveawayViewModel> target, IReadOnlyList<Giveaway> giveaways)
    {
        if (target.Count == giveaways.Count && target.Select(i => i.Giveaway).SequenceEqual<Giveaway>(giveaways, ReferenceEqualityComparer.Instance))
            return;
        target.ReplaceAll(giveaways.Select(Item));
    }

    private GiveawayViewModel Item(Giveaway giveaway)
    {
        if (_items.TryGetValue(giveaway, out var item))
            return item;
        item = new GiveawayViewModel(giveaway, _services.Giveaways.Now, SourceNames(giveaway));
        item.UpdatePrice(_services.Currency, _services.DisplayCurrency);
        return _items[giveaway] = item;
    }

    private string SourceNames(Giveaway giveaway) =>
        string.Join(", ", giveaway.OtherSourceIds.Prepend(giveaway.SourceId)
            .Select(id => Sources.FirstOrDefault(s => s.Id == id)?.Name ?? id));

    private void SetFilter(Action<bool> apply, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        apply(value);
        OnPropertyChanged(propertyName);
        _services.SaveSettingsSoon();
        Rebuild();
    }

    private void RaiseStateProperties()
    {
        OnPropertyChanged(nameof(AreHighlightsVisible));
        OnPropertyChanged(nameof(IsListVisible));
        OnPropertyChanged(nameof(HasUpcoming));
        OnPropertyChanged(nameof(IsLoadingWithoutItems));
        OnPropertyChanged(nameof(IsFailedVisible));
        OnPropertyChanged(nameof(IsEmptyVisible));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(SummaryText));
    }
}
