using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OB2.App.Infrastructure;
using OB2.Core.Downloads;
using OB2.Core.Engine;

namespace OB2.App.ViewModels;

/// <summary>One row of the main list (§9.1) plus the data for the lower details panel.</summary>
public sealed partial class DownloadRowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private DownloadStatus _status;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private long? _totalBytes;

    [ObservableProperty]
    private string _sizeText = string.Empty;

    [ObservableProperty]
    private long _downloadedBytes;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private double _bytesPerSecond;

    [ObservableProperty]
    private string _speedText = string.Empty;

    [ObservableProperty]
    private string _timeLeftText = string.Empty;

    [ObservableProperty]
    private DateTime _dateAdded;

    [ObservableProperty]
    private string _saveFolder = string.Empty;

    [ObservableProperty]
    private string _categoryName = string.Empty;

    [ObservableProperty]
    private string _queueName = string.Empty;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private string _destPath = string.Empty;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private int _activeConnections;

    public DownloadRowViewModel(DownloadItem item, string categoryName, string queueName)
    {
        Id = item.Id;
        Item = item;
        Update(item, categoryName, queueName);
    }

    public long Id { get; }

    public DownloadItem Item { get; private set; }

    public long? CategoryId => Item.CategoryId;

    public long? QueueId => Item.QueueId;

    public long QueuePosition => Item.QueuePosition;

    public bool IsCompleted => Status == DownloadStatus.Completed;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public ObservableCollection<SegmentViewModel> Segments { get; } = [];

    public void Update(DownloadItem item, string categoryName, string queueName)
    {
        Item = item;
        FileName = item.FileName;
        Status = item.Status;
        StatusText = Loc.Status(item.Status);
        TotalBytes = item.TotalBytes;
        SizeText = Fmt.Size(item.TotalBytes);
        DateAdded = item.CreatedAt.LocalDateTime;
        SaveFolder = item.SaveFolder;
        CategoryName = categoryName;
        QueueName = queueName;
        Url = item.FinalUrl ?? item.Url;
        DestPath = item.Status == DownloadStatus.Completed ? item.DestPath : item.PartPath;
        ErrorText = item.Status == DownloadStatus.Failed ? $"{item.ErrorMessage} [{item.ErrorCode}]" : null;
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsCompleted));
        SetBytes(item.Status == DownloadStatus.Completed ? item.TotalBytes ?? item.DownloadedBytes : item.DownloadedBytes);
        if (!item.Status.IsRunning())
        {
            ClearLive();
        }

        if (item.Status == DownloadStatus.Completed)
        {
            foreach (var segment in Segments)
            {
                segment.MarkComplete();
            }
        }
    }

    /// <summary>Live numbers from the engine, 4 times a second.</summary>
    public void Apply(DownloadProgress progress)
    {
        IsActive = true;
        if (progress.Status != Status)
        {
            Status = progress.Status;
            StatusText = Loc.Status(progress.Status);
        }

        if (progress.TotalBytes is { } total && TotalBytes != total)
        {
            TotalBytes = total;
            SizeText = Fmt.Size(total);
        }

        SetBytes(progress.DownloadedBytes);
        BytesPerSecond = progress.BytesPerSecond;
        SpeedText = Fmt.Speed(progress.BytesPerSecond);
        TimeLeftText = Fmt.Duration(progress.Remaining);
        ActiveConnections = progress.ActiveConnections;
        UpdateSegments(progress.Segments);
    }

    public void ClearLive()
    {
        IsActive = false;
        BytesPerSecond = 0;
        SpeedText = string.Empty;
        TimeLeftText = string.Empty;
        ActiveConnections = 0;
    }

    public void LoadSegments(IReadOnlyList<Segment> segments) => UpdateSegments(segments);

    private void SetBytes(long downloaded)
    {
        DownloadedBytes = downloaded;
        Progress = TotalBytes is > 0 ? Math.Min(100, downloaded * 100.0 / TotalBytes.Value) : (Status == DownloadStatus.Completed ? 100 : 0);
        ProgressText = TotalBytes is > 0 || Status == DownloadStatus.Completed
            ? Progress.ToString("0.0", CultureInfo.CurrentCulture) + "%"
            : Fmt.Size(downloaded);
    }

    private void UpdateSegments(IReadOnlyList<Segment> segments)
    {
        if (Segments.Count != segments.Count)
        {
            Segments.Clear();
            foreach (var segment in segments)
            {
                Segments.Add(new SegmentViewModel(segment));
            }

            return;
        }

        for (var i = 0; i < segments.Count; i++)
        {
            Segments[i].Update(segments[i]);
        }
    }
}

/// <summary>One connection bar in the details panel.</summary>
public sealed partial class SegmentViewModel : ObservableObject
{
    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private string _text = string.Empty;

    public SegmentViewModel(Segment segment) => Update(segment);

    public int Number { get; private set; }

    private long? _length;

    /// <summary>The download finished between refreshes: show every byte as received.</summary>
    public void MarkComplete()
    {
        Percent = 100;
        Text = string.Create(CultureInfo.CurrentCulture, $"#{Number}  {Fmt.Size(_length)} / {Fmt.Size(_length)}");
    }

    public void Update(Segment segment)
    {
        _length = segment.Length;
        Number = segment.Index + 1;
        Percent = segment.Length is > 0 ? Math.Min(100, segment.Downloaded * 100.0 / segment.Length.Value) : (segment.IsComplete ? 100 : 0);
        Text = string.Create(CultureInfo.CurrentCulture, $"#{Number}  {Fmt.Size(segment.Downloaded)} / {Fmt.Size(segment.Length)}");
    }
}

/// <summary>A node of the left tree: All, Unfinished, Finished, a queue, or a category.</summary>
public sealed partial class NavNodeViewModel : ObservableObject
{
    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private string _display = string.Empty;

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isSelected;

    public NavNodeViewModel(NavKind kind, string name, long? id = null, Func<DownloadRowViewModel, bool>? filter = null)
    {
        Kind = kind;
        Name = name;
        Id = id;
        Filter = filter;
        Display = name;
    }

    public NavKind Kind { get; }

    public string Name { get; set; }

    public long? Id { get; }

    public bool IsRunning { get; set; }

    /// <summary>Null for group headers ("Queues", "Categories").</summary>
    public Func<DownloadRowViewModel, bool>? Filter { get; }

    public ObservableCollection<NavNodeViewModel> Children { get; } = [];

    public void SetCount(int count)
    {
        Count = count;
        var running = IsRunning ? " · " + Loc.Get("Nav_Running") : string.Empty;
        Display = Filter is null ? Name : string.Create(CultureInfo.CurrentCulture, $"{Name} ({count}){running}");
    }
}

public enum NavKind
{
    All,
    Unfinished,
    Finished,
    QueuesGroup,
    Queue,
    CategoriesGroup,
    Category,
}
