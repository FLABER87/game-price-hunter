using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows.Data;
using System.Windows.Markup;
using GamePriceHunter.Core.Globalization;

namespace GamePriceHunter.App.Localization;

public sealed record LanguageOption(string Code, string NativeName);

/// <summary>
/// Runtime-switchable string table backed by Strings.resx, plus the culture numbers and prices are written in.
/// To add a language, add Strings.&lt;culture&gt;.resx next to Strings.resx, list it in <see cref="Languages"/>,
/// in SatelliteResourceLanguages (Directory.Build.props) and in the installer's [Languages].
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    // What Windows is set to, read before the app changes the thread cultures.
    private static readonly CultureInfo WindowsUiCulture = CultureInfo.CurrentUICulture;
    private static readonly CultureInfo WindowsFormat = CultureInfo.CurrentCulture;

    private readonly ResourceManager _resources = new("GamePriceHunter.App.Localization.Strings", typeof(Loc).Assembly);
    private string? _countryCode;

    private Loc()
    {
        Culture = CultureInfo.GetCultureInfo("en");
        FormatCulture = FormatCultures.Select(Culture, WindowsFormat, null);
    }

    public static Loc Instance { get; } = new();

    /// <summary>English first, then Latin script, Cyrillic and East Asian languages, each group in alphabetical order.</summary>
    public static IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new("en", "English"),
        new("de", "Deutsch"),
        new("es", "Español"),
        new("fr", "Français"),
        new("it", "Italiano"),
        new("pl", "Polski"),
        new("pt-BR", "Português (Brasil)"),
        new("tr", "Türkçe"),
        new("ru", "Русский"),
        new("uk", "Українська"),
        new("zh-Hans", "简体中文"),
        new("ja", "日本語"),
        new("ko", "한국어"),
    ];

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The UI language or the number format changed: texts with numbers and prices need a refresh.</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>The UI language.</summary>
    public CultureInfo Culture { get; private set; }

    /// <summary>The culture numbers and prices are written in (see <see cref="FormatCultures.Select"/>).</summary>
    public CultureInfo FormatCulture { get; private set; }

    /// <summary>For FrameworkElement.Language: WPF picks Japanese, Chinese or Korean glyph shapes by it.</summary>
    public XmlLanguage XmlLanguage => XmlLanguage.GetLanguage(Culture.IetfLanguageTag);

    public string this[string key] => _resources.GetString(key, Culture) ?? $"[{key}]";

    public static string T(string key) => Instance[key];

    public static string F(string key, params object?[] args) => string.Format(Instance.FormatCulture, Instance[key], args);

    /// <summary>
    /// The plural form of <paramref name="key"/> that suits <paramref name="count"/>: Key_one, Key_few or Key_many,
    /// or Key itself for "other". Without <paramref name="args"/> the count is {0}.
    /// </summary>
    public static string P(string key, long count, params object?[] args)
    {
        var category = PluralRules.Select(Instance.Culture.Name, count);
        var form = category == PluralCategory.Other ? key : $"{key}_{category.ToString().ToLowerInvariant()}";
        return string.Format(Instance.FormatCulture, Instance[form], args.Length == 0 ? [count] : args);
    }

    /// <summary>The language the app uses when the setting says "follow Windows".</summary>
    public static LanguageOption WindowsLanguage => Match(WindowsUiCulture);

    /// <param name="code">The saved language; empty or unknown means the Windows language.</param>
    /// <param name="countryCode">The user's country; picks the number format when Windows has another language.</param>
    public void SetLanguage(string? code, string? countryCode)
    {
        var supported = Languages.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) ?? WindowsLanguage;
        Culture = CultureInfo.GetCultureInfo(supported.Code);
        _countryCode = countryCode;
        FormatCulture = FormatCultures.Select(Culture, WindowsFormat, countryCode);
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        Thread.CurrentThread.CurrentUICulture = Culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(XmlLanguage)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The user's country became known or changed, which can change the number format.</summary>
    public void SetCountry(string? countryCode)
    {
        if (string.Equals(countryCode, _countryCode, StringComparison.OrdinalIgnoreCase))
            return;
        _countryCode = countryCode;
        var format = FormatCultures.Select(Culture, WindowsFormat, countryCode);
        if (format.Name == FormatCulture.Name)
            return;
        FormatCulture = format;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The culture itself or a parent ("pt-BR", "zh-Hans" for zh-CN), else any variant of its language (pt-PT gets pt-BR).</summary>
    private static LanguageOption Match(CultureInfo culture)
    {
        for (var c = culture; c.Name.Length > 0; c = c.Parent)
        {
            if (Languages.FirstOrDefault(l => string.Equals(l.Code, c.Name, StringComparison.OrdinalIgnoreCase)) is { } exact)
                return exact;
        }
        return Languages.FirstOrDefault(l => CultureInfo.GetCultureInfo(l.Code).TwoLetterISOLanguageName == culture.TwoLetterISOLanguageName)
            ?? Languages[0];
    }
}

/// <summary>XAML: Text="{loc:Loc Search_Placeholder}". Updates live when the language changes.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
