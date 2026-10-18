using GamePriceHunter.App.Infrastructure;
using GamePriceHunter.App.Localization;
using GamePriceHunter.Core.Models;

namespace GamePriceHunter.App.ViewModels;

/// <summary>A store in the sidebar: on/off switch plus the status of the current search.</summary>
public sealed class ProviderViewModel : ObservableObject
{
    private readonly Action<ProviderViewModel> _onEnabledChanged;
    private bool _isEnabled;
    private ProviderStatus _status = ProviderStatus.Pending;
    private int _offerCount;
    private string? _error;
    private TimeSpan _elapsed;

    public ProviderViewModel(ProviderInfo info, bool isEnabled, Action<ProviderViewModel> onEnabledChanged)
    {
        Info = info;
        _isEnabled = isEnabled;
        _onEnabledChanged = onEnabledChanged;
        IconUrl = StoreIcons.For(info.HomeUrl);
    }

    public ProviderInfo Info { get; }

    public string Id => Info.Id;

    public string Name => Info.Name;

    public string? IconUrl { get; }

    public string Initial => Info.Name[..1].ToUpperInvariant();

    public StoreKind Kind => Info.Kind;

    public string KindText => Info.IsAggregator ? Loc.T("Kind_Aggregator") : Loc.T($"Kind_{Info.Kind}");

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (Set(ref _isEnabled, value))
                _onEnabledChanged(this);
        }
    }

    public ProviderStatus Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value))
                RaiseStatus();
        }
    }

    public bool IsRunning => Status == ProviderStatus.Running;

    public bool IsProblem => Status is ProviderStatus.Failed or ProviderStatus.Blocked or ProviderStatus.TimedOut;

    public bool IsSkipped => Status == ProviderStatus.Skipped;

    public bool HasCount => Status == ProviderStatus.Succeeded;

    public int OfferCount
    {
        get => _offerCount;
        private set => Set(ref _offerCount, value);
    }

    public string StatusTooltip
    {
        get
        {
            var header = Info.Note is null ? Info.Name : $"{Info.Name}\n{Info.Note}";
            var line = Status switch
            {
                ProviderStatus.Running => Loc.T("Provider_Searching"),
                ProviderStatus.Succeeded => Loc.F("Provider_Found", OfferCount, _elapsed.TotalSeconds),
                ProviderStatus.Blocked => Loc.T("Provider_Blocked"),
                ProviderStatus.TimedOut => Loc.T("Provider_TimedOut"),
                ProviderStatus.Failed => Loc.F("Provider_Failed", _error),
                ProviderStatus.Skipped => _error,
                _ => null,
            };
            return line is null ? header : $"{header}\n\n{line}";
        }
    }

    public void SetRunning()
    {
        OfferCount = 0;
        _error = null;
        Status = ProviderStatus.Running;
    }

    public void Reset()
    {
        OfferCount = 0;
        _error = null;
        Status = ProviderStatus.Pending;
    }

    public void Apply(ProviderSearchResult result, int visibleCount)
    {
        _error = result.Error;
        _elapsed = result.Elapsed;
        OfferCount = visibleCount;
        Status = result.Status;
        RaiseStatus();
    }

    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(KindText));
        RaiseStatus();
    }

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsProblem));
        OnPropertyChanged(nameof(IsSkipped));
        OnPropertyChanged(nameof(HasCount));
        OnPropertyChanged(nameof(StatusTooltip));
    }
}

public sealed class SuggestionViewModel(string title, string? imageUrl, string? priceText, bool isHistory)
{
    public string Title { get; } = title;

    public string? ImageUrl { get; } = imageUrl;

    public string? PriceText { get; } = priceText;

    public bool IsHistory { get; } = isHistory;
}

public sealed record SortOption(Core.Settings.SortMode Mode, string Name);
