using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using OB2.App.Infrastructure;

namespace OB2.App.Services;

/// <summary>Tray icon (§9.4) and Windows notifications (FR-27).</summary>
public sealed class TrayService : IDisposable
{
    private TaskbarIcon? _icon;

    public event EventHandler? ToggleRequested;

    public event EventHandler? ShowRequested;

    public event EventHandler? PauseAllRequested;

    public event EventHandler? ResumeAllRequested;

    public event EventHandler? OptionsRequested;

    public event EventHandler? ExitRequested;

    public void Initialize()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Tray_Show", () => ShowRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Tray_PauseAll", () => PauseAllRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(Item("Tray_ResumeAll", () => ResumeAllRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(Item("Tray_Options", () => OptionsRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Tray_Exit", () => ExitRequested?.Invoke(this, EventArgs.Empty)));

        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Resources/ob2.ico")),
            ToolTipText = Loc.Get("App_Title"),
            ContextMenu = menu,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(() => ToggleRequested?.Invoke(this, EventArgs.Empty)),
        };
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void SetToolTip(string text)
    {
        if (_icon is not null && _icon.ToolTipText != text)
        {
            _icon.ToolTipText = text;
        }
    }

    public void Notify(string title, string message, bool isError)
    {
        _icon?.ShowNotification(title, message, isError ? NotificationIcon.Error : NotificationIcon.Info);
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }

    private static MenuItem Item(string key, Action action)
    {
        var item = new MenuItem { Header = Loc.Get(key) };
        item.Click += (_, _) => action();
        return item;
    }
}
