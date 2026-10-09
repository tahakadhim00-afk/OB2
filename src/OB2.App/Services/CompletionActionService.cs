using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.Logging;
using OB2.App.Infrastructure;
using OB2.App.Views;
using OB2.Core.Organization;

namespace OB2.App.Services;

/// <summary>
/// Runs a queue's after-completion action (FR-17). Sleep, shutdown, and closing the app wait
/// behind a 30-second countdown the user can cancel; shutdown also leaves Windows' own
/// one-minute warning (<c>shutdown /a</c> aborts it).
/// </summary>
public sealed partial class CompletionActionService
{
    private const int CountdownSeconds = 30;

    private readonly ILogger<CompletionActionService> _logger;

    public CompletionActionService(ILogger<CompletionActionService> logger)
    {
        _logger = logger;
    }

    /// <summary>Set by the app: a clean exit (checkpoint downloads, remove tray icon).</summary>
    public Action? ExitApp { get; set; }

    public void Run(QueueDefinition queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        var schedule = queue.Schedule;
        LogAction(queue.Name, schedule.CompletionAction);
        switch (schedule.CompletionAction)
        {
            case CompletionAction.None:
                return;
            case CompletionAction.RunCommand when !string.IsNullOrWhiteSpace(schedule.Command):
                RunCommand(schedule.Command);
                return;
            case CompletionAction.CloseApp when Confirm("Action_CloseApp"):
                ExitApp?.Invoke();
                return;
            case CompletionAction.Sleep when Confirm("Action_Sleep"):
                SetSuspendState(hibernate: false, force: false, disableWakeEvent: false);
                return;
            case CompletionAction.Shutdown when Confirm("Action_Shutdown"):
                Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 60 /c \"OB2 finished its queue.\"") { CreateNoWindow = true, UseShellExecute = false })?.Dispose();
                ExitApp?.Invoke();
                return;
        }
    }

    private static bool Confirm(string actionKey)
    {
        var window = ConfirmWindow.ForCountdown(Loc.Get(actionKey), CountdownSeconds);
        if (Application.Current.MainWindow is { IsVisible: true } owner)
        {
            window.Owner = owner;
        }

        return window.ShowDialog() == true;
    }

    private void RunCommand(string command)
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", command }, CreateNoWindow = true, UseShellExecute = false })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            LogCommandFailed(ex);
        }
    }

    [LibraryImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetSuspendState([MarshalAs(UnmanagedType.Bool)] bool hibernate, [MarshalAs(UnmanagedType.Bool)] bool force, [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

    [LoggerMessage(Level = LogLevel.Information, Message = "Queue {Queue} finished; completion action {Action}")]
    private partial void LogAction(string queue, CompletionAction action);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Completion command failed to start")]
    private partial void LogCommandFailed(Exception ex);
}
