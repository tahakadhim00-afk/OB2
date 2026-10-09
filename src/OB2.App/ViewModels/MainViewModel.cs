using CommunityToolkit.Mvvm.ComponentModel;
using OB2.Storage;

namespace OB2.App.ViewModels;

/// <summary>Phase 1 shell: shows where data lives. Download lists arrive in Phase 4.</summary>
public sealed class MainViewModel : ObservableObject
{
    public MainViewModel(StorageContext storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        DataRoot = storage.Paths.Root;
        IsPortable = storage.Paths.IsPortable;
        SchemaVersion = storage.Migration.CurrentVersion;
    }

    public string DataRoot { get; }

    public bool IsPortable { get; }

    public int SchemaVersion { get; }
}
