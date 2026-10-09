using System.Windows;
using OB2.App.ViewModels;

namespace OB2.App.Views;

public partial class SchedulerWindow : Window
{
    public SchedulerWindow(SchedulerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
    }
}
