using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using GamePriceHunter.App.Infrastructure;
using GamePriceHunter.App.Localization;
using GamePriceHunter.App.Services;
using GamePriceHunter.Core.Currency;
using GamePriceHunter.Core.Diagnostics;
using GamePriceHunter.Core.Settings;
using GamePriceHunter.Core.Updates;

namespace GamePriceHunter.App.ViewModels;

public enum UpdateState
{
    None,
    Checking,
    UpToDate,
    CheckFailed,
    Available,
    Downloading,
    Installing,
    InstallFailed,
}

/// <summary>
/// Update offer shared by the main-window banner and Settings → About. Installed copies download the
/// new installer, check its signature, run it silently and exit; portable and stand-alone copies only
/// open the release page.
/// </summary>
public sealed class UpdateViewModel : ObservableObject
{
    /// <summary>Unattended upgrade; RELAUNCH makes the setup start the new version (see build\installer\GamePriceHunter.iss).</summary>
    private const string SilentInstallArguments = "/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /NORESTART /RELAUNCH=1";

    private readonly AppServices _services;
    private ReleaseInfo? _release;
    private UpdateState _state;
    private double _progress;
    private bool _isChecking;
    private bool _isDismissed;
    private CancellationTokenSource? _downloadCts;
    private bool _downloadCancelled;

    public UpdateViewModel(AppServices services)
    {
        _services = services;
        CheckCommand = new AsyncRelayCommand(() => CheckAsync(manual: true), () => !IsBusy);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => _release is not null && !IsBusy);
        CancelCommand = new RelayCommand(CancelDownload, () => State == UpdateState.Downloading);
        LaterCommand = new RelayCommand(() => IsDismissed = true);
        OpenReleasePageCommand = new RelayCommand(() => Shell.OpenUrl(_release?.PageUrl.AbsoluteUri ?? services.Updates.ReleasesPageUrl));

        // An update found by an earlier check is offered again on every start until it is installed.
        var known = services.Updates.CreateRelease(services.Settings.LatestReleaseTag);
        if (services.Settings.CheckForUpdates && known is not null && UpdateChecker.IsNewer(known, AppServices.Version))
        {
            _release = known;
            _state = UpdateState.Available;
        }

        Loc.Instance.LanguageChanged += (_, _) => RaiseStateProperties();
    }

    public ICommand CheckCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand LaterCommand { get; }
    public ICommand OpenReleasePageCommand { get; }

    /// <summary>"Update now" for installed copies; "Download" (the release page) for portable ones.</summary>
    public ICommand PrimaryCommand => _services.CanInstallUpdates ? InstallCommand : OpenReleasePageCommand;

    public string PrimaryText => Loc.T(_services.CanInstallUpdates ? "Update_Install" : "Update_Download");

    public string ReleasePageText => Loc.T(State == UpdateState.InstallFailed ? "Update_ReleasePage" : "Update_WhatsNew");

    public UpdateState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
                RaiseStateProperties();
        }
    }

    public bool IsBusy => _isChecking || State is UpdateState.Downloading or UpdateState.Installing;

    public bool IsBannerVisible =>
        !IsDismissed && State is UpdateState.Available or UpdateState.Downloading or UpdateState.Installing or UpdateState.InstallFailed;

    /// <summary>An update is known and nothing is running, so the buttons are offered.</summary>
    public bool IsOfferVisible => State is UpdateState.Available or UpdateState.InstallFailed;

    public bool IsDownloading => State == UpdateState.Downloading;

    public double ProgressPercent => _progress * 100;

    public string BannerText => State switch
    {
        UpdateState.Downloading => Loc.F("Update_Downloading", MoneyFormatter.Percent(Math.Floor(ProgressPercent) / 100, Loc.Instance.FormatCulture)),
        UpdateState.Installing => Loc.T("Update_Installing"),
        UpdateState.InstallFailed => Loc.T("Update_InstallFailed"),
        _ => Loc.F("Update_Available", _release?.Tag.TrimStart('v', 'V')),
    };

    /// <summary>One line under the version in Settings → About.</summary>
    public string StatusText => State switch
    {
        UpdateState.None => string.Empty,
        UpdateState.Checking => Loc.T("Update_Checking"),
        UpdateState.UpToDate => Loc.T("Update_UpToDate"),
        UpdateState.CheckFailed => Loc.T("Update_CheckFailed"),
        _ => BannerText,
    };

    private bool IsDismissed
    {
        get => _isDismissed;
        set
        {
            _isDismissed = value;
            OnPropertyChanged(nameof(IsBannerVisible));
        }
    }

    private double Progress
    {
        set
        {
            _progress = value;
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(BannerText));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>Startup check: at most once a day and silent unless there is something new.</summary>
    public async Task CheckOnStartupAsync()
    {
        _ = DeleteOldDownloadsAsync();
        if (_services.Settings.CheckForUpdates && UpdateChecker.IsCheckDue(_services.Settings.LastUpdateCheck, DateTimeOffset.UtcNow))
            await CheckAsync(manual: false);
    }

    private async Task CheckAsync(bool manual)
    {
        if (IsBusy)
            return;

        var previous = State;
        _isChecking = true;
        if (manual)
            State = UpdateState.Checking;
        RaiseStateProperties();
        try
        {
            var latest = await _services.Updates.GetLatestReleaseAsync();
            _services.Settings.LastUpdateCheck = DateTimeOffset.UtcNow;
            _services.Settings.LatestReleaseTag = latest?.Tag;
            _services.SaveSettingsSoon();

            if (latest is not null && UpdateChecker.IsNewer(latest, AppServices.Version))
            {
                _services.Logger.Info($"Update available: {latest.Tag}.");
                _release = latest;
                if (manual)
                    IsDismissed = false;
                State = UpdateState.Available;
            }
            else
            {
                _release = null;
                State = manual ? UpdateState.UpToDate : UpdateState.None;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _services.Logger.Warn($"Update check failed: {ex.Message}");
            State = manual ? UpdateState.CheckFailed : previous;
        }
        finally
        {
            _isChecking = false;
            RaiseStateProperties();
        }
    }

    private async Task InstallAsync()
    {
        if (_release is not { } release || IsBusy)
            return;

        if (!_services.CanInstallUpdates)
        {
            Shell.OpenUrl(release.PageUrl.AbsoluteUri);
            return;
        }

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        _downloadCts = cts;
        _downloadCancelled = false;
        Progress = 0;
        State = UpdateState.Downloading;
        string installer;
        try
        {
            var target = Path.Combine(AppPaths.UpdateDownloadDirectory, $"GamePriceHunter-Setup-{release.Version.ToString(3)}.exe");
            // Returns only once the installer matches the release's signature; a missing or wrong one throws InvalidDataException.
            installer = await InstallerDownloader.DownloadAsync(_services.Http, release, target, new Progress<double>(p => Progress = p), cts.Token);
        }
        catch (OperationCanceledException) when (_downloadCancelled)
        {
            State = UpdateState.Available;
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
                                       or OperationCanceledException or CryptographicException)
        {
            _services.Logger.Error($"Could not download or verify {release.InstallerUrl}.", ex);
            State = UpdateState.InstallFailed;
            return;
        }
        finally
        {
            _downloadCts = null;
            cts.Dispose();
        }

        State = UpdateState.Installing;
        try
        {
            var log = Path.Combine(_services.Paths.LogDirectory, $"update-{release.Version.ToString(3)}.log");
            Process.Start(new ProcessStartInfo(installer, $"{SilentInstallArguments} /LOG=\"{log}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _services.Logger.Error("Could not start the installer.", ex);
            State = UpdateState.InstallFailed;
            return;
        }

        // The setup waits for this process to exit, replaces the exe and starts the new version.
        _services.Logger.Info($"Installing {release.Tag}; closing.");
        Application.Current.Shutdown();
    }

    private void CancelDownload()
    {
        _downloadCancelled = true;
        _downloadCts?.Cancel();
    }

    private static async Task DeleteOldDownloadsAsync()
    {
        // Right after an update the installer that started this copy is still finishing, so retry a little later.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(AppPaths.UpdateDownloadDirectory))
                    Directory.Delete(AppPaths.UpdateDownloadDirectory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromSeconds(20));
            }
        }
    }

    private void RaiseStateProperties()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsBannerVisible));
        OnPropertyChanged(nameof(IsOfferVisible));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(BannerText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PrimaryText));
        OnPropertyChanged(nameof(ReleasePageText));
        CommandManager.InvalidateRequerySuggested();
    }
}
