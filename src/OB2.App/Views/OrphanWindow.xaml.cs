using System.Windows;
using OB2.App.ViewModels;

namespace OB2.App.Views;

/// <summary>§10.3: lists partial files that no download claims.</summary>
public partial class OrphanWindow : Window
{
    public OrphanWindow(OrphanViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
