using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OB2.App.Infrastructure;
using OB2.App.Services;
using OB2.Core.Downloads;
using OB2.Core.Files;
using OB2.Core.Media;
using OB2.Core.Services;

namespace OB2.App.ViewModels;

public enum MediaDialogState
{
    NeedsTools,
    InstallingTools,
    Loading,
    Ready,
    Failed,
}

/// <summary>
/// Download Video window (D-71): installs yt-dlp, Deno and ffmpeg on first use, asks yt-dlp what
/// the page offers, and adds the chosen picture quality and audio track as a download. D-74: a
/// chosen subtitle track becomes a download of its own beside the video, or the only one.
/// </summary>
public sealed partial class MediaDownloadViewModel : ObservableObject, IDisposable
{
    private readonly DownloadService _service;
    private readonly MediaInspector _inspector;
    private readonly MediaToolInstaller _installer;
    private readonly AddDownloadPrefill _prefill;
    private readonly Uri _pageUrl;
    private readonly CancellationTokenSource _cts = new();
    private MediaInfo? _info;

    /// <summary>The choice <see cref="FileName"/> was made for, to find the title in a name the user edited.</summary>
    private MediaSelection? _named;
    private bool _nameEdited;
    private bool _folderEdited;
    private bool _audioEdited;
    private bool _updating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNeedsTools), nameof(IsInstalling), nameof(IsLoading), nameof(IsReady), nameof(IsFailed))]
    [NotifyCanExecuteChangedFor(nameof(DownloadNowCommand), nameof(DownloadLaterCommand))]
    private MediaDialogState _state;

    [ObservableProperty]
    private string _videoTitle = string.Empty;

    [ObservableProperty]
    private string? _durationText;

    [ObservableProperty]
    private Choice<MediaFormat>? _selectedVideo;

    [ObservableProperty]
    private Choice<MediaFormat>? _selectedAudio;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVideoEnabled))]
    private bool _audioOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSubtitleChosen))]
    private Choice<SubtitleTrack?>? _selectedSubtitle;

    [ObservableProperty]
    private Choice<string>? _selectedSubtitleFormat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVideoEnabled), nameof(IsAudioOnlyEnabled))]
    private bool _subtitlesOnly;

    [ObservableProperty]
    private bool _hasSubtitles;

    /// <summary>"Added as its own download: Clip.en.srt" while subtitles go with the video.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSubtitleNameVisible))]
    private string? _subtitleNameText;

    [ObservableProperty]
    private bool _isAudioEnabled = true;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _saveFolder = string.Empty;

    [ObservableProperty]
    private string _sizeText = string.Empty;

    [ObservableProperty]
    private string _installText = string.Empty;

    [ObservableProperty]
    private double _installPercent;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadNowCommand), nameof(DownloadLaterCommand))]
    private bool _isBusy;

    public MediaDownloadViewModel(DownloadService service, MediaInspector inspector, MediaToolInstaller installer, AddDownloadPrefill prefill, Uri pageUrl)
    {
        _service = service;
        _inspector = inspector;
        _installer = installer;
        _prefill = prefill;
        _pageUrl = pageUrl;
        PageText = pageUrl.AbsoluteUri;
        ToolsText = Loc.Format("Media_ToolsBody", Fmt.Size(MediaToolInstaller.ApproximateTotalBytes), installer.Tools.Folder);
        SubtitleFormats = [.. SubtitleTrack.Formats.Select(f => new Choice<string>(f, f.ToUpperInvariant()))];
        Set(() => SelectedSubtitleFormat = SubtitleFormats[0]);
        SaveFolder = _service.SaveFolderFor(_service.VideoCategory?.Id);
        _folderEdited = false;

        if (inspector.Tools.IsInstalled)
        {
            _ = LoadAsync();
        }
        else
        {
            State = MediaDialogState.NeedsTools;
        }
    }

    /// <summary>Raised with the new download, or null when cancelled.</summary>
    public event EventHandler<DownloadItem?>? CloseRequested;

    public string PageText { get; }

    public string ToolsText { get; }

    public ObservableCollection<Choice<MediaFormat>> Videos { get; } = [];

    public ObservableCollection<Choice<MediaFormat>> Audios { get; } = [];

    /// <summary>D-74: "None" first, then the tracks the page has.</summary>
    public ObservableCollection<Choice<SubtitleTrack?>> Subtitles { get; } = [];

    public IReadOnlyList<Choice<string>> SubtitleFormats { get; }

    public bool IsNeedsTools => State == MediaDialogState.NeedsTools;

    public bool IsInstalling => State == MediaDialogState.InstallingTools;

    public bool IsLoading => State == MediaDialogState.Loading;

    public bool IsReady => State == MediaDialogState.Ready;

    public bool IsFailed => State == MediaDialogState.Failed;

    public bool IsVideoEnabled => !AudioOnly && !SubtitlesOnly;

    public bool IsAudioOnlyEnabled => !SubtitlesOnly;

    public bool IsSubtitleChosen => SelectedSubtitle?.Value is not null;

    public bool IsSubtitleNameVisible => SubtitleNameText is not null;

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    /// <summary>Called by the view after the folder picker.</summary>
    public void ChooseFolder(string folder) => SaveFolder = folder;

    partial void OnSelectedVideoChanged(Choice<MediaFormat>? value)
    {
        if (_updating)
        {
            return;
        }

        if (!_audioEdited && _info is not null)
        {
            Set(() => SelectedAudio = Audios.FirstOrDefault(a => a.Value == MediaChoices.AudioFor(value?.Value, Audios.Select(c => c.Value).ToList())) ?? Audios.FirstOrDefault());
        }

        UpdateSelection();
    }

    partial void OnSelectedAudioChanged(Choice<MediaFormat>? value)
    {
        if (!_updating)
        {
            _audioEdited = true;
            UpdateSelection();
        }
    }

    partial void OnAudioOnlyChanged(bool value) => UpdateSelection();

    partial void OnSelectedSubtitleChanged(Choice<SubtitleTrack?>? value)
    {
        if (value?.Value is null)
        {
            SubtitlesOnly = false;
        }

        if (!_updating)
        {
            UpdateSelection();
        }
    }

    partial void OnSelectedSubtitleFormatChanged(Choice<string>? value)
    {
        if (!_updating)
        {
            UpdateSelection();
        }
    }

    partial void OnSubtitlesOnlyChanged(bool value)
    {
        if (!_updating)
        {
            UpdateSelection();
        }
    }

    partial void OnFileNameChanged(string value)
    {
        if (!_updating)
        {
            _nameEdited = true;
            UpdateSubtitleName();
        }
    }

    partial void OnSaveFolderChanged(string value)
    {
        if (!_updating)
        {
            _folderEdited = true;
        }
    }

    [RelayCommand]
    private async Task InstallToolsAsync()
    {
        ErrorText = null;
        State = MediaDialogState.InstallingTools;
        try
        {
            await _installer.InstallAsync(InstallProgress(), _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ErrorText = Loc.Format("Media_ToolsFailed", ex.Message);
            State = MediaDialogState.NeedsTools;
            return;
        }

        await LoadAsync();
    }

    /// <summary>Sites change often; a newer yt-dlp is the usual fix (also offered after a failure).</summary>
    [RelayCommand]
    private async Task UpdateYtDlpAsync()
    {
        ErrorText = null;
        State = MediaDialogState.InstallingTools;
        try
        {
            await _installer.InstallYtDlpAsync(InstallProgress(), _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ErrorText = Loc.Format("Media_ToolsFailed", ex.Message);
            State = MediaDialogState.Failed;
            return;
        }

        await LoadAsync();
    }

    [RelayCommand]
    private Task RetryAsync() => LoadAsync();

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private Task DownloadNowAsync() => SubmitAsync(startNow: true);

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private Task DownloadLaterAsync() => SubmitAsync(startNow: false);

    private bool CanSubmit() => State == MediaDialogState.Ready && !IsBusy;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, null);

    private Progress<ToolDownloadProgress> InstallProgress() => new(p =>
    {
        InstallText = Loc.Format("Media_ToolsProgress", p.Tool, Fmt.Size(p.Received), Fmt.Size(p.Total), Fmt.Speed(p.BytesPerSecond));
        InstallPercent = p.Total is > 0 ? p.Received * 100.0 / p.Total.Value : 0;
    });

    private async Task LoadAsync()
    {
        ErrorText = null;
        State = MediaDialogState.Loading;
        try
        {
            var info = await _inspector.InspectAsync(_pageUrl, _cts.Token);
            if (info.Formats.Count == 0)
            {
                throw new MediaException(Loc.Get("Media_NoFormats"));
            }

            Show(info);
            State = MediaDialogState.Ready;
        }
        catch (OperationCanceledException)
        {
        }
        catch (MediaException ex)
        {
            ErrorText = Loc.Format("Media_Failed", ex.Message);
            State = MediaDialogState.Failed;
        }
    }

    private void Show(MediaInfo info)
    {
        _info = info;
        VideoTitle = info.Title;
        DurationText = info.Duration is { } d ? Loc.Format("Media_Duration", d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss", CultureInfo.CurrentCulture) : d.ToString(@"m\:ss", CultureInfo.CurrentCulture)) : null;

        Set(() =>
        {
            Videos.Clear();
            foreach (var video in MediaChoices.Videos(info))
            {
                Videos.Add(new Choice<MediaFormat>(video, VideoLabel(video)));
            }

            Audios.Clear();
            foreach (var audio in MediaChoices.Audios(info))
            {
                Audios.Add(new Choice<MediaFormat>(audio, AudioLabel(audio)));
            }

            Subtitles.Clear();
            Subtitles.Add(new Choice<SubtitleTrack?>(null, Loc.Get(info.Subtitles.Count > 0 ? "Media_SubtitlesNone" : "Media_SubtitlesNoneFound")));
            foreach (var track in info.Subtitles)
            {
                Subtitles.Add(new Choice<SubtitleTrack?>(track, track.Automatic ? Loc.Format("Media_SubtitleAuto", track.Name) : track.Name));
            }

            HasSubtitles = info.Subtitles.Count > 0;
            SelectedSubtitle = Subtitles[0];

            SelectedVideo = Videos.FirstOrDefault();
            SelectedAudio = Audios.FirstOrDefault(a => a.Value == MediaChoices.AudioFor(SelectedVideo?.Value, Audios.Select(c => c.Value).ToList())) ?? Audios.FirstOrDefault();
            AudioOnly = Videos.Count == 0;
            Preselect(_prefill.MediaFormat, _prefill.MediaContainer);
        });
        UpdateSelection();
    }

    /// <summary>
    /// D-72: the quality picked on the browser's button (<c>137+140</c>, or <c>140</c> for sound
    /// alone). D-74: or subtitles (<c>subs:en</c>) in the format picked with them.
    /// </summary>
    private void Preselect(string? formatSpec, string? container)
    {
        if (SubtitleTrack.IsSpec(formatSpec))
        {
            if (Subtitles.FirstOrDefault(s => s.Value?.Spec == formatSpec) is { } track)
            {
                SelectedSubtitle = track;
                SelectedSubtitleFormat = SubtitleFormats.FirstOrDefault(f => f.Value == container) ?? SelectedSubtitleFormat;
                SubtitlesOnly = true;
            }

            return;
        }

        var ids = formatSpec?.Split('+') ?? [];
        var video = ids.Length > 0 ? Videos.FirstOrDefault(v => v.Value.Id == ids[0]) : null;
        var audio = ids.Length > 0 ? Audios.FirstOrDefault(a => a.Value.Id == ids[^1]) : null;
        if (video is not null)
        {
            SelectedVideo = video;
            AudioOnly = false;
        }
        else if (audio is not null && ids.Length == 1)
        {
            AudioOnly = true;
        }

        if (audio is not null)
        {
            SelectedAudio = audio;
        }
    }

    /// <summary>Keeps the file name's extension, the size and the folder in step with the choice.</summary>
    private void UpdateSelection()
    {
        IsAudioEnabled = !SubtitlesOnly && (AudioOnly || SelectedVideo?.Value is not { HasAudio: true });
        if (MainSelection() is not { } selection)
        {
            SizeText = string.Empty;
            SubtitleNameText = null;
            return;
        }

        SizeText = selection.IsSubtitles ? string.Empty
            : selection.EstimatedSize is { } size ? Loc.Format("Media_Size", Fmt.Size(size))
            : Loc.Get("Media_SizeUnknown");
        Set(() =>
        {
            FileName = selection.FileName(_nameEdited && FileName.Length > 0 ? (_named ?? selection).Stem(FileName) : FileNameSanitizer.Sanitize(_info?.Title ?? "video"));
            _named = selection;
            if (!_folderEdited)
            {
                SaveFolder = _service.SaveFolderFor(_service.CategoryFor(selection, FileName)?.Id);
            }
        });
        UpdateSubtitleName();
    }

    /// <summary>D-74: names the subtitles' own download while they go with the video.</summary>
    private void UpdateSubtitleName() =>
        SubtitleNameText = !SubtitlesOnly && SubtitleSelection() is { } subtitles && _named is { } named
            ? Loc.Format("Media_SubtitlesBeside", FileNameSanitizer.Sanitize(subtitles.FileName(StemFor(named))))
            : null;

    /// <summary>The title part of "Save as", for the downloads <paramref name="named"/> and anything added beside it.</summary>
    private string StemFor(MediaSelection named) => named.Stem(string.IsNullOrWhiteSpace(FileName) ? "video" : FileName.Trim());

    /// <summary>What "Save as" names: the picture and sound, or the subtitles when they are all that is wanted.</summary>
    private MediaSelection? MainSelection() => SubtitlesOnly ? SubtitleSelection() : Selection();

    private MediaSelection? Selection()
    {
        var video = AudioOnly ? null : SelectedVideo?.Value;
        var audio = SelectedAudio?.Value;
        return video is null && audio is null ? null : MediaChoices.Select(video, audio);
    }

    private MediaSelection? SubtitleSelection() =>
        SelectedSubtitle?.Value is { } track && SelectedSubtitleFormat is { } format ? MediaChoices.Select(track, format.Value) : null;

    private async Task SubmitAsync(bool startNow)
    {
        if (MainSelection() is not { } selection)
        {
            return;
        }

        ErrorText = null;
        IsBusy = true;
        try
        {
            // The extension follows the choice even if the user typed another one.
            var stem = StemFor(selection);
            var folder = string.IsNullOrWhiteSpace(SaveFolder) ? null : SaveFolder.Trim();
            var item = await AddAsync(selection, stem, folder, startNow);
            if (!SubtitlesOnly && SubtitleSelection() is { } subtitles)
            {
                // D-74: a download of their own, beside the video and named after it.
                await AddAsync(subtitles, stem, item.SaveFolder, startNow);
            }

            CloseRequested?.Invoke(this, item);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or UnsafePathException)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task<DownloadItem> AddAsync(MediaSelection selection, string stem, string? folder, bool startNow) =>
        _service.AddMediaAsync(
            new AddDownloadOptions
            {
                Url = _pageUrl,
                FileName = FileNameSanitizer.Sanitize(selection.FileName(stem)),
                SaveFolder = folder,
                StartNow = startNow,
                Referrer = _prefill.Referrer,
                Source = _prefill.Source,
                MediaFormat = selection.FormatSpec,
                MediaContainer = selection.Container,
            },
            selection.EstimatedSize);

    private static string VideoLabel(MediaFormat f)
    {
        var parts = new List<string> { f.Quality, f.VideoCodecName is { } codec ? $"{f.Ext.ToUpperInvariant()} ({codec})" : f.Ext.ToUpperInvariant() };
        if (f.HasAudio && f.AudioCodec is not null)
        {
            parts.Add(Loc.Get("Media_WithSound"));
        }

        if (f.Size is { } size)
        {
            parts.Add(Fmt.Size(size));
        }

        return string.Join("  ·  ", parts);
    }

    private static string AudioLabel(MediaFormat f)
    {
        var parts = new List<string>();
        if ((f.Note ?? f.Language) is { Length: > 0 } name)
        {
            parts.Add(name);
        }

        parts.Add(f.AudioCodecName is { } codec ? $"{f.Ext.ToUpperInvariant()} ({codec})" : f.Ext.ToUpperInvariant());
        if ((f.AudioBitrate ?? f.Bitrate) is { } kbps)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{Math.Round(kbps)} kbit/s"));
        }

        if (f.Size is { } size)
        {
            parts.Add(Fmt.Size(size));
        }

        return string.Join("  ·  ", parts);
    }

    private void Set(Action change)
    {
        var was = _updating;
        _updating = true;
        try
        {
            change();
        }
        finally
        {
            _updating = was;
        }
    }
}
