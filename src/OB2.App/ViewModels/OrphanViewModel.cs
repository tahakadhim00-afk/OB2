using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OB2.App.Infrastructure;
using OB2.App.Services;
using OB2.Core.Files;

namespace OB2.App.ViewModels;

/// <summary>
/// §10.3 "Recovered orphan files": partial files on disk that no download claims. An orphan has
/// no stored address, so it cannot simply be resumed; the user either removes it or supplies the
/// address again through the Add dialog, which is pre-filled with the name.
/// </summary>
public sealed partial class OrphanViewModel : ObservableObject
{
    private readonly OrphanScanner _scanner;
    private readonly IDialogService _dialogs;
    private readonly Func<IEnumerable<string>> _folders;

    [ObservableProperty]
    private OrphanRowViewModel? _selected;

    [ObservableProperty]
    private string? _statusText;

    public OrphanViewModel(OrphanScanner scanner, IDialogService dialogs, Func<IEnumerable<string>> folders)
    {
        _scanner = scanner;
        _dialogs = dialogs;
        _folders = folders;
        Rescan();
    }

    public ObservableCollection<OrphanRowViewModel> Orphans { get; } = [];

    public bool IsEmpty => Orphans.Count == 0;

    [RelayCommand]
    public void Rescan()
    {
        Orphans.Clear();
        foreach (var orphan in _scanner.Scan(_folders()))
        {
            Orphans.Add(new OrphanRowViewModel(orphan));
        }

        Selected = Orphans.FirstOrDefault();
        var total = Orphans.Sum(o => o.Source.SizeBytes);
        StatusText = Orphans.Count == 0
            ? Loc.Get("Orphans_None")
            : Loc.Format("Orphans_Found", Orphans.Count, Fmt.Size(total));
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Reveal() => _dialogs.OpenFolder(Selected!.Source.PartPath);

    /// <summary>
    /// Deleting is explicit and confirmed: the file may hold hours of a download that only
    /// lost its database row, and §10.3 forbids removing one on OB2's own initiative.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete()
    {
        var orphan = Selected!;
        if (_dialogs.ConfirmDelete(1, anyCompleted: false) is null)
        {
            return;
        }

        try
        {
            OrphanScanner.Delete(orphan.Source.PartPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = Loc.Format("Orphans_DeleteFailed", orphan.FileName, ex.Message);
            return;
        }

        Rescan();
    }

    /// <summary>
    /// The address is not recorded anywhere, so downloading again means asking for it. The Add
    /// dialog opens with the name pre-filled and the folder already holding the part file, so a
    /// matching address resumes onto the existing bytes instead of starting over.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DownloadAgain()
    {
        var orphan = Selected!;
        _dialogs.ShowAddDownload(new AddDownloadPrefill
        {
            FileNameHint = orphan.FileName,
            SaveFolder = orphan.Source.Folder,
        });
        Rescan();
    }

    private bool HasSelection() => Selected is not null;

    partial void OnSelectedChanged(OrphanRowViewModel? value)
    {
        RevealCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        DownloadAgainCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>One row in the orphan list.</summary>
public sealed class OrphanRowViewModel(OrphanFile source)
{
    public OrphanFile Source { get; } = source;

    public string FileName => Source.FileName;

    public string Folder => Source.Folder;

    public string SizeText => Fmt.Size(Source.SizeBytes);

    public DateTimeOffset LastWrite => Source.LastWriteUtc.ToLocalTime();
}
