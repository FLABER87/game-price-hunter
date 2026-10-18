using System.Runtime.InteropServices;

namespace GamePriceHunter.App.Infrastructure;

/// <summary>
/// Keeps the app to one running copy per Windows session, the installed and a portable one included.
/// A second launch wakes the running copy so it comes to the front, and exits.
/// </summary>
public static partial class SingleInstance
{
    /// <summary>
    /// Exists while the app runs; the setup also waits for it to go away before replacing the exe during
    /// an update. Must match AppMutex in build\installer\GamePriceHunter.iss.
    /// </summary>
    private const string RunningMutexName = "GamePriceHunter.Running";
    private const string ShowEventName = "GamePriceHunter.Show";

    // Never released explicitly: the handles have to live until the process is gone.
    private static Mutex? _runningMutex;
    private static EventWaitHandle? _showEvent;

    /// <summary>
    /// True in the first copy, which from now on calls <paramref name="show"/> (on a pool thread) each time
    /// the app is launched again. False in a later copy, which has woken the first one and should exit.
    /// </summary>
    public static bool TryStart(Action show)
    {
        _runningMutex = new Mutex(initiallyOwned: false, RunningMutexName, out var createdNew);
        _showEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ShowEventName);
        if (!createdNew)
        {
            // Windows lets only the foreground process, this one, started by the user, raise a window.
            _ = AllowSetForegroundWindow(AsfwAny);
            _showEvent.Set();
            return false;
        }

        ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => show(), null, Timeout.Infinite, executeOnlyOnce: false);
        return true;
    }

    private const int AsfwAny = -1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
