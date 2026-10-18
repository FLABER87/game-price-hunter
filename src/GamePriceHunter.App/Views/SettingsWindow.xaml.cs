using System.Windows;
using GamePriceHunter.App.Infrastructure;
using GamePriceHunter.App.ViewModels;

namespace GamePriceHunter.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ThemeManager.Attach(this);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
