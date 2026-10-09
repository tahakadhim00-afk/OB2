using System.Windows;
using Microsoft.Win32;
using OB2.App.ViewModels;

namespace OB2.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += (_, saved) => DialogResult = saved;
    }

    private void OnBrowseDefault(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = _viewModel.DefaultFolder };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.DefaultFolder = dialog.FolderName;
        }
    }
}
