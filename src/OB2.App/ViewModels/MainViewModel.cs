using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OB2.App.Infrastructure;
using OB2.App.Services;
using OB2.Core.Downloads;
using OB2.Core.Organization;
using OB2.Core.Services;
using OB2.Core.Settings;

namespace OB2.App.ViewModels;

/// <summary>The main window (§9.1): toolbar commands, left tree, list, details panel, status bar.</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly DownloadService _service;
    private readonly AppSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<long, DownloadRowViewModel> _rows = [];
    private Dictionary<long, string> _categoryNames = [];
    private Dictionary<long, string> _queueNames = [];
    private IReadOnlyList<DownloadRowViewModel> _selectedRows = [];

    [ObservableProperty]
    private NavNodeViewModel? _selectedNode;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private DownloadRowViewModel? _selectedRow;

    [ObservableProperty]
    private string _totalSpeedText = string.Empty;

    [ObservableProperty]
    private string _activeCountText = string.Empty;

    [ObservableProperty]
    private string _queueStatusText = string.Empty;

    [ObservableProperty]
    private string _trayStateText = string.Empty;

    [ObservableProperty]
    private bool _isListEmpty = true;

    public MainViewModel(DownloadService service, AppSettings settings, IDialogService dialogs)
    {
        _service = service;
        _settings = settings;
        _dialogs = dialogs;
        _dispatcher = Dispatcher.CurrentDispatcher;

        DownloadsView = CollectionViewSource.GetDefaultView(Downloads);
        DownloadsView.Filter = o => o is DownloadRowViewModel row && Matches(row);
        DownloadsView.SortDescriptions.Add(new SortDescription(nameof(DownloadRowViewModel.DateAdded), ListSortDirection.Descending));

        ReloadNames();
        BuildNav();
        foreach (var item in _service.List())
        {
            Upsert(item);
        }

        _service.Engine.StatusChanged += OnStatusChanged;
        _service.Queues.StateChanged += OnQueuesChanged;

        // NFR Performance: the UI refreshes 4 times a second.
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Refresh(), _dispatcher);
        _timer.Start();
        Refresh();
    }

    public ObservableCollection<DownloadRowViewModel> Downloads { get; } = [];

    public ICollectionView DownloadsView { get; }

    public ObservableCollection<NavNodeViewModel> Nav { get; } = [];

    /// <summary>Total speed and active count for the tray tooltip.</summary>
    public (int Active, double BytesPerSecond) Totals { get; private set; }

    /// <summary>Set by the view from the DataGrid's multi-selection.</summary>
    public IReadOnlyList<DownloadRowViewModel> SelectedRows
    {
        get => _selectedRows;
        set
        {
            _selectedRows = value;
            NotifyCommands();
        }
    }

    private IEnumerable<long> SelectedIds => _selectedRows.Select(r => r.Id);

    private long? SelectedQueueId => SelectedNode is { Kind: NavKind.Queue, Id: { } id } ? id : null;

    public void Dispose()
    {
        _timer.Stop();
        _service.Engine.StatusChanged -= OnStatusChanged;
        _service.Queues.StateChanged -= OnQueuesChanged;
    }

    [RelayCommand]
    private void AddUrl(string? url) => _dialogs.ShowAddDownload(new AddDownloadPrefill { Url = url ?? ClipboardUrl() });

    public void AddFromPrefill(AddDownloadPrefill prefill) => _dialogs.ShowAddDownload(prefill);

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume() => _service.Start(SelectedIds);

    private bool CanResume() => _selectedRows.Any(r => !r.IsActive && r.Status != DownloadStatus.Completed);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => _service.StopAsync(_selectedRows.Where(r => r.IsActive).Select(r => r.Id));

    private bool CanStop() => _selectedRows.Any(r => r.IsActive);

    /// <summary>Space (§9.6): pause the selection if anything in it runs, otherwise resume it.</summary>
    [RelayCommand]
    private Task ToggleSelectedAsync()
    {
        if (CanStop())
        {
            return StopAsync();
        }

        Resume();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task StopAllAsync() => _service.StopAllAsync();

    [RelayCommand]
    private void ResumeAll() => _service.StartAll();

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        var rows = _selectedRows.ToList();
        var deleteFiles = _dialogs.ConfirmDelete(rows.Count, rows.Any(r => r.IsCompleted));
        if (deleteFiles is null)
        {
            return;
        }

        await _service.DeleteAsync(rows.Select(r => r.Id), deleteFiles.Value);
    }

    private bool HasSelection() => _selectedRows.Count > 0;

    [RelayCommand]
    private void Options()
    {
        _dialogs.ShowSettings();
        ReloadNames();
        BuildNav();
        RefreshRowNames();
    }

    [RelayCommand]
    private void Scheduler()
    {
        _dialogs.ShowScheduler(SelectedQueueId);
        ReloadNames();
        BuildNav();
        RefreshRowNames();
    }

    [RelayCommand]
    private void StartQueue() => _service.Queues.StartQueue(SelectedQueueId ?? _service.DefaultQueueId);

    [RelayCommand]
    private Task StopQueueAsync() => _service.Queues.StopQueueAsync(SelectedQueueId ?? _service.DefaultQueueId);

    [RelayCommand(CanExecute = nameof(CanOpenFile))]
    private void OpenFile() => _dialogs.OpenFile(SelectedRow!.DestPath);

    private bool CanOpenFile() => SelectedRow is { IsCompleted: true };

    [RelayCommand(CanExecute = nameof(HasSelectedRow))]
    private void OpenFolder() => _dialogs.OpenFolder(SelectedRow!.DestPath);

    [RelayCommand(CanExecute = nameof(HasSelectedRow))]
    private void CopyUrl() => _dialogs.CopyText(SelectedRow!.Url);

    private bool HasSelectedRow() => SelectedRow is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedRow))]
    private void MoveUp() => MoveSelected(-1);

    [RelayCommand(CanExecute = nameof(HasSelectedRow))]
    private void MoveDown() => MoveSelected(+1);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DownloadLater() => _service.Enqueue(SelectedIds);

    [RelayCommand]
    private void MoveToQueue(long queueId)
    {
        foreach (var id in SelectedIds)
        {
            _service.Assign(id, queueId, null);
        }
    }

    public IReadOnlyList<QueueDefinition> QueueList => _service.QueueList;

    partial void OnSelectedNodeChanged(NavNodeViewModel? value)
    {
        using (DownloadsView.DeferRefresh())
        {
            DownloadsView.SortDescriptions.Clear();
            DownloadsView.SortDescriptions.Add(value?.Kind == NavKind.Queue
                ? new SortDescription(nameof(DownloadRowViewModel.QueuePosition), ListSortDirection.Ascending)
                : new SortDescription(nameof(DownloadRowViewModel.DateAdded), ListSortDirection.Descending));
        }

        UpdateEmpty();
    }

    partial void OnSearchTextChanged(string value)
    {
        DownloadsView.Refresh();
        UpdateEmpty();
    }

    partial void OnSelectedRowChanged(DownloadRowViewModel? value)
    {
        if (value is not null && !value.IsActive)
        {
            value.LoadSegments(_service.Engine.GetProgress(value.Id)?.Segments ?? LoadSavedSegments(value.Id));
        }

        NotifyCommands();
    }

    private IReadOnlyList<Segment> LoadSavedSegments(long id) =>
        _service.Engine.GetProgress(id)?.Segments ?? _service.SegmentsOf(id);

    private bool Matches(DownloadRowViewModel row)
    {
        if (SelectedNode?.Filter is { } filter && !filter(row))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(SearchText)
            || row.FileName.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            || row.Url.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private void MoveSelected(int delta)
    {
        var row = SelectedRow!;
        var siblings = Downloads.Where(r => r.QueueId == row.QueueId).OrderBy(r => r.QueuePosition).ThenBy(r => r.Id).ToList();
        var index = siblings.IndexOf(row);
        _service.Queues.Move(row.Id, Math.Clamp(index + delta, 0, siblings.Count - 1));
        foreach (var sibling in siblings)
        {
            if (_service.Get(sibling.Id) is { } fresh)
            {
                Upsert(fresh);
            }
        }

        DownloadsView.Refresh();
    }

    private void Refresh()
    {
        var active = 0;
        double speed = 0;
        foreach (var row in Downloads)
        {
            var progress = _service.Engine.GetProgress(row.Id);
            if (progress is not null)
            {
                row.Apply(progress);
                active++;
                speed += progress.BytesPerSecond;
            }
            else if (row.IsActive)
            {
                row.ClearLive();
            }
        }

        Totals = (active, speed);
        TotalSpeedText = Loc.Format("StatusBar_Speed", Fmt.Speed(speed) is { Length: > 0 } s ? s : "0 B/s");
        ActiveCountText = Loc.Format("StatusBar_Active", active);
        // Cached names: this runs 4 times a second and must not touch the database.
        var running = _queueNames.Where(q => _service.Queues.IsRunning(q.Key)).Select(q => q.Value).ToList();
        QueueStatusText = running.Count == 0 ? Loc.Get("StatusBar_NoQueues") : Loc.Format("StatusBar_Queues", string.Join(", ", running));
        TrayStateText = Loc.Get(_settings.General.MinimizeToTrayOnClose ? "StatusBar_Tray" : "StatusBar_NoTray");
        UpdateCounts(Nav);
        if (SelectedRow is { IsActive: true })
        {
            NotifyCommands();
        }
    }

    private void UpdateCounts(IEnumerable<NavNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Kind == NavKind.Queue && node.Id is { } id)
            {
                node.IsRunning = _service.Queues.IsRunning(id);
            }

            node.SetCount(node.Filter is { } f ? Downloads.Count(f) : 0);
            UpdateCounts(node.Children);
        }
    }

    private void OnStatusChanged(object? sender, DownloadItem item) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (item.Status == DownloadStatus.Cancelled && _service.Get(item.Id) is null)
            {
                Remove(item.Id);
            }
            else
            {
                Upsert(item);
            }
        });

    private void OnQueuesChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(Refresh);

    private void Upsert(DownloadItem item)
    {
        var category = item.CategoryId is { } c && _categoryNames.TryGetValue(c, out var cn) ? cn : string.Empty;
        var queue = item.QueueId is { } q && _queueNames.TryGetValue(q, out var qn) ? qn : string.Empty;
        if (_rows.TryGetValue(item.Id, out var row))
        {
            var positionChanged = row.QueuePosition != item.QueuePosition;
            row.Update(item, category, queue);
            if (positionChanged)
            {
                DownloadsView.Refresh();
            }
        }
        else
        {
            row = new DownloadRowViewModel(item, category, queue);
            _rows[item.Id] = row;
            Downloads.Add(row);
        }

        if (row == SelectedRow)
        {
            NotifyCommands();
        }

        UpdateEmpty();
    }

    private void Remove(long id)
    {
        if (_rows.Remove(id, out var row))
        {
            Downloads.Remove(row);
            if (SelectedRow == row)
            {
                SelectedRow = null;
            }
        }

        UpdateEmpty();
    }

    private void UpdateEmpty() => IsListEmpty = DownloadsView.IsEmpty;

    private void ReloadNames()
    {
        _categoryNames = _service.Categories.ToDictionary(c => c.Id, c => c.Name);
        _queueNames = _service.QueueList.ToDictionary(q => q.Id, q => q.Name);
        OnPropertyChanged(nameof(QueueList));
    }

    private void RefreshRowNames()
    {
        foreach (var row in Downloads.ToList())
        {
            if (_service.Get(row.Id) is { } item)
            {
                Upsert(item);
            }
        }
    }

    private void BuildNav()
    {
        var selected = SelectedNode is { } s ? (s.Kind, s.Id) : (NavKind.All, (long?)null);
        Nav.Clear();
        Nav.Add(new NavNodeViewModel(NavKind.All, Loc.Get("Nav_All"), filter: _ => true));
        Nav.Add(new NavNodeViewModel(NavKind.Unfinished, Loc.Get("Nav_Unfinished"), filter: r => r.Status != DownloadStatus.Completed));
        Nav.Add(new NavNodeViewModel(NavKind.Finished, Loc.Get("Nav_Finished"), filter: r => r.Status == DownloadStatus.Completed));

        var queues = new NavNodeViewModel(NavKind.QueuesGroup, Loc.Get("Nav_Queues"));
        foreach (var queue in _service.QueueList)
        {
            var id = queue.Id;
            queues.Children.Add(new NavNodeViewModel(NavKind.Queue, queue.Name, id, r => r.QueueId == id));
        }

        Nav.Add(queues);

        var categories = new NavNodeViewModel(NavKind.CategoriesGroup, Loc.Get("Nav_Categories"));
        foreach (var category in _service.Categories)
        {
            var id = category.Id;
            categories.Children.Add(new NavNodeViewModel(NavKind.Category, category.Name, id, r => r.CategoryId == id));
        }

        Nav.Add(categories);

        var match = Flatten(Nav).FirstOrDefault(n => n.Kind == selected.Item1 && n.Id == selected.Item2) ?? Nav[0];
        match.IsSelected = true;
        SelectedNode = match;
        DownloadsView.Refresh();
    }

    private static IEnumerable<NavNodeViewModel> Flatten(IEnumerable<NavNodeViewModel> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

    private void NotifyCommands()
    {
        ResumeCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        OpenFileCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
        CopyUrlCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        DownloadLaterCommand.NotifyCanExecuteChanged();
    }

    private static string? ClipboardUrl()
    {
        try
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : null;
            return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? text : null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null; // clipboard busy
        }
    }
}
