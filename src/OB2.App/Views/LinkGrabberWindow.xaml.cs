using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using OB2.App.Infrastructure;
using OB2.App.ViewModels;

namespace OB2.App.Views;

/// <summary>D-87: Grab links.</summary>
public partial class LinkGrabberWindow : Window
{
    private readonly LinkGrabberViewModel _viewModel;

    public LinkGrabberWindow(LinkGrabberViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.ConfirmCredentials = count =>
            MessageBox.Show(this, Loc.Format("Grab_Credentials", count), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        viewModel.CloseRequested += (_, _) => Close();
        Loaded += (_, _) =>
        {
            // The clipboard's text, if it was put in the box, is selected: pasting something else replaces it.
            PasteBox.SelectAll();
            MakeRoomForGuide();
        };
        Closed += (_, _) => viewModel.Dispose();
    }

    /// <summary>
    /// D-88: the guide opens with the window the first time; the window grows by its height so the
    /// list keeps its room, within the screen's working area, and stays centred.
    /// </summary>
    private void MakeRoomForGuide()
    {
        if (!_viewModel.IsGuideVisible)
        {
            return;
        }

        var area = SystemParameters.WorkArea;
        var extra = Math.Min(Guide.ActualHeight + Guide.Margin.Bottom, Math.Max(0, area.Height - Height));
        Height += extra;
        Top = Math.Max(area.Top, Math.Min(Top - (extra / 2), area.Bottom - Height));
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _viewModel.SelectedLinks = List.SelectedItems.OfType<GrabbedLinkViewModel>().ToList();

    /// <summary>A double-click on a row, but not on its tick box or the column headers.</summary>
    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = ParentOf(element))
        {
            if (element is ToggleButton or DataGridColumnHeader)
            {
                return;
            }

            if (element is DataGridRow)
            {
                if (_viewModel.OpenLinkCommand.CanExecute(null))
                {
                    _viewModel.OpenLinkCommand.Execute(null);
                }

                return;
            }
        }
    }

    /// <summary>Text in a cell is a content element, which VisualTreeHelper refuses.</summary>
    private static DependencyObject? ParentOf(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = _viewModel.SaveFolder };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.ChooseFolder(dialog.FolderName);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
