using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GamePriceHunter.App.Infrastructure;
using GamePriceHunter.App.Localization;
using GamePriceHunter.App.Services;
using GamePriceHunter.App.ViewModels;
using GamePriceHunter.Core.Settings;

namespace GamePriceHunter.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly AppServices _services;

    public MainWindow(MainViewModel viewModel, AppServices services)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _services = services;
        DataContext = viewModel;
        ThemeManager.Attach(this);
        ApplySavedBounds(services.Settings.Window);

        viewModel.SettingsRequested += (_, _) => OpenSettings();
        Loaded += (_, _) => SearchBox.Focus();
        PreviewKeyDown += OnWindowPreviewKeyDown;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _services.Settings.Window = new WindowBounds
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = WindowState == WindowState.Maximized,
        };
        base.OnClosing(e);
    }

    private void ApplySavedBounds(WindowBounds? bounds)
    {
        if (bounds is null || bounds.Width < MinWidth || bounds.Height < MinHeight)
            return;

        // Only restore if the window would be visible on the current monitor layout.
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var target = new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        if (!screen.IntersectsWith(target) || Rect.Intersect(screen, target).Width < 200)
            return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        if (bounds.IsMaximized)
            WindowState = WindowState.Maximized;
    }

    private void OpenSettings()
    {
        var settingsViewModel = new SettingsViewModel(_services, _viewModel.Update);
        var dialog = new SettingsWindow(settingsViewModel) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        var regionChanged = settingsViewModel.Save();
        ThemeManager.Apply(_services.Settings.Theme);
        if (!string.Equals(Loc.Instance.Culture.Name, _services.Settings.Language, StringComparison.OrdinalIgnoreCase))
            Loc.Instance.SetLanguage(_services.Settings.Language);
        _viewModel.OnSettingsChanged(regionChanged);
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if ((ctrl && e.Key is Key.F or Key.L) || (e.Key == Key.F3))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && _viewModel.SearchCommand.CanExecute(null))
        {
            _viewModel.SearchCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !SearchBox.IsKeyboardFocusWithin && _viewModel.CancelCommand.CanExecute(null))
        {
            _viewModel.CancelCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnSearchBoxFocused(object sender, KeyboardFocusChangedEventArgs e) => _viewModel.ShowHistory();

    private void OnSearchBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var popupOpen = SuggestionsPopup.IsOpen && SuggestionsList.Items.Count > 0;
        switch (e.Key)
        {
            case Key.Down when popupOpen:
                SuggestionsList.SelectedIndex = Math.Min(SuggestionsList.SelectedIndex + 1, SuggestionsList.Items.Count - 1);
                SuggestionsList.ScrollIntoView(SuggestionsList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Down:
                if (ResultsList.Items.Count > 0)
                {
                    ResultsList.SelectedIndex = Math.Max(0, ResultsList.SelectedIndex);
                    (ResultsList.ItemContainerGenerator.ContainerFromIndex(ResultsList.SelectedIndex) as UIElement)?.Focus();
                    e.Handled = true;
                }
                break;
            case Key.Up when popupOpen:
                SuggestionsList.SelectedIndex = Math.Max(SuggestionsList.SelectedIndex - 1, -1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (popupOpen && SuggestionsList.SelectedItem is SuggestionViewModel suggestion)
                    _viewModel.ApplySuggestionCommand.Execute(suggestion);
                else if (_viewModel.SearchCommand.CanExecute(null))
                    _viewModel.SearchCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                if (popupOpen)
                    _viewModel.CloseSuggestions();
                else if (_viewModel.CancelCommand.CanExecute(null))
                    _viewModel.CancelCommand.Execute(null);
                else
                    _viewModel.ClearQueryCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnSuggestionClicked(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is SuggestionViewModel suggestion)
        {
            _viewModel.ApplySuggestionCommand.Execute(suggestion);
            SearchBox.CaretIndex = SearchBox.Text.Length;
        }
    }

    private void OnResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ResultsList.SelectedItem is OfferViewModel offer)
        {
            _viewModel.OpenOfferCommand.Execute(offer);
            e.Handled = true;
        }
        else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && ResultsList.SelectedItem is OfferViewModel selected)
        {
            _viewModel.CopyLinkCommand.Execute(selected);
            e.Handled = true;
        }
    }

    private static OfferViewModel? OfferFrom(object sender) => (sender as FrameworkElement)?.DataContext as OfferViewModel;

    private void OnOpenOfferClick(object sender, RoutedEventArgs e) => _viewModel.OpenOfferCommand.Execute(OfferFrom(sender));

    private void OnCopyLinkClick(object sender, RoutedEventArgs e) => _viewModel.CopyLinkCommand.Execute(OfferFrom(sender));

    private void OnCopyTitleClick(object sender, RoutedEventArgs e) => _viewModel.CopyTitleCommand.Execute(OfferFrom(sender));

    private void OnSearchTitleClick(object sender, RoutedEventArgs e) => _viewModel.SearchTitleCommand.Execute(OfferFrom(sender));
}
