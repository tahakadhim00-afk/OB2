using System.Windows;
using Microsoft.Win32;
using OB2.App.ViewModels;
using OB2.Core.Downloads;

namespace OB2.App.Views;

public partial class MediaDownloadWindow : Window
{
    private readonly MediaDownloadViewModel _viewModel;

    public MediaDownloadWindow(MediaDownloadViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
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
