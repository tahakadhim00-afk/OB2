using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OB2.App.Services;
using OB2.App.ViewModels;

namespace OB2.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        PreviewKeyDown += OnPreviewKeyDown;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedRow))
            {
                UpdateDetails();
            }
        };
        UpdateDetails();
    }

    /// <summary>True when Exit was chosen; otherwise closing hides to the tray if the setting is on (§9.4).</summary>
    public bool IsExiting { get; set; }

    public Func<bool>? ShouldHideOnClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!IsExiting && ShouldHideOnClose?.Invoke() == true)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+F focuses search (§9.6).
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin)
        {
            SearchBox.Clear();
            List.Focus();
            e.Handled = true;
        }
    }

    private void OnNavSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is NavNodeViewModel node && node.Filter is not null)
        {
            _viewModel.SelectedNode = node;
        }
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _viewModel.SelectedRows = List.SelectedItems.OfType<DownloadRowViewModel>().ToList();

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.OpenFileCommand.CanExecute(null))
        {
            _viewModel.OpenFileCommand.Execute(null);
        }
        else if (_viewModel.OpenFolderCommand.CanExecute(null))
        {
            _viewModel.OpenFolderCommand.Execute(null);
        }
    }

    private void UpdateDetails()
    {
        var has = _viewModel.SelectedRow is not null;
        DetailBody.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        DetailPlaceholder.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
    }

    // FR-1: drag a link or URL text onto the window.
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ExtractUrl(e.Data) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (ExtractUrl(e.Data) is { } url)
        {
            _viewModel.AddFromPrefill(new AddDownloadPrefill { Url = url });
        }
    }

    private static string? ExtractUrl(IDataObject data)
    {
        foreach (var format in new[] { "UniformResourceLocatorW", DataFormats.UnicodeText, DataFormats.Text })
        {
            if (!data.GetDataPresent(format))
            {
                continue;
            }

            var value = data.GetData(format) switch
            {
                string s => s,
                System.IO.MemoryStream ms => System.Text.Encoding.Unicode.GetString(ms.ToArray()).TrimEnd('\0'),
                _ => null,
            };
            var line = value?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return uri.AbsoluteUri;
            }
        }

        return null;
    }
}
