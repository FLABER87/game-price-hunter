using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Shell;

namespace GamePriceHunter.App.Infrastructure;

/// <summary>
/// Turns a window's header into its title bar: the header is dragged like a caption and holds the window's own
/// caption buttons (elements in it that take clicks need WindowChrome.IsHitTestVisibleInChrome). Also covers what
/// WindowChrome leaves out: a maximized window reaches past the screen edges by its resize frame, and Windows 11
/// offers snap layouts only over a maximize button it knows of, so this one answers hit tests as one and gets
/// its hover and press states from here.
/// </summary>
public static partial class TitleBar
{
    public static readonly DependencyProperty IsHotProperty =
        DependencyProperty.RegisterAttached("IsHot", typeof(bool), typeof(TitleBar), new PropertyMetadata(false));

    public static readonly DependencyProperty IsDownProperty =
        DependencyProperty.RegisterAttached("IsDown", typeof(bool), typeof(TitleBar), new PropertyMetadata(false));

    public static bool GetIsHot(DependencyObject element) => (bool)element.GetValue(IsHotProperty);

    public static void SetIsHot(DependencyObject element, bool value) => element.SetValue(IsHotProperty, value);

    public static bool GetIsDown(DependencyObject element) => (bool)element.GetValue(IsDownProperty);

    public static void SetIsDown(DependencyObject element, bool value) => element.SetValue(IsDownProperty, value);

    /// <summary>Call from a window's constructor, after InitializeComponent.</summary>
    public static void Attach(Window window, FrameworkElement header, ButtonBase maximizeButton)
    {
        // A glass frame keeps the DWM shadow and, on Windows 11, the rounded corners; without one WindowChrome
        // clips the window to a region. One pixel at the bottom is enough and is painted over.
        var chrome = new WindowChrome
        {
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = default,
            UseAeroCaptionButtons = false,
        };
        WindowChrome.SetWindowChrome(window, chrome);

        // After WindowChrome's own hook is in: the last hook added sees a message first.
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var corners = DwmwcpRound;
            _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corners, sizeof(int));
            HwndSource.FromHwnd(handle)?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                WndProc(hwnd, maximizeButton, msg, wParam, lParam, ref handled));
        };

        void Fit() => FitToScreen(window, header, chrome);
        window.Loaded += (_, _) => Fit();
        window.StateChanged += (_, _) => Fit();
        window.SizeChanged += (_, _) => Fit();
        window.LocationChanged += (_, _) => Fit();
        header.SizeChanged += (_, _) => Fit();
    }

    // Keeps a maximized window's content on the screen and the caption as tall as the header.
    private static void FitToScreen(Window window, FrameworkElement header, WindowChrome chrome)
    {
        if (window.Content is not FrameworkElement root || !header.IsLoaded)
            return;

        var inset = window.WindowState == WindowState.Maximized ? WorkArea.Overhang(window) : default;
        if (root.Margin != inset)
            root.Margin = inset;

        // The caption starts below the top resize border.
        var bottom = inset.Top + header.ActualHeight;
        chrome.CaptionHeight = Math.Max(0, bottom - chrome.ResizeBorderThickness.Top);
    }

    private static IntPtr WndProc(IntPtr hwnd, ButtonBase maximize, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var onMaximize = wParam == HtMaxButton;
        switch (msg)
        {
            case WmNcHitTest when IsOver(maximize, lParam):
                handled = true;
                return HtMaxButton;

            case WmNcMouseMove:
                SetIsHot(maximize, onMaximize);
                if (!onMaximize)
                    SetIsDown(maximize, false);
                var track = new TrackMouseEventInfo { cbSize = Marshal.SizeOf<TrackMouseEventInfo>(), dwFlags = TmeLeave | TmeNonClient, hwndTrack = hwnd };
                _ = TrackMouseEvent(ref track);
                break;

            case WmNcMouseLeave:
                SetIsHot(maximize, false);
                SetIsDown(maximize, false);
                break;

            // Windows would track the press itself and draw a classic button over ours.
            case WmNcLButtonDown or WmNcLButtonDblClk when onMaximize:
                SetIsDown(maximize, true);
                handled = true;
                break;

            case WmNcLButtonUp when onMaximize:
                if (GetIsDown(maximize))
                    maximize.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, maximize));
                SetIsDown(maximize, false);
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private static bool IsOver(FrameworkElement element, IntPtr lParam)
    {
        if (!element.IsVisible || PresentationSource.FromVisual(element) is null)
            return false;

        // Screen coordinates in device pixels, signed: monitors left of or above the main one are negative.
        var value = lParam.ToInt64();
        var point = element.PointFromScreen(new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF)));
        return point.X >= 0 && point.Y >= 0 && point.X < element.ActualWidth && point.Y < element.ActualHeight;
    }

    private const int WmNcHitTest = 0x0084;
    private const int WmNcMouseMove = 0x00A0;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcLButtonUp = 0x00A2;
    private const int WmNcLButtonDblClk = 0x00A3;
    private const int WmNcMouseLeave = 0x02A2;
    private const int TmeLeave = 0x0002;
    private const int TmeNonClient = 0x0010;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private static readonly IntPtr HtMaxButton = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventInfo
    {
        public int cbSize;
        public int dwFlags;
        public IntPtr hwndTrack;
        public int dwHoverTime;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TrackMouseEvent(ref TrackMouseEventInfo info);

    // Windows 11 only; Windows 10 returns an error and keeps square corners.
    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
