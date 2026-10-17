using System.Windows;
using Microsoft.Win32;
using OB2.App.Infrastructure;
using OB2.App.ViewModels;
using OB2.Core.Settings;

namespace OB2.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    /// <summary>The theme in force when the window opened, to undo an unsaved preview.</summary>
    private readonly AppTheme _themeOnOpen = ThemeManager.Current;

    private bool _saved;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += (_, saved) =>
        {
            _saved = saved;
            DialogResult = saved;
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        // Picking a theme applies it at once so it can be seen; leaving without saving puts
        // the old one back, including when the window is closed with the title-bar X.
        if (!_saved && ThemeManager.Current != _themeOnOpen)
        {
            ThemeManager.Apply(_themeOnOpen);
        }

        base.OnClosed(e);
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
