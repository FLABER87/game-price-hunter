using System.Diagnostics;
using System.Windows;

namespace GamePriceHunter.App.Infrastructure;

/// <summary>Opens links and folders in the user's default applications.</summary>
public static class Shell
{
    /// <summary>Opens http(s) links only; store data must never launch other URI schemes.</summary>
    public static void OpenUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return;
        Start(uri.AbsoluteUri);
    }

    public static void OpenFolder(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        Start(path);
    }

    public static void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard is busy (another app holds it); not worth an error dialog.
        }
    }

    private static void Start(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            App.ReportError(ex);
        }
    }
}
