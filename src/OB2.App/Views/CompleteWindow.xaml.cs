using System.Windows;
using OB2.App.Infrastructure;
using OB2.Core.Downloads;

namespace OB2.App.Views;

/// <summary>Download Complete dialog (§9.3).</summary>
public partial class CompleteWindow : Window
{
    private readonly Action _openFile;
    private readonly Action _openFolder;

    public CompleteWindow(DownloadItem item, Action openFile, Action openFolder)
    {
        InitializeComponent();
        _openFile = openFile;
        _openFolder = openFolder;
        NameText.Text = item.FileName;
        SizeText.Text = Loc.Format("Complete_Size", Fmt.Size(item.TotalBytes));
        var taken = item.CompletedAt - item.StartedAt;
        TimeText.Text = Loc.Format("Complete_Time", Fmt.Duration(taken));
    }

    /// <summary>"Do not show again for this category" was ticked.</summary>
    public bool SuppressCategory => DontShow.IsChecked == true;

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        Close();
        _openFile();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Close();
        _openFolder();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
