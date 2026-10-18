using System.Windows;
using System.Windows.Threading;
using GamePriceHunter.App.Infrastructure;
using GamePriceHunter.App.Localization;
using GamePriceHunter.App.Services;
using GamePriceHunter.App.ViewModels;
using GamePriceHunter.App.Views;
using GamePriceHunter.Core.Diagnostics;

namespace GamePriceHunter.App;

public partial class App : Application
{
    private static AppServices? _services;

    public static AppServices Services => _services ?? throw new InvalidOperationException("Services are not initialized.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _services?.Logger.Error("Unhandled exception.", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _services?.Logger.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };

        _services = AppServices.Create();
        Loc.Instance.SetLanguage(_services.Settings.Language);
        ThemeManager.Apply(_services.Settings.Theme);

        var viewModel = new MainViewModel(_services);
        var window = new MainWindow(viewModel, _services);
        MainWindow = window;
        window.Show();
        _ = viewModel.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Logger.Info("Exiting.");
        _services?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Logs an error and tells the user without taking the app down.</summary>
    public static void ReportError(Exception exception)
    {
        _services?.Logger.Error("Unexpected error.", exception);
        var owner = Current?.MainWindow;
        var message = Loc.F("Error_Unexpected", exception.Message);
        if (owner is { IsLoaded: true })
            MessageBox.Show(owner, message, Loc.T("App_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show(message, Loc.T("App_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ReportError(e.Exception);
    }
}
