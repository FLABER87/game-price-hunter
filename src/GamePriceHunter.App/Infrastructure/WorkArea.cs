using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GamePriceHunter.App.Infrastructure;

/// <summary>
/// Keeps windows inside the work area of their monitor. At high display scaling a screen is small in
/// device-independent pixels (a 4K display at 300% has 1280×672 above the taskbar), so a window opened at
/// its design size, or at a saved one, could be taller than the screen and center with its title bar above it.
/// </summary>
public static partial class WorkArea
{
    /// <summary>Call from a window's constructor, after its size and position are set.</summary>
    public static void KeepInside(Window window)
    {
        window.SourceInitialized += (_, _) => Fit(window);
    }

    // Runs before the window is first shown. Works on the restored bounds, so a window that opens
    // maximized also restores to a size that fits.
    private static void Fit(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        var placement = new WindowPlacement { length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, MonitorDefaultToNearest), ref info) || !GetWindowPlacement(handle, ref placement))
            return;

        // The minimum size wins over the work area, so a smaller screen lowers it.
        var dpi = VisualTreeHelper.GetDpi(window);
        var workWidth = info.rcWork.Right - info.rcWork.Left;
        var workHeight = info.rcWork.Bottom - info.rcWork.Top;
        window.MinWidth = Math.Min(window.MinWidth, workWidth / dpi.DpiScaleX);
        window.MinHeight = Math.Min(window.MinHeight, workHeight / dpi.DpiScaleY);

        // The placement is in workspace coordinates: the work area starts where its monitor does.
        var normal = placement.rcNormalPosition;
        var width = Math.Min(normal.Right - normal.Left, workWidth);
        var height = Math.Min(normal.Bottom - normal.Top, workHeight);
        var left = Math.Max(info.rcMonitor.Left, Math.Min(normal.Left, info.rcMonitor.Left + workWidth - width));
        var top = Math.Max(info.rcMonitor.Top, Math.Min(normal.Top, info.rcMonitor.Top + workHeight - height));
        if (left == normal.Left && top == normal.Top && width == normal.Right - normal.Left && height == normal.Bottom - normal.Top)
            return;

        placement.rcNormalPosition = new NativeRect { Left = left, Top = top, Right = left + width, Bottom = top + height };
        // A maximized placement would otherwise show the window before WPF does.
        if (!window.IsVisible)
            placement.showCmd = SwHide;
        SetWindowPlacement(handle, ref placement);
    }

    private const int MonitorDefaultToNearest = 2;
    private const int SwHide = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int length;
        public int flags;
        public int showCmd;
        public NativePoint ptMinPosition;
        public NativePoint ptMaxPosition;
        public NativeRect rcNormalPosition;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);
}
