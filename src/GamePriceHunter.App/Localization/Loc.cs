using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows.Data;
using System.Windows.Markup;

namespace GamePriceHunter.App.Localization;

public sealed record LanguageOption(string Code, string NativeName);

/// <summary>
/// Runtime-switchable string table backed by Strings.resx. To add a language, add
/// Strings.&lt;culture&gt;.resx next to Strings.resx and list it in <see cref="Languages"/>.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private readonly ResourceManager _resources = new("GamePriceHunter.App.Localization.Strings", typeof(Loc).Assembly);

    private Loc()
    {
    }

    public static Loc Instance { get; } = new();

    public static IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new("en", "English"),
    ];

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en");

    public string this[string key] => _resources.GetString(key, Culture) ?? $"[{key}]";

    public static string T(string key) => Instance[key];

    public static string F(string key, params object?[] args) => string.Format(Instance.Culture, Instance[key], args);

    public void SetLanguage(string? code)
    {
        var supported = Languages.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))
            ?? Languages.FirstOrDefault(l => l.Code == CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)
            ?? Languages[0];

        Culture = CultureInfo.GetCultureInfo(supported.Code);
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        Thread.CurrentThread.CurrentUICulture = Culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
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
