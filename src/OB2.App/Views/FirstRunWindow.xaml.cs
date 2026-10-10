using System.IO;
using System.Windows;
using Microsoft.Win32;
using OB2.App.Infrastructure;

namespace OB2.App.Views;

/// <summary>First-run wizard (Flow D, Phase 5): choose where downloads are saved.</summary>
public partial class FirstRunWindow : Window
{
    public FirstRunWindow(string suggestedFolder, bool isPortable, string dataRoot)
    {
        InitializeComponent();
        FolderBox.Text = suggestedFolder;
        ModeText.Text = Loc.Format(isPortable ? "FirstRun_Portable" : "FirstRun_PerUser", dataRoot);
    }

    /// <summary>The folder the user accepted.</summary>
    public string? ChosenFolder { get; private set; }

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
