using GamePriceHunter.App.Infrastructure;
using GamePriceHunter.App.Localization;
using GamePriceHunter.Core.Currency;
using GamePriceHunter.Core.Models;

namespace GamePriceHunter.App.ViewModels;

/// <summary>One row of the results list.</summary>
public sealed class OfferViewModel : ObservableObject
{
    private string _priceText = string.Empty;
    private string? _originalPriceText;
    private string? _regularPriceText;
    private bool _isBest;

    public OfferViewModel(Offer offer)
    {
        Offer = offer;
        StoreIconUrl = StoreIcons.For(offer.StoreUrl);
    }

    public Offer Offer { get; }

    public string Title => Offer.Title;

    public string StoreName => Offer.StoreName;

    public string StoreInitial => Offer.StoreName.Length > 0 ? Offer.StoreName[..1].ToUpperInvariant() : "?";

    public string? StoreIconUrl { get; }

    public string? ImageUrl => Offer.ImageUrl;

    public string Url => Offer.Url;

    public StoreKind Kind => Offer.StoreKind;

    public string KindText => Loc.T($"Kind_{Offer.StoreKind}");

    /// <summary>Activation platform, hidden when it just repeats the store ("Steam" on Steam).</summary>
    public string? PlatformText
    {
        get
        {
            if (Offer.Platform is Platform.Unknown)
                return null;
            var text = Loc.T($"Platform_{Offer.Platform}");
            return Offer.StoreName.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? null : text;
        }
    }

    public string? TypeText => Offer.Type is OfferType.Dlc or OfferType.Bundle or OfferType.Other ? Loc.T($"Type_{Offer.Type}") : null;

    /// <summary>The key region: codes as they are ("EU/CIS"), the words "Global" and "Asia" translated.</summary>
    public string? RegionText => Offer.Region is { } region
        ? string.Join("/", region.Split('/').Select(part => part switch
        {
            "Global" => Loc.T("Region_Global"),
            "Asia" => Loc.T("Region_Asia"),
            _ => part,
        }))
        : null;

    public RegionCompatibility RegionCompatibility => Offer.RegionCompatibility;

    public string RegionTooltip => Offer.RegionCompatibility switch
    {
        RegionCompatibility.Compatible => Loc.F("Region_Compatible", RegionText),
        RegionCompatibility.Incompatible => Loc.F("Region_Incompatible", RegionText),
        _ => Loc.F("Region_Unknown", RegionText),
    };

    public string? Seller => Offer.Seller;

    /// <summary>The seller's rating as the marketplace writes it: stars with one decimal ("4.9"), Plati's points rounded ("4 344").</summary>
    public string? SellerRatingText => Offer.SellerRating is { } rating
        ? rating.OutOf is null
            ? Math.Round(rating.Value, MidpointRounding.AwayFromZero).ToString("N0", Loc.Instance.FormatCulture)
            : rating.Value.ToString("0.0", Loc.Instance.FormatCulture)
        : null;

    public string? SellerRatingTooltip => Offer.SellerRating is { } rating
        ? rating.OutOf is { } outOf
            ? Loc.F("Offer_SellerRatingOutOf", SellerRatingText, outOf.ToString("0.#", Loc.Instance.FormatCulture))
            : Loc.F("Offer_SellerRatingPoints", Offer.StoreName)
        : null;

    public string? SalesText => Offer.Sales is { } sales
        ? Loc.F("Offer_Sales", sales.Count.ToString("N0", Loc.Instance.FormatCulture) + (sales.AtLeast ? "+" : ""))
        : null;

    public int? DiscountPercent => Offer.DiscountPercent;

    public string? DiscountText => Offer.DiscountPercent is { } percent ? MoneyFormatter.Discount(percent, Loc.Instance.FormatCulture) : null;

    public double Relevance => Offer.Relevance;

    /// <summary>Price in the display currency, used for sorting; null when it cannot be converted.</summary>
    public decimal? SortPrice { get; private set; }

    public string PriceText
    {
        get => _priceText;
        private set => Set(ref _priceText, value);
    }

    /// <summary>The store's own price when it differs from the display currency.</summary>
    public string? OriginalPriceText
    {
        get => _originalPriceText;
        private set => Set(ref _originalPriceText, value);
    }

    public string? RegularPriceText
    {
        get => _regularPriceText;
        private set => Set(ref _regularPriceText, value);
    }

    public bool IsBest
    {
        get => _isBest;
        set => Set(ref _isBest, value);
    }

    public void UpdatePrice(CurrencyService currency, string displayCurrency)
    {
        var converted = currency.Convert(Offer.Price, displayCurrency);
        SortPrice = converted?.Amount;
        var culture = Loc.Instance.FormatCulture;
        PriceText = MoneyFormatter.Format(converted ?? Offer.Price, culture);
        OriginalPriceText = converted is not null && !string.Equals(Offer.Price.Currency, displayCurrency, StringComparison.OrdinalIgnoreCase)
            ? MoneyFormatter.Format(Offer.Price, culture)
            : null;

        if (Offer.RegularPrice is { } regular && Offer.DiscountPercent is not null)
        {
            var regularConverted = converted is null ? regular : currency.Convert(regular, displayCurrency) ?? regular;
            RegularPriceText = MoneyFormatter.Format(regularConverted, culture);
        }
        else
        {
            RegularPriceText = null;
        }
    }

    /// <summary>Re-reads localized labels after a language switch.</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(PlatformText));
        OnPropertyChanged(nameof(TypeText));
        OnPropertyChanged(nameof(RegionText));
        OnPropertyChanged(nameof(RegionTooltip));
        OnPropertyChanged(nameof(SellerRatingText));
        OnPropertyChanged(nameof(SellerRatingTooltip));
        OnPropertyChanged(nameof(SalesText));
        OnPropertyChanged(nameof(DiscountText));
    }
}

public static class StoreIcons
{
    /// <summary>64px favicon for a store home page.</summary>
    public static string? For(string? homeUrl) =>
        Uri.TryCreate(homeUrl, UriKind.Absolute, out var uri)
            ? $"https://www.google.com/s2/favicons?domain={uri.Host}&sz=64"
            : null;
}
