using System.Windows;
using VoiceBridge.App.Desktop.ViewModels;

namespace VoiceBridge.App.Desktop.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
