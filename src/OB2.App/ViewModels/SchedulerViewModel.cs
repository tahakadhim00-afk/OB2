using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OB2.App.Infrastructure;
using OB2.Core.Downloads;
using OB2.Core.Organization;
using OB2.Core.Services;

namespace OB2.App.ViewModels;

/// <summary>Scheduler window: queues (FR-13), order (FR-15), weekday times (FR-16), completion action (FR-17).</summary>
public sealed partial class SchedulerViewModel : ObservableObject
{
    private readonly DownloadService _service;
    private readonly IQueueRepository _queues;
    private readonly List<QueueEditViewModel> _removed = [];

    [ObservableProperty]
    private QueueEditViewModel? _selectedQueue;

    [ObservableProperty]
    private QueueItemViewModel? _selectedItem;

    [ObservableProperty]
    private string? _errorText;

    public SchedulerViewModel(DownloadService service, IQueueRepository queues, long? selectQueueId)
    {
        _service = service;
        _queues = queues;
        foreach (var queue in queues.List())
        {
            Queues.Add(new QueueEditViewModel(queue, service.Queues.IsRunning(queue.Id)));
        }

        SelectedQueue = Queues.FirstOrDefault(q => q.Source.Id == selectQueueId) ?? Queues.FirstOrDefault();
        Actions =
        [
            new Choice<CompletionAction>(CompletionAction.None, Loc.Get("Action_None")),
            new Choice<CompletionAction>(CompletionAction.CloseApp, Loc.Get("Action_CloseApp")),
            new Choice<CompletionAction>(CompletionAction.Sleep, Loc.Get("Action_Sleep")),
            new Choice<CompletionAction>(CompletionAction.Shutdown, Loc.Get("Action_Shutdown")),
            new Choice<CompletionAction>(CompletionAction.RunCommand, Loc.Get("Action_RunCommand")),
        ];
    }

    public event EventHandler? CloseRequested;

    public ObservableCollection<QueueEditViewModel> Queues { get; } = [];

    public ObservableCollection<QueueItemViewModel> Items { get; } = [];

    public IReadOnlyList<Choice<CompletionAction>> Actions { get; }

    partial void OnSelectedQueueChanged(QueueEditViewModel? value) => LoadItems();

    [RelayCommand]
    private void AddQueue()
    {
        var queue = new QueueEditViewModel(new QueueDefinition { Name = Loc.Get("Scheduler_NewQueue"), SortOrder = Queues.Count }, false);
        Queues.Add(queue);
        SelectedQueue = queue;
    }

    [RelayCommand]
    private void RemoveQueue()
    {
        if (SelectedQueue is not { } queue)
        {
            return;
        }

        if (Queues.Count <= 1)
        {
            ErrorText = Loc.Get("Scheduler_CannotDeleteLast");
            return;
        }

        Queues.Remove(queue);
        if (queue.Source.Id != 0)
        {
            _removed.Add(queue);
        }

        SelectedQueue = Queues.FirstOrDefault();
    }

    [RelayCommand]
    private void StartNow()
    {
        if (Save(close: false) && SelectedQueue is { } queue)
        {
            _service.Queues.StartQueue(queue.Source.Id);
            queue.IsRunning = true;
        }
    }

    [RelayCommand]
    private async Task StopNowAsync()
    {
        if (SelectedQueue is { Source.Id: > 0 } queue)
        {
            await _service.Queues.StopQueueAsync(queue.Source.Id);
            queue.IsRunning = false;
        }
    }

    [RelayCommand]
    private void MoveUp() => MoveItem(-1);

    [RelayCommand]
    private void MoveDown() => MoveItem(+1);

    [RelayCommand]
    private void SaveAndClose() => Save(close: true);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private bool Save(bool close)
    {
        foreach (var queue in Queues)
        {
            if (!queue.TryApply(out var error))
            {
                SelectedQueue = queue;
                ErrorText = error;
                return false;
            }
        }

        var keep = Queues.First(q => q.Source.Id != 0 || Queues.Count == 1);
        foreach (var queue in Queues.Where(q => q.Source.Id == 0))
        {
            _queues.Insert(queue.Source);
        }

        foreach (var queue in Queues.Where(q => q.Source.Id != 0))
        {
            _queues.Update(queue.Source);
        }

        foreach (var removed in _removed)
        {
            _queues.Delete(removed.Source.Id, keep.Source.Id);
        }

        _removed.Clear();
        ErrorText = null;
        if (close)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    private void MoveItem(int delta)
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        var index = Items.IndexOf(item);
        _service.Queues.Move(item.Id, Math.Clamp(index + delta, 0, Items.Count - 1));
        LoadItems();
        SelectedItem = Items.FirstOrDefault(i => i.Id == item.Id);
    }

    private void LoadItems()
    {
        Items.Clear();
        if (SelectedQueue is not { Source.Id: > 0 } queue)
        {
            return;
        }

        foreach (var item in _service.List().Where(d => d.QueueId == queue.Source.Id && d.Status != DownloadStatus.Completed).OrderBy(d => d.QueuePosition).ThenBy(d => d.Id))
        {
            Items.Add(new QueueItemViewModel(item.Id, item.FileName, Loc.Status(item.Status)));
        }
    }
}

public sealed record QueueItemViewModel(long Id, string FileName, string StatusText);

public sealed partial class QueueEditViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private int _concurrency;

    [ObservableProperty]
    private bool _scheduleEnabled;

    [ObservableProperty]
    private string _startText;

    [ObservableProperty]
    private bool _useStop;

    [ObservableProperty]
    private string _stopText;

    [ObservableProperty]
    private CompletionAction _completionAction;

    [ObservableProperty]
    private string _command;

    [ObservableProperty]
    private bool _isRunning;

    public QueueEditViewModel(QueueDefinition source, bool isRunning)
    {
        Source = source;
        var s = source.Schedule;
        _name = source.Name;
        _concurrency = source.Concurrency;
        _scheduleEnabled = s.Enabled;
        _startText = s.Start.ToString("HH\\:mm", CultureInfo.InvariantCulture);
        _useStop = s.Stop is not null;
        _stopText = (s.Stop ?? new TimeOnly(7, 0)).ToString("HH\\:mm", CultureInfo.InvariantCulture);
        _completionAction = s.CompletionAction;
        _command = s.Command ?? string.Empty;
        _isRunning = isRunning;
        Days = [.. Enum.GetValues<DayOfWeek>()
            .OrderBy(d => ((int)d + 6) % 7) // Monday first
            .Select(d => new DayChoice(d, CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(d), s.Days.Contains(d)))];
    }

    public QueueDefinition Source { get; }

    public IReadOnlyList<DayChoice> Days { get; }

    public string Display => Name;

    public bool TryApply(out string error)
    {
        error = string.Empty;
        if (!TimeOnly.TryParseExact(StartText.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || (UseStop && !TimeOnly.TryParseExact(StopText.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
        {
            error = Loc.Get("Scheduler_InvalidTime");
            return false;
        }

        TimeOnly? stop = UseStop ? TimeOnly.ParseExact(StopText.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture) : null;
        Source.Name = string.IsNullOrWhiteSpace(Name) ? Source.Name : Name.Trim();
        Source.Concurrency = Math.Clamp(Concurrency, 1, 32);
        Source.Schedule = new QueueSchedule
        {
            Enabled = ScheduleEnabled,
            Start = start,
            Stop = stop,
            Days = Days.Where(d => d.IsChecked).Select(d => d.Day).ToHashSet(),
            CompletionAction = CompletionAction,
            Command = string.IsNullOrWhiteSpace(Command) ? null : Command.Trim(),
        };
        return true;
    }
}

public sealed partial class DayChoice(DayOfWeek day, string label, bool isChecked) : ObservableObject
{
    [ObservableProperty]
    private bool _isChecked = isChecked;

    public DayOfWeek Day { get; } = day;

    public string Label { get; } = label;
}
