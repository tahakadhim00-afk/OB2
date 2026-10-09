using System.Windows;
using Microsoft.Win32;
using OB2.App.Infrastructure;
using OB2.App.ViewModels;
using OB2.Core.Downloads;

namespace OB2.App.Views;

public partial class AddDownloadWindow : Window
{
    private readonly AddDownloadViewModel _viewModel;

    public AddDownloadWindow(AddDownloadViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.ConfirmCredentials = () =>
            MessageBox.Show(this, Loc.Get("Add_Credentials"), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        viewModel.CloseRequested += (_, item) =>
        {
            Result = item;
            Close();
        };
        Closed += (_, _) => viewModel.Dispose();
    }

    public DownloadItem? Result { get; private set; }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = _viewModel.SaveFolder };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.ChooseFolder(dialog.FolderName);
        }
    }
}
