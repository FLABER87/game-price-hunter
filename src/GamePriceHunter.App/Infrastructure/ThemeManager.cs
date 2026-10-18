using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using GamePriceHunter.Core.Settings;
using Microsoft.Win32;

namespace GamePriceHunter.App.Infrastructure;

/// <summary>
/// Swaps the color dictionary at runtime and keeps the native title bar in sync with it
/// (dark mode flag and caption colors through DWM).
/// </summary>
public static partial class ThemeManager
{
    private const string ColorsPrefix = "Themes/Colors.";
    private static AppTheme _theme = AppTheme.System;
    private static bool _listening;

    public static bool IsDark { get; private set; } = true;

    public static event EventHandler? ThemeChanged;

    public static void Apply(AppTheme theme)
    {
        _theme = theme;
        var dark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark(),
        };

        if (!_listening)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listening = true;
        }

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var fresh = new ResourceDictionary { Source = new Uri($"pack://application:,,,/{ColorsPrefix}{(dark ? "Dark" : "Light")}.xaml") };
        var index = dictionaries.ToList().FindIndex(d => d.Source?.OriginalString.Contains(ColorsPrefix, StringComparison.OrdinalIgnoreCase) == true);
        if (index >= 0)
            dictionaries[index] = fresh;
        else
            dictionaries.Insert(0, fresh);

        IsDark = dark;
        foreach (Window window in Application.Current.Windows)
            ApplyTitleBar(window);
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Call from a window's constructor so its title bar follows the theme.</summary>
    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => ApplyTitleBar(window);
    }

    private static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        var darkFlag = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref darkFlag, sizeof(int));

        // Caption colors are Windows 11+; older builds ignore them.
        if (Application.Current.TryFindResource("Color.Window") is Color background)
        {
            var caption = ToColorRef(background);
            _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));
        }
        if (Application.Current.TryFindResource("Color.Text") is Color text)
        {
            var textColor = ToColorRef(text);
            _ = DwmSetWindowAttribute(handle, DwmwaTextColor, ref textColor, sizeof(int));
        }
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value ? value == 0 : true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_theme == AppTheme.System && e.Category == UserPreferenceCategory.General)
            Application.Current?.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
    }

    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
