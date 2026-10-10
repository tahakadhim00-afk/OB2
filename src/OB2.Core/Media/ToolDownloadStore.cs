using System.Text.Json;
using OB2.Core.Downloads;

namespace OB2.Core.Media;

/// <summary>
/// The download records of the tool installer (D-71), kept in one JSON file next to the
/// partial files instead of the user's download list. With it, a slow tool download continues
/// where it stopped after a pause, a failure, or an app restart.
/// </summary>
internal sealed class ToolDownloadStore : IDownloadRepository
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _path;
    private readonly object _lock = new();
    private readonly State _state;

    public ToolDownloadStore(string path)
    {
        _path = path;
        _state = Load(path);
    }

    public long Insert(DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_lock)
        {
            var copy = item.Clone();
            copy.Id = _state.NextId++;
            _state.Items.Add(copy);
            Save();
            return copy.Id;
        }
    }

    public void Update(DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_lock)
        {
            var index = _state.Items.FindIndex(d => d.Id == item.Id);
            if (index >= 0)
            {
                _state.Items[index] = item.Clone();
                Save();
            }
        }
    }

    public DownloadItem? Get(long id)
    {
        lock (_lock)
        {
            return _state.Items.FirstOrDefault(d => d.Id == id)?.Clone();
        }
    }

    public IReadOnlyList<DownloadItem> List()
    {
        lock (_lock)
        {
            return [.. _state.Items.Select(d => d.Clone())];
        }
    }

    public void Delete(long id)
    {
        lock (_lock)
        {
            _state.Items.RemoveAll(d => d.Id == id);
            _state.Segments.Remove(id);
            Save();
        }
    }

    public IReadOnlyList<Segment> GetSegments(long downloadId)
    {
        lock (_lock)
        {
            return _state.Segments.TryGetValue(downloadId, out var segments) ? [.. segments.Select(s => s.Snapshot())] : [];
        }
    }

    public void SaveSegments(long downloadId, IReadOnlyList<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        lock (_lock)
        {
            _state.Segments[downloadId] = [.. segments.Select(s => s.Snapshot())];
            Save();
        }
    }

    public void Checkpoint(long downloadId, long downloadedBytes, IReadOnlyList<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        lock (_lock)
        {
            if (_state.Items.FirstOrDefault(d => d.Id == downloadId) is { } item)
            {
                item.DownloadedBytes = downloadedBytes;
            }

            _state.Segments[downloadId] = [.. segments.Select(s => s.Snapshot())];
            Save();
        }
    }

    public void AddHistoryEvent(long? downloadId, string eventType, string? detail = null)
    {
        // Tool downloads have no history to show.
    }

    public IReadOnlyList<HistoryEvent> GetHistory(long downloadId) => [];

    public IReadOnlyList<long> MarkInterruptedAsPaused()
    {
        lock (_lock)
        {
            var ids = new List<long>();
            foreach (var item in _state.Items.Where(d => d.Status.IsRunning()))
            {
                item.Status = DownloadStatus.Paused;
                ids.Add(item.Id);
            }

            foreach (var segment in _state.Segments.Values.SelectMany(s => s).Where(s => s.Status == SegmentStatus.Active))
            {
                segment.Status = SegmentStatus.Pending;
            }

            Save();
            return ids;
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_state, Json));
        File.Move(temp, _path, overwrite: true);
    }

    private static State Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path), Json) ?? new State() : new State();
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return new State(); // a damaged file only costs the partial downloads
        }
    }

    private sealed class State
    {
        public long NextId { get; set; } = 1;

        public List<DownloadItem> Items { get; set; } = [];

        public Dictionary<long, List<Segment>> Segments { get; set; } = [];
    }
}
