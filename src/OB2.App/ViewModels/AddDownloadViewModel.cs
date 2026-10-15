using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OB2.App.Infrastructure;
using OB2.App.Services;
using OB2.Core.Downloads;
using OB2.Core.Http;
using OB2.Core.Integrity;
using OB2.Core.Media;
using OB2.Core.Services;
using OB2.Core.Settings;

namespace OB2.App.ViewModels;

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Add Download dialog (§9.2): probes the address as the user types, then pre-fills name,
/// size, category, and folder. Fields the user edits are left alone.
/// </summary>
public sealed partial class AddDownloadViewModel : ObservableObject, IDisposable
{
    private readonly DownloadService _service;
    private readonly AddDownloadPrefill _prefill;
    private CancellationTokenSource? _probeCts;
    private ProbeResult? _probe;
    private string? _probedUrl;
    private bool _nameEdited;
    private bool _categoryEdited;
    private bool _folderEdited;
    private bool _updating;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private Choice<long?>? _selectedCategory;

    [ObservableProperty]
    private string _saveFolder = string.Empty;

    [ObservableProperty]
    private int _connections;

    [ObservableProperty]
    private Choice<long>? _selectedQueue;

    [ObservableProperty]
    private Choice<ChecksumAlgorithm?>? _selectedChecksumAlgorithm;

    [ObservableProperty]
    private string _checksumValue = string.Empty;

    [ObservableProperty]
    private int _speedLimitKBps;

    [ObservableProperty]
    private string _infoText = string.Empty;

    [ObservableProperty]
    private string? _errorText;

    /// <summary>D-71: the address is a YouTube video; the window offers the Download Video window instead.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVideoPage))]
    private Uri? _videoPage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadNowCommand), nameof(DownloadLaterCommand))]
    private bool _isBusy;

    public AddDownloadViewModel(DownloadService service, AppSettings settings, AddDownloadPrefill prefill)
    {
        _service = service;
        _prefill = prefill;
        Connections = settings.Downloads.Connections;

        Categories = [new Choice<long?>(null, Loc.Get("Add_General")), .. service.Categories.Select(c => new Choice<long?>(c.Id, c.Name))];
        Queues = [.. service.QueueList.Select(q => new Choice<long>(q.Id, q.Name))];
        ChecksumAlgorithms =
        [
            new Choice<ChecksumAlgorithm?>(null, Loc.Get("Add_ChecksumNone")),
            new Choice<ChecksumAlgorithm?>(ChecksumAlgorithm.Md5, "MD5"),
            new Choice<ChecksumAlgorithm?>(ChecksumAlgorithm.Sha256, "SHA-256"),
        ];

        _updating = true;
        SelectedCategory = Categories[0];
        SelectedQueue = Queues.FirstOrDefault(q => q.Value == service.DefaultQueueId) ?? Queues.FirstOrDefault();
        SelectedChecksumAlgorithm = ChecksumAlgorithms[0];
        SaveFolder = service.DefaultSaveFolder;
        if (!string.IsNullOrWhiteSpace(prefill.FileNameHint))
        {
            FileName = prefill.FileNameHint;
            ApplyCategoryFor(FileName);
        }

        // After the category rule above, which picks a folder of its own from the name.
        if (!string.IsNullOrWhiteSpace(prefill.SaveFolder))
        {
            SaveFolder = prefill.SaveFolder;
        }

        if (prefill.FileSizeHint is > 0)
        {
            InfoText = Loc.Format("Add_Size", Fmt.Size(prefill.FileSizeHint));
        }

        SourceText = prefill.Source == DownloadSource.Chrome ? Loc.Format("Add_FromBrowser", "Chrome") : null;
        _updating = false;
        Url = prefill.Url ?? string.Empty;
    }

    /// <summary>Raised with the new download, or null when cancelled.</summary>
    public event EventHandler<DownloadItem?>? CloseRequested;

    /// <summary>Asks the user to confirm an address with embedded credentials (§11.1).</summary>
    public Func<bool>? ConfirmCredentials { get; set; }

    public ObservableCollection<Choice<long?>> Categories { get; }

    public ObservableCollection<Choice<long>> Queues { get; }

    public ObservableCollection<Choice<ChecksumAlgorithm?>> ChecksumAlgorithms { get; }

    public string? SourceText { get; }

    public bool IsVideoPage => VideoPage is not null;

    /// <summary>Raised when the user picks "Choose quality…" for a video page.</summary>
    public event EventHandler<Uri>? SwitchToVideoRequested;

    public void Dispose()
    {
        _probeCts?.Cancel();
        _probeCts?.Dispose();
    }

    partial void OnUrlChanged(string value)
    {
        ErrorText = null;
        VideoPage = Uri.TryCreate(value.Trim(), UriKind.Absolute, out var url) ? VideoSites.YouTubeVideo(url) : null;
        if (VideoPage is not null)
        {
            _probeCts?.Cancel(); // the page is not the file; don't probe it
            InfoText = string.Empty;
            return;
        }

        _ = ProbeAfterPauseAsync(value);
    }

    [RelayCommand]
    private void OpenAsVideo()
    {
        if (VideoPage is { } page)
        {
            SwitchToVideoRequested?.Invoke(this, page);
        }
    }

    partial void OnFileNameChanged(string value)
    {
        if (_updating)
        {
            return;
        }

        _nameEdited = true;
        ApplyCategoryFor(value);
    }

    partial void OnSelectedCategoryChanged(Choice<long?>? value)
    {
        if (_updating)
        {
            return;
        }

        _categoryEdited = true;
        if (!_folderEdited)
        {
            Set(() => SaveFolder = _service.SaveFolderFor(value?.Value));
        }
    }

    partial void OnSaveFolderChanged(string value)
    {
        if (!_updating)
        {
            _folderEdited = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private Task DownloadNowAsync() => SubmitAsync(startNow: true);

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private Task DownloadLaterAsync() => SubmitAsync(startNow: false);

    private bool CanSubmit() => !IsBusy;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, null);

    /// <summary>Called by the view after the folder picker.</summary>
    public void ChooseFolder(string folder) => SaveFolder = folder;

    private async Task ProbeAfterPauseAsync(string text)
    {
        _probeCts?.Cancel();
        _probeCts?.Dispose();
        var cts = _probeCts = new CancellationTokenSource();
        var check = UrlValidator.Check(text, out var url);
        if (check is not (UrlCheck.Ok or UrlCheck.HasCredentials))
        {
            InfoText = string.Empty;
            return;
        }

        try
        {
            await Task.Delay(400, cts.Token);
            InfoText = Loc.Get("Add_Probing");
            var probe = await _service.ProbeAsync(Options(url!, startNow: false), cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            _probe = probe;
            _probedUrl = text;
            InfoText = Loc.Format("Add_Size", Fmt.Size(probe.TotalBytes)) + "  ·  " + Loc.Get(probe.SupportsRanges ? "Add_ResumeYes" : "Add_ResumeNo");
            if (!_nameEdited)
            {
                Set(() => FileName = probe.SuggestedFileName);
                ApplyCategoryFor(probe.SuggestedFileName);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or HttpStatusException or TimeoutException or ArgumentException)
        {
            if (!cts.IsCancellationRequested)
            {
                InfoText = string.Empty;
                ErrorText = Loc.Format("Add_ProbeFailed", ex.Message);
            }
        }
    }

    private void ApplyCategoryFor(string? fileName)
    {
        if (_categoryEdited)
        {
            return;
        }

        var category = _service.CategoryFor(fileName);
        Set(() =>
        {
            SelectedCategory = Categories.FirstOrDefault(c => c.Value == category?.Id) ?? Categories[0];
            if (!_folderEdited)
            {
                SaveFolder = _service.SaveFolderFor(category?.Id);
            }
        });
    }

    private async Task SubmitAsync(bool startNow)
    {
        ErrorText = null;
        var check = UrlValidator.Check(Url, out var url);
        switch (check)
        {
            case UrlCheck.TooLong:
                ErrorText = Loc.Get("Add_TooLong");
                return;
            case UrlCheck.HasCredentials when ConfirmCredentials?.Invoke() != true:
                return;
            case UrlCheck.Invalid or UrlCheck.UnsupportedScheme:
                ErrorText = Loc.Get("Add_InvalidUrl");
                return;
        }

        Checksum? checksum = null;
        if (SelectedChecksumAlgorithm?.Value is { } algorithm
            && !Checksum.TryCreate(algorithm.ToString(), ChecksumValue, out checksum))
        {
            ErrorText = Loc.Format("Add_InvalidChecksum", SelectedChecksumAlgorithm.Label);
            return;
        }

        IsBusy = true;
        try
        {
            var options = Options(url!, startNow) with { Checksum = checksum };
            var probe = _probedUrl == Url ? _probe : null;
            var item = await _service.AddAsync(options, probe);
            CloseRequested?.Invoke(this, item);
        }
        catch (Exception ex) when (ex is HttpRequestException or HttpStatusException or TimeoutException or IOException or ArgumentException or UnauthorizedAccessException or OB2.Core.Files.UnsafePathException)
        {
            ErrorText = Loc.Format("Add_ProbeFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private AddDownloadOptions Options(Uri url, bool startNow) => new()
    {
        Url = url,
        FileName = string.IsNullOrWhiteSpace(FileName) ? null : FileName.Trim(),
        FileNameHint = _prefill.FileNameHint,
        SaveFolder = string.IsNullOrWhiteSpace(SaveFolder) ? null : SaveFolder.Trim(),
        CategoryId = SelectedCategory?.Value,
        QueueId = SelectedQueue?.Value,
        Connections = Math.Clamp(Connections, 1, 32),
        SpeedLimitBps = SpeedLimitKBps > 0 ? SpeedLimitKBps * 1024L : null,
        StartNow = startNow,
        Referrer = _prefill.Referrer,
        UserAgent = _prefill.UserAgent,
        Cookies = _prefill.Cookies,
        Headers = _prefill.Headers ?? new Dictionary<string, string>(),
        Source = _prefill.Source,
    };

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
