using System.IO;
using System.Windows;
using Microsoft.Win32;
using OB2.App.Infrastructure;
using OB2.App.ViewModels;

namespace OB2.App.Views;

/// <summary>
/// First-run wizard (Flow D, Phase 5). D-89: first the video tools are installed (required), then
/// the user chooses where downloads are saved. When the tools are already there, it opens on the
/// folder.
/// </summary>
public partial class FirstRunWindow : Window
{
    public FirstRunWindow(string suggestedFolder, bool isPortable, string dataRoot, FirstRunToolsViewModel tools)
    {
        InitializeComponent();
        DataContext = tools;
        FolderBox.Text = suggestedFolder;
        ModeText.Text = Loc.Format(isPortable ? "FirstRun_Portable" : "FirstRun_PerUser", dataRoot);
        tools.Completed += (_, _) => ShowFolderStep();
        Closed += (_, _) => tools.Dispose(); // stops a running download; it continues next time

        if (tools.IsInstalled)
        {
            FolderStepNumber.Visibility = Visibility.Collapsed; // one step only
            ShowFolderStep();
        }
    }

    /// <summary>The folder the user accepted.</summary>
    public string? ChosenFolder { get; private set; }

    private void ShowFolderStep()
    {
        ToolsStep.Visibility = Visibility.Collapsed;
        FolderStep.Visibility = Visibility.Visible;
        StartButton.IsDefault = true;
        FolderBox.Focus();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = FolderBox.Text };
        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
        }
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = Path.GetFullPath(FolderBox.Text.Trim());
            Directory.CreateDirectory(folder);
            ChosenFolder = folder;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ErrorText.Text = Loc.Format("FirstRun_FolderError", ex.Message);
        }
    }
}
