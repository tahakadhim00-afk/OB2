namespace OB2.Storage.Paths;

/// <summary>
/// A per-data-folder lock held for the life of an OB2 process. Only the first process to
/// open a data folder may run startup recovery (§11.3), so a second process, such as a CLI
/// run beside the open window, never marks the first one's live downloads as paused.
/// Implemented as <c>data\ob2.lock</c> opened without sharing: no thread affinity, and
/// Windows releases it if the process crashes.
/// </summary>
public sealed class InstanceLock : IDisposable
{
    private FileStream? _file;

    private InstanceLock(FileStream? file)
    {
        _file = file;
    }

    /// <summary>True when no other OB2 process was using this data folder.</summary>
    public bool IsFirstInstance => _file is not null;

    public static InstanceLock Acquire(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.DataDirectory);
        try
        {
            var file = new FileStream(Path.Combine(paths.DataDirectory, "ob2.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return new InstanceLock(file);
        }
        catch (IOException)
        {
            return new InstanceLock(null);
        }
        catch (UnauthorizedAccessException)
        {
            return new InstanceLock(null);
        }
    }

    public void Dispose()
    {
        _file?.Dispose();
        _file = null;
    }
}
