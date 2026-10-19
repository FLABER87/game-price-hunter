using GamePriceHunter.App.Infrastructure;
using GamePriceHunter.App.Localization;
using GamePriceHunter.Core.Currency;
using GamePriceHunter.Core.Giveaways;
using GamePriceHunter.Core.Models;

namespace GamePriceHunter.App.ViewModels;

/// <summary>A giveaway card.</summary>
public sealed class GiveawayViewModel : ObservableObject
{
    private static readonly TimeSpan EndingSoon = TimeSpan.FromHours(24);

    private readonly string _sourceNames;
    private string? _worthText;
    private string _timeText = string.Empty;
    private bool _isEndingSoon;
    private bool _isUpcoming;

    public GiveawayViewModel(Giveaway giveaway, DateTimeOffset now, string sourceNames)
    {
        Giveaway = giveaway;
        _sourceNames = sourceNames;
        StoreIconUrl = StoreIcons.For(giveaway.StoreUrl);
        RefreshTimes(now);
    }

    public Giveaway Giveaway { get; }

    public string Title => Giveaway.Title;

    public string StoreName => Giveaway.StoreName;

    public string StoreInitial => Giveaway.StoreName.Length > 0 ? Giveaway.StoreName[..1].ToUpperInvariant() : "?";

    public string? StoreIconUrl { get; }

    public string? ImageUrl => Giveaway.ImageUrl;

    public string Url => Giveaway.Url;

    public string? KindText => Giveaway.Kind switch
    {
        GiveawayKind.Dlc => Loc.T("Type_Dlc"),
        GiveawayKind.Beta => Loc.T("Giveaway_Beta"),
        _ => null,
    };

    /// <summary>Where the game is activated, hidden when it just repeats the store ("Steam" on Steam).</summary>
    public string? PlatformText
    {
        get
        {
            if (Giveaway.Platform is Platform.Unknown or Platform.Other)
                return null;
            var text = Loc.T($"Platform_{Giveaway.Platform}");
            return Giveaway.StoreName.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? null : text;
        }
    }

    /// <summary>The usual price, crossed out next to "Free".</summary>
    public string? WorthText
    {
        get => _worthText;
        private set => Set(ref _worthText, value);
    }

    public string TimeText
    {
        get => _timeText;
        private set => Set(ref _timeText, value);
    }

    /// <summary>"Free on Steam", or "Free on Steam soon" for an announced giveaway.</summary>
    public string HeadlineText => Loc.F(_isUpcoming ? "Giveaway_SoonFreeOn" : "Giveaway_FreeOn", Giveaway.StoreName);

    /// <summary>Less than a day left.</summary>
    public bool IsEndingSoon
    {
        get => _isEndingSoon;
        private set => Set(ref _isEndingSoon, value);
    }

    public string Tooltip
    {
        get
        {
            var lines = new List<string> { Giveaway.Title, Loc.F("Giveaway_FoundBy", _sourceNames) };
            if (!Giveaway.IsRegionChecked)
                lines.Add(Loc.T("Giveaway_RegionUnchecked"));
            return string.Join("\n", lines);
        }
    }

    public void UpdatePrice(CurrencyService currency, string displayCurrency)
    {
        WorthText = Giveaway.Worth is { } worth
            ? MoneyFormatter.Format(currency.Convert(worth, displayCurrency) ?? worth, Loc.Instance.FormatCulture)
            : null;
    }

    public void RefreshTimes(DateTimeOffset now)
    {
        var culture = Loc.Instance.FormatCulture;
        string Day(DateTime date) => date.ToString(culture.DateTimeFormat.MonthDayPattern, culture);

        var upcoming = Giveaway.StartsAt > now;
        if (upcoming != _isUpcoming)
        {
            _isUpcoming = upcoming;
            OnPropertyChanged(nameof(HeadlineText));
        }

        if (Giveaway.StartsAt is { } starts && starts > now)
        {
            var local = starts.ToLocalTime().DateTime;
            TimeText = Loc.F("Giveaway_Starts", $"{Day(local)}, {local.ToString(culture.DateTimeFormat.ShortTimePattern, culture)}");
            IsEndingSoon = false;
        }
        else if (Giveaway.EndsAt is { } ends && Giveaway.EndsOnDateOnly)
        {
            // Only the day is known, and it is the source's day, not a local one.
            TimeText = Loc.F("Giveaway_Until", Day(ends.UtcDateTime.Date));
            IsEndingSoon = false;
        }
        else if (Giveaway.EndsAt is { } exact)
        {
            var left = exact - now;
            TimeText = Loc.F("Giveaway_EndsIn", Duration(left < TimeSpan.Zero ? TimeSpan.Zero : left));
            IsEndingSoon = left < EndingSoon;
        }
        else
        {
            TimeText = Loc.T("Giveaway_NoEnd");
            IsEndingSoon = false;
        }
    }

    public void RefreshTexts(DateTimeOffset now)
    {
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(PlatformText));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(HeadlineText));
        RefreshTimes(now);
    }

    private static string Duration(TimeSpan span) =>
        span.TotalDays >= 1 ? Loc.F("Time_DaysHours", (int)span.TotalDays, span.Hours)
        : span.TotalHours >= 1 ? Loc.F("Time_HoursMinutes", (int)span.TotalHours, span.Minutes)
        : Loc.F("Time_Minutes", Math.Max(1, span.Minutes));
}

/// <summary>A giveaway source in the sidebar, with what its last refresh brought.</summary>
public sealed class GiveawaySourceViewModel(ProviderInfo info) : ObservableObject
{
    private ProviderStatus _status = ProviderStatus.Pending;
    private int _count;
    private string? _error;
    private TimeSpan _elapsed;
    private int _found;

    public ProviderInfo Info { get; } = info;

    public string Id => Info.Id;

    public string Name => Info.Name;

    public string? IconUrl { get; } = StoreIcons.For(info.HomeUrl);

    public string Initial => Info.Name[..1].ToUpperInvariant();

    public string KindText => Info.IsAggregator ? Loc.T("Kind_GiveawayAggregator") : Loc.T("Kind_Official");

    public bool IsRunning => _status == ProviderStatus.Running;

    public bool IsProblem => _status is ProviderStatus.Failed or ProviderStatus.Blocked or ProviderStatus.TimedOut;

    public bool HasCount => _status == ProviderStatus.Succeeded;

    /// <summary>Giveaways from this source that the list shows.</summary>
    public int Count
    {
        get => _count;
        set => Set(ref _count, value);
    }

    public string StatusTooltip
    {
        get
        {
            var note = ProviderViewModel.NoteText(Info);
            var header = note is null ? Info.Name : $"{Info.Name}\n{note}";
            var line = _status switch
            {
                ProviderStatus.Running => Loc.T("Giveaways_Loading"),
                ProviderStatus.Succeeded => Loc.P("Giveaways_SourceFound", _found, _found, _elapsed.TotalSeconds),
                ProviderStatus.Blocked => Loc.T("Provider_Blocked"),
                ProviderStatus.TimedOut => Loc.T("Provider_TimedOut"),
                ProviderStatus.Failed => Loc.F("Provider_Failed", _error),
                _ => null,
            };
            return line is null ? header : $"{header}\n\n{line}";
        }
    }

    public void SetRunning() => SetStatus(ProviderStatus.Running);

    public void Reset() => SetStatus(ProviderStatus.Pending);

    public void Apply(GiveawaySourceResult result)
    {
        _found = result.Count;
        _error = result.Error;
        _elapsed = result.Elapsed;
        SetStatus(result.Status);
    }

    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(StatusTooltip));
    }

    private void SetStatus(ProviderStatus status)
    {
        _status = status;
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsProblem));
        OnPropertyChanged(nameof(HasCount));
        OnPropertyChanged(nameof(StatusTooltip));
    }
}
