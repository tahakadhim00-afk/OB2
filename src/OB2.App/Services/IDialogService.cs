using OB2.Core.Downloads;

namespace OB2.App.Services;

/// <summary>What a browser capture, the CLI hand-off, or drag-and-drop pre-fills in the Add dialog.</summary>
public sealed record AddDownloadPrefill
{
    public string? Url { get; init; }

    public string? FileNameHint { get; init; }

    public string? Referrer { get; init; }

    public string? Cookies { get; init; }

    public string? UserAgent { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public long? FileSizeHint { get; init; }

    public DownloadSource Source { get; init; } = DownloadSource.Manual;

    /// <summary>The address is a video page for yt-dlp (D-71): opens the Download Video window.</summary>
    public bool MediaPage { get; init; }
}

/// <summary>Opens windows and shell actions so view models stay free of WPF window code.</summary>
public interface IDialogService
{
    /// <returns>The added download, or null if the user cancelled.</returns>
    /// <remarks>A video page (YouTube, or <see cref="AddDownloadPrefill.MediaPage"/>) opens the Download Video window instead.</remarks>
    DownloadItem? ShowAddDownload(AddDownloadPrefill prefill);

    /// <returns>Null if cancelled; otherwise whether files should be deleted too.</returns>
    bool? ConfirmDelete(int count, bool anyCompleted);

    void ShowSettings();

    void ShowScheduler(long? queueId);

    void ShowComplete(DownloadItem item);

    void OpenFile(string path);

    void OpenFolder(string path);

    void CopyText(string text);
}
