using System.Windows;
using System.Windows.Threading;
using OB2.App.Infrastructure;

namespace OB2.App.Views;

/// <summary>A small confirmation dialog: delete (with "Also delete file") or a countdown.</summary>
public partial class ConfirmWindow : Window
{
    private DispatcherTimer? _timer;

    private ConfirmWindow()
    {
        InitializeComponent();
    }

    public bool OptionChecked => Option.IsChecked == true;

    /// <summary>§9.6: deleting a file from disk needs explicit confirmation.</summary>
    public static ConfirmWindow ForDelete(int count, bool offerFileDeletion)
    {
        var window = new ConfirmWindow { Title = Loc.Get("Delete_Title") };
        window.MessageText.Text = Loc.Format("Delete_Message", count);
        window.ConfirmButton.Content = Loc.Get("Delete_Confirm");
        window.ConfirmButton.SetResourceReference(StyleProperty, "DangerDialogButton");
        if (offerFileDeletion)
        {
            window.Option.Content = Loc.Get("Delete_AlsoFile");
            window.Option.Visibility = Visibility.Visible;
        }

        return window;
    }

    /// <summary>FR-17: a cancellable countdown before sleep, shutdown, or closing.</summary>
    public static ConfirmWindow ForCountdown(string action, int seconds)
    {
        var window = new ConfirmWindow { Title = Loc.Get("Countdown_Title"), Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = true };
        window.ConfirmButton.Content = Loc.Get("Common_Ok");
        var remaining = seconds;
        window.MessageText.Text = Loc.Format("Countdown_Message", action, remaining);
        window._timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) =>
        {
            remaining--;
            window.MessageText.Text = Loc.Format("Countdown_Message", action, remaining);
            if (remaining <= 0)
            {
                window.DialogResult = true;
            }
        }, window.Dispatcher);
        window.Closed += (_, _) => window._timer.Stop();
        return window;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
