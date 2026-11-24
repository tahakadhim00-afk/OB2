using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OB2.App.Infrastructure;
using OB2.Core.Media;

namespace OB2.App.ViewModels;

/// <summary>
/// D-89: the first-run window's first step. The video tools (yt-dlp, Deno, ffmpeg) are installed
/// before the save folder is chosen, so the Download Video window is ready the first time it is
/// opened. The owner made the step required: the window goes on to the folder only once all
/// three are installed. Closing the window stops the download (kept for next time) and the
/// first-run window comes back at the next start, as it always did when closed.
/// </summary>
public sealed partial class FirstRunToolsViewModel : ObservableObject, IDisposable
{
    private readonly MediaToolInstaller _installer;
    private readonly CancellationTokenSource _closing = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyPropertyChangedFor(nameof(InstallLabel))]
    private bool _isInstalling;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallLabel))]
    private string? _errorText;

    public FirstRunToolsViewModel(MediaToolInstaller installer)
    {
        _installer = installer;
        var tools = installer.Tools;
        Tools =
        [
            new ToolRowViewModel("yt-dlp", Loc.Get("FirstRun_ToolYtDlp"), () => File.Exists(tools.YtDlpPath)),
            new ToolRowViewModel("Deno", Loc.Get("FirstRun_ToolDeno"), () => File.Exists(tools.DenoPath)),
            new ToolRowViewModel("ffmpeg", Loc.Get("FirstRun_ToolFfmpeg"), () => File.Exists(tools.FfmpegPath)),
        ];
        BodyText = Loc.Format("FirstRun_ToolsBody", Fmt.Size(MediaToolInstaller.ApproximateTotalBytes), tools.Folder);
    }

    /// <summary>Raised once all three tools are installed: the window moves on to the save folder.</summary>
    public event EventHandler? Completed;

    public ObservableCollection<ToolRowViewModel> Tools { get; }

    public string BodyText { get; }

    /// <summary>Nothing to do: every tool is already there (a copy whose tools folder came along).</summary>
    public bool IsInstalled => _installer.Tools.IsInstalled;

    /// <summary>"Download the tools", or "Try again" after a failure.</summary>
    public string InstallLabel => Loc.Get(ErrorText is null ? "Media_ToolsInstall" : "FirstRun_ToolsRetry");

    public void Dispose()
    {
        _closing.Cancel(); // the partial download is kept and continues next time
        _closing.Dispose();
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        ErrorText = null;
        IsInstalling = true;
        try
        {
            await _installer.InstallAsync(new Progress<ToolDownloadProgress>(OnProgress), _closing.Token);
        }
        catch (OperationCanceledException)
        {
            return; // the window closed
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ErrorText = Loc.Format("Media_ToolsFailed", ex.Message);
            return;
        }
        finally
        {
            IsInstalling = false;
            foreach (var tool in Tools)
            {
                tool.Refresh();
            }
        }

        if (IsInstalled)
        {
            Completed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool CanInstall() => !IsInstalling;

    /// <summary>The installer reports one tool at a time, in order; the ones before it are done.</summary>
    private void OnProgress(ToolDownloadProgress progress)
    {
        foreach (var tool in Tools)
        {
            if (tool.Name == progress.Tool)
            {
                tool.Show(progress);
                break;
            }

            tool.Refresh();
        }
    }
}

/// <summary>One tool in the first-run step: what it is for, and how far its download is.</summary>
public sealed partial class ToolRowViewModel : ObservableObject
{
    private readonly Func<bool> _isInstalled;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private string _statusText;

    public ToolRowViewModel(string name, string purpose, Func<bool> isInstalled)
    {
        Name = name;
        Purpose = purpose;
        _isInstalled = isInstalled;
        _statusText = string.Empty;
        Refresh();
    }

    public string Name { get; }

    public string Purpose { get; }

    /// <summary>Installed, or waiting its turn.</summary>
    public void Refresh()
    {
        var installed = _isInstalled();
        Percent = installed ? 100 : Percent;
        StatusText = Loc.Get(installed ? "FirstRun_ToolInstalled" : "FirstRun_ToolWaiting");
    }

    public void Show(ToolDownloadProgress progress)
    {
        Percent = progress.Total is > 0 ? progress.Received * 100.0 / progress.Total.Value : 0;
        StatusText = progress switch
        {
            { Total: > 0 } when progress.Received >= progress.Total => Loc.Get("FirstRun_ToolUnpacking"), // checksum, then unzip: a few seconds at 100 %
            { Total: > 0 } => Loc.Format("FirstRun_ToolProgress", Fmt.Size(progress.Received), Fmt.Size(progress.Total), Fmt.Speed(progress.BytesPerSecond)),
            { Received: 0 } => Loc.Get("FirstRun_ToolStarting"), // before the server has said how large it is
            _ => Loc.Format("FirstRun_ToolProgressNoTotal", Fmt.Size(progress.Received), Fmt.Speed(progress.BytesPerSecond)),
        };
    }
}
