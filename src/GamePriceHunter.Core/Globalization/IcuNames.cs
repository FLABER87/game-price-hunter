using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace GamePriceHunter.Core.Globalization;

/// <summary>
/// Country and currency names in any language, from the ICU library that is part of Windows 10 1903 and
/// later (.NET formats numbers with it too). Returns null when ICU is missing or has no name, and the
/// caller falls back to the English name. .NET itself only gives native or English country names.
/// </summary>
internal static unsafe partial class IcuNames
{
    private const int UsingFallbackWarning = -128;
    private const int CurrencyLongName = 1;

    private static readonly ConcurrentDictionary<(string Code, string Locale), string?> CountryCache = new();
    private static readonly ConcurrentDictionary<(string Code, string Locale), string?> CurrencyCache = new();
    private static bool _unavailable;

    /// <param name="locale">Culture name of the language to name it in ("ru", "zh-Hans").</param>
    public static string? Country(string countryCode, string locale) =>
        CountryCache.GetOrAdd((countryCode.ToUpperInvariant(), locale), key => Call(() => GetCountry(key.Code, key.Locale)));

    public static string? Currency(string currencyCode, string locale) =>
        CurrencyCache.GetOrAdd((currencyCode.ToUpperInvariant(), locale), key => Call(() => GetCurrency(key.Code, key.Locale)));

    private static string? Call(Func<string?> lookup)
    {
        if (_unavailable)
            return null;
        try
        {
            return lookup();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _unavailable = true;
            return null;
        }
    }

    private static string? GetCountry(string countryCode, string locale)
    {
        var buffer = stackalloc char[128];
        var status = 0;
        var length = GetDisplayCountry(Ascii("und_" + countryCode), Ascii(IcuLocale(locale)), buffer, 128, &status);
        // A missing name comes back as the code itself with a "default" warning.
        return IsSuccess(status) && length > 0 ? new string(buffer, 0, length) : null;
    }

    private static string? GetCurrency(string currencyCode, string locale)
    {
        var status = 0;
        var length = 0;
        byte isChoiceFormat = 0;
        fixed (char* code = currencyCode + "\0")
        {
            var name = GetCurrencyName(code, Ascii(IcuLocale(locale)), CurrencyLongName, &isChoiceFormat, &length, &status);
            return IsSuccess(status) && name != null && length > 0 ? new string(name, 0, length) : null;
        }
    }

    private static bool IsSuccess(int status) => status is 0 or UsingFallbackWarning;

    private static string IcuLocale(string cultureName) => cultureName.Replace('-', '_');

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text + "\0");

    // ICU's C API is cdecl; that differs from the Windows default (stdcall) in 32-bit builds.
    [LibraryImport("icu.dll", EntryPoint = "uloc_getDisplayCountry")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int GetDisplayCountry(byte[] locale, byte[] displayLocale, char* country, int capacity, int* status);

    [LibraryImport("icu.dll", EntryPoint = "ucurr_getName")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial char* GetCurrencyName(char* currency, byte[] locale, int nameStyle, byte* isChoiceFormat, int* length, int* status);
}
