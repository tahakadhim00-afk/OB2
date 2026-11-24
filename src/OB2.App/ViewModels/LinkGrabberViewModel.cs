using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OB2.App.Infrastructure;
using OB2.App.Services;
using OB2.Core.Downloads;
using OB2.Core.Files;
using OB2.Core.Grabber;
using OB2.Core.Http;
using OB2.Core.Organization;
using OB2.Core.Services;

namespace OB2.App.ViewModels;

/// <summary>
/// D-87: Grab links. The user pastes any text; every http(s) address in it becomes a row, which
/// is checked (name, size, file or web page, working or not) a few at a time. A pasted address
/// that is a web page is read, and the files it links to are listed under it (one level; deeper
/// on request). A row is ticked once its check finds a file. Boxes filter the list by kind, and
/// the ticked rows the filters show are added to one queue in one go, each to its category's
/// folder or all to one folder. Nothing is requested before "Find links".
/// </summary>
public sealed partial class LinkGrabberViewModel : ObservableObject, IDisposable
{
    private readonly DownloadService _service;
    private readonly IDialogService _dialogs;
    private readonly LinkChecker _checker;
    private readonly IReadOnlyCollection<string> _fileExtensions;
    private readonly CancellationTokenSource _closing = new();

    /// <summary>Addresses of the downloads already in OB2's list (as added and after redirects).</summary>
    private readonly HashSet<string> _known;

    /// <summary>Addresses in this window's list, so finding links twice adds each once.</summary>
    private readonly HashSet<string> _listed = new(StringComparer.Ordinal);

    private readonly ListCollectionView _view;
    private IReadOnlyList<GrabbedLinkViewModel> _selected = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindLinksCommand))]
    private string _pasteText = string.Empty;

    [ObservableProperty]
    private string? _foundText;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseOneFolder))]
    private bool _useCategoryFolders = true;

    [ObservableProperty]
    private string _saveFolder;

    [ObservableProperty]
    private Choice<long>? _selectedQueue;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadNowCommand), nameof(DownloadLaterCommand))]
    private int _tickedCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadNowCommand), nameof(DownloadLaterCommand), nameof(FindLinksCommand))]
    private bool _isBusy;

    /// <summary>D-88: the steps panel at the top. Shown by itself the first time the window opens only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGuideHidden))]
    private bool _isGuideVisible;

    /// <summary>"How it works" shows while the guide is hidden.</summary>
    public bool IsGuideHidden => !IsGuideVisible;

    /// <param name="initialText">Put in the paste box, not searched: the clipboard's text when it holds a link.</param>
    /// <param name="fileExtensions">The capture rules' types (Options > Browser), which count as files on a page too.</param>
    public LinkGrabberViewModel(DownloadService service, IDialogService dialogs, string? initialText, IReadOnlyCollection<string> fileExtensions)
    {
        _service = service;
        _dialogs = dialogs;
        _fileExtensions = fileExtensions;
        _checker = new LinkChecker(
            (url, referrer, ct) => _service.ProbeAsync(new AddDownloadOptions { Url = url, Referrer = referrer?.AbsoluteUri }, ct),
            (url, referrer, ct) => _service.ReadPageAsync(url, referrer, ct));
        _known = service.List().SelectMany(d => new[] { d.Url, d.FinalUrl }).OfType<string>().ToHashSet(StringComparer.Ordinal);
        _saveFolder = service.DefaultSaveFolder;
        _pasteText = initialText ?? string.Empty;

        Filters = [.. Enum.GetValues<LinkGroup>().Select(g => new LinkFilterViewModel(g, Loc.Get("Grab_Group_" + g)))];
        foreach (var filter in Filters)
        {
            filter.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LinkFilterViewModel.IsShown))
                {
                    _view!.Refresh();
                    UpdateSummary();
                }
            };
        }

        Queues = [.. service.QueueList.Select(q => new Choice<long>(q.Id, q.Name))];
        SelectedQueue = Queues.FirstOrDefault(q => q.Value == service.DefaultQueueId) ?? Queues.FirstOrDefault();

        _view = new ListCollectionView(Links) { Filter = o => o is GrabbedLinkViewModel link && Shows(link) };
        // A row changes group when its check ends (a .zip that turns out to be a web page), and
        // its name when the server names it, so the filters and the search look at it again.
        _view.LiveFilteringProperties.Add(nameof(GrabbedLinkViewModel.Group));
        _view.LiveFilteringProperties.Add(nameof(GrabbedLinkViewModel.FileName));
        _view.IsLiveFiltering = true;
        // Live filtering applies its changes later, on the dispatcher, so the summary follows the
        // view's own change events rather than the calls that cause them.
        ((ICollectionView)_view).CollectionChanged += (_, _) => UpdateSummary();
        UpdateSummary();
    }

    /// <summary>Raised when the window should close: everything ticked was added.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Asks before adding addresses with a user name and password in them (§11.1); the argument is how many.</summary>
    public Func<int, bool>? ConfirmCredentials { get; set; }

    public ObservableCollection<GrabbedLinkViewModel> Links { get; } = [];

    public ICollectionView LinksView => _view;

    public ObservableCollection<LinkFilterViewModel> Filters { get; }

    public ObservableCollection<Choice<long>> Queues { get; }

    public bool IsEmpty => Links.Count == 0;

    public bool HasLinks => Links.Count > 0;

    /// <summary>The second choice of the folder radio buttons.</summary>
    public bool UseOneFolder
    {
        get => !UseCategoryFolders;
        set => UseCategoryFolders = !value;
    }

    /// <summary>Set by the view from the list's selection.</summary>
    public IReadOnlyList<GrabbedLinkViewModel> SelectedLinks
    {
        get => _selected;
        set
        {
            _selected = value;
            ToggleSelectedCommand.NotifyCanExecuteChanged();
            RemoveSelectedCommand.NotifyCanExecuteChanged();
            OpenLinkCommand.NotifyCanExecuteChanged();
            CopyAddressCommand.NotifyCanExecuteChanged();
            FindLinksOnPageCommand.NotifyCanExecuteChanged();
        }
    }

    private IEnumerable<GrabbedLinkViewModel> Shown => _view.Cast<GrabbedLinkViewModel>();

    public void Dispose()
    {
        _closing.Cancel(); // checks still waiting or running end here
        _closing.Dispose();
    }

    /// <summary>Called by the view after the folder picker.</summary>
    public void ChooseFolder(string folder)
    {
        SaveFolder = folder;
        UseCategoryFolders = false;
    }

    [RelayCommand(CanExecute = nameof(CanFindLinks))]
    private void FindLinks()
    {
        var found = LinkExtractor.Extract(PasteText, out var leftOut);
        var fresh = found.Where(u => !_listed.Contains(u.AbsoluteUri)).ToList();
        var room = Math.Max(0, LinkExtractor.MaxLinks - Links.Count);
        leftOut += Math.Max(0, fresh.Count - room);

        foreach (var url in fresh.Take(room))
        {
            Insert(url, foundOn: null, Links.Count);
        }

        var added = Math.Min(fresh.Count, room);
        var text = found.Count == 0 ? Loc.Get("Grab_NoneFound")
            : added == 0 && leftOut == 0 ? Loc.Get("Grab_AllListed")
            : found.Count - fresh.Count > 0 ? Loc.Format("Grab_FoundSome", added, found.Count - fresh.Count)
            : Loc.Format("Grab_Found", added);
        FoundText = leftOut > 0 ? text + " " + Loc.Format("Grab_TooMany", LinkExtractor.MaxLinks, leftOut) : text;
        if (found.Count > 0)
        {
            PasteText = string.Empty; // ready for more
        }

        ErrorText = null;
        AfterListChanged();
    }

    private bool CanFindLinks() => !IsBusy && !string.IsNullOrWhiteSpace(PasteText);

    /// <summary>"Got it": hides the guide.</summary>
    [RelayCommand]
    private void HideGuide() => IsGuideVisible = false;

    /// <summary>"How it works": the guide again.</summary>
    [RelayCommand]
    private void ShowGuide() => IsGuideVisible = true;

    [RelayCommand]
    private void TickAll() => SetTicks(Shown, true);

    [RelayCommand]
    private void TickNone() => SetTicks(Shown, false);

    /// <summary>Space: ticks the selected rows, or unticks them when all of them are ticked.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ToggleSelected() => SetTicks(_selected, !_selected.Where(l => l.CanTick).All(l => l.IsChecked));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveSelected() => Remove([.. _selected]);

    [RelayCommand]
    private void ClearList() => Remove([.. Links]);

    /// <summary>Double-click or Enter: the Add window for this one address (for a video page, the Download Video window).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenLink()
    {
        var link = _selected[0];
        var item = _dialogs.ShowAddDownload(new AddDownloadPrefill { Url = link.Url.AbsoluteUri });
        if (item is not null)
        {
            _known.Add(link.Url.AbsoluteUri);
            Remove([link]);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyAddress() => _dialogs.CopyText(string.Join(Environment.NewLine, _selected.Select(l => l.Url.AbsoluteUri)));

    private bool HasSelection() => _selected.Count > 0;

    /// <summary>
    /// Reads the selected web pages for their file links. Pasted pages are read on their own; this
    /// is for a page found on one of them (one level further), or one to read again.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFindLinksOnPage))]
    private void FindLinksOnPage()
    {
        foreach (var page in _selected.Where(l => l.CanReadPage).ToList())
        {
            _ = ReadPageAsync(page);
        }
    }

    private bool CanFindLinksOnPage() => _selected.Any(l => l.CanReadPage);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private Task DownloadNowAsync() => AddTickedAsync(startQueue: true);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private Task DownloadLaterAsync() => AddTickedAsync(startQueue: false);

    private bool CanAdd() => !IsBusy && TickedCount > 0;

    /// <summary>
    /// Adds the ticked rows the filters show, in list order, to the chosen queue. "Download now"
    /// then starts the queue rather than every download: the queue's limit (3 at a time unless
    /// changed) decides how many run, where a hundred links started at once would each get a
    /// sliver of the line and open hundreds of connections.
    /// </summary>
    private async Task AddTickedAsync(bool startQueue)
    {
        ErrorText = null;
        var links = Shown.Where(l => l.IsChecked && l.CanTick).ToList();
        var folder = UseCategoryFolders ? null : SaveFolder.Trim();
        if (folder is { Length: 0 })
        {
            ErrorText = Loc.Get("Grab_NoFolder");
            return;
        }

        var withPassword = links.Count(l => l.HasCredentials);
        if (withPassword > 0 && ConfirmCredentials?.Invoke(withPassword) != true)
        {
            return;
        }

        var queueId = SelectedQueue?.Value ?? _service.DefaultQueueId;
        var added = new List<GrabbedLinkViewModel>();
        IsBusy = true;
        try
        {
            foreach (var link in links)
            {
                FoundText = Loc.Format("Grab_Adding", added.Count + 1, links.Count);
                try
                {
                    // A row still being checked goes in with what its address says; the download
                    // asks the server itself when it starts, as a browser capture does.
                    // A file found on a page keeps the page as referrer, as its check did.
                    var probe = link.Check.Probe ?? new ProbeResult(link.Url, null, false, link.FileName, null, null, null);
                    await _service.AddAsync(
                        new AddDownloadOptions
                        {
                            Url = link.Url, SaveFolder = folder, QueueId = queueId, StartNow = false, Source = DownloadSource.Manual,
                            Referrer = link.FoundOn?.AbsoluteUri,
                        },
                        probe);
                    link.AddError = null;
                    added.Add(link);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or UnsafePathException or HttpRequestException)
                {
                    link.AddError = ex.Message;
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        foreach (var link in added)
        {
            _known.Add(link.Url.AbsoluteUri);
        }

        Remove(added);
        if (startQueue && added.Count > 0)
        {
            _service.Queues.StartQueue(queueId);
        }

        if (added.Count == links.Count)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        FoundText = null;
        ErrorText = Loc.Format("Grab_AddFailed", added.Count, links.Count - added.Count);
    }

    /// <summary>Lists an address at <paramref name="at"/> and starts its check.</summary>
    /// <param name="foundOn">The page it was found on: its referrer, and what its row says it came from.</param>
    private void Insert(Uri url, GrabbedLinkViewModel? foundOn, int at)
    {
        var link = new GrabbedLinkViewModel(url, _known.Contains(url.AbsoluteUri), foundOn?.PageUrl);
        link.PropertyChanged += OnLinkChanged;
        _listed.Add(url.AbsoluteUri);
        Links.Insert(at, link);
        _ = CheckAsync(link);
    }

    private async Task CheckAsync(GrabbedLinkViewModel link)
    {
        LinkCheckResult result;
        try
        {
            result = await _checker.CheckAsync(link.Url, link.FoundOn, _closing.Token);
        }
        catch (OperationCanceledException)
        {
            return; // the window closed
        }

        link.Apply(result, result.Probe is { } probe && _known.Contains(probe.FinalUrl.AbsoluteUri));
        UpdateCounts();

        // A pasted page is read for its files; a page found on a page is not (one level), unless asked.
        if (result.State == LinkState.WebPage && link.FoundOn is null && Links.Contains(link))
        {
            await ReadPageAsync(link);
        }
    }

    /// <summary>
    /// Lists the file links on a web page's row right under it, each checked with the page as
    /// referrer. Links already in the list are not listed again.
    /// </summary>
    private async Task ReadPageAsync(GrabbedLinkViewModel page)
    {
        if (!page.CanReadPage)
        {
            return;
        }

        page.PageReading = true;
        PageReadResult result;
        try
        {
            result = await _checker.ReadPageAsync(page.PageUrl, page.FoundOn, _fileExtensions, _closing.Token);
        }
        catch (OperationCanceledException)
        {
            return; // the window closed
        }
        finally
        {
            page.PageReading = false;
        }

        var fresh = result.Links.Where(u => !_listed.Contains(u.AbsoluteUri)).ToList();
        var room = Math.Max(0, LinkExtractor.MaxLinks - Links.Count);
        var at = Links.IndexOf(page);
        if (at >= 0)
        {
            // Under the page, after anything it listed before.
            at++;
            while (at < Links.Count && Links[at].FoundOn == page.PageUrl)
            {
                at++;
            }

            foreach (var url in fresh.Take(room))
            {
                Insert(url, page, at++);
            }
        }

        page.PageFound(result.Links.Count, result.Error);
        if (fresh.Count > room)
        {
            FoundText = Loc.Format("Grab_TooMany", LinkExtractor.MaxLinks, fresh.Count - room);
        }

        AfterListChanged();
    }

    private void OnLinkChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GrabbedLinkViewModel.IsChecked) or nameof(GrabbedLinkViewModel.Size) or nameof(GrabbedLinkViewModel.Check)
            or nameof(GrabbedLinkViewModel.PageReading))
        {
            UpdateSummary();
        }

        if (e.PropertyName is nameof(GrabbedLinkViewModel.CanReadPage))
        {
            FindLinksOnPageCommand.NotifyCanExecuteChanged();
        }
    }

    private static void SetTicks(IEnumerable<GrabbedLinkViewModel> links, bool value)
    {
        foreach (var link in links)
        {
            link.Tick(value);
        }
    }

    private void Remove(IReadOnlyCollection<GrabbedLinkViewModel> links)
    {
        foreach (var link in links)
        {
            link.PropertyChanged -= OnLinkChanged;
            _listed.Remove(link.Url.AbsoluteUri);
            Links.Remove(link);
        }

        AfterListChanged();
    }

    private void AfterListChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasLinks));
        UpdateCounts();
        UpdateSummary();
    }

    private void UpdateCounts()
    {
        var counts = Links.GroupBy(l => l.Group).ToDictionary(g => g.Key, g => g.Count());
        foreach (var filter in Filters)
        {
            filter.Count = counts.GetValueOrDefault(filter.Group);
        }
    }

    private bool Shows(GrabbedLinkViewModel link)
    {
        if (!Filters[(int)link.Group].IsShown)
        {
            return false;
        }

        var search = SearchText.Trim();
        return search.Length == 0
            || link.FileName.Contains(search, StringComparison.CurrentCultureIgnoreCase)
            || link.Url.AbsoluteUri.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSearchTextChanged(string value)
    {
        _view.Refresh();
        UpdateSummary();
    }

    /// <summary>"12 of 30 links ticked · at least 1.2 GB · 5 hidden by the filters".</summary>
    private void UpdateSummary()
    {
        if (_view is null)
        {
            return; // during construction
        }

        var shown = Shown.ToList();
        var ticked = shown.Where(l => l.IsChecked && l.CanTick).ToList();
        TickedCount = ticked.Count;
        if (Links.Count == 0)
        {
            SummaryText = string.Empty;
            return;
        }

        var parts = new List<string> { Loc.Format("Grab_Ticked", ticked.Count, shown.Count) };
        if (ticked.Count > 0 && ticked.Any(l => l.Size is not null))
        {
            var size = Fmt.Size(ticked.Sum(l => l.Size ?? 0));
            parts.Add(ticked.All(l => l.Size is not null) ? size : Loc.Format("Grab_AtLeast", size));
        }

        var checking = Links.Count(l => l.Check.State == LinkState.Checking || l.PageReading);
        if (checking > 0)
        {
            parts.Add(Loc.Format("Grab_StillChecking", checking));
        }

        if (Links.Count > shown.Count)
        {
            parts.Add(Loc.Format("Grab_Hidden", Links.Count - shown.Count));
        }

        SummaryText = string.Join("  ·  ", parts);
    }
}

/// <summary>One grabbed address and what its check found.</summary>
public sealed partial class GrabbedLinkViewModel : ObservableObject
{
    private readonly string _nameFromAddress;
    private bool _touched;
    private bool _automatic;

    /// <summary>The page's file links once read (null: not read); a failed read leaves <see cref="_pageError"/>.</summary>
    private int? _pageLinks;
    private string? _pageError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName), nameof(Size), nameof(SizeText), nameof(StatusText), nameof(StatusTip), nameof(Group), nameof(Icon), nameof(CanTick), nameof(CanReadPage))]
    private LinkCheckResult _check = new(LinkState.Checking);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isDuplicate;

    /// <summary>Starts unticked: a row is ticked when its check finds a file, so a web page still being checked is never added by accident.</summary>
    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(StatusTip))]
    private string? _addError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanReadPage))]
    private bool _pageReading;

    /// <param name="isDuplicate">The address is already in OB2's download list: listed, but not ticked.</param>
    /// <param name="foundOn">The page this address was found on, or null when it was pasted.</param>
    public GrabbedLinkViewModel(Uri url, bool isDuplicate, Uri? foundOn = null)
    {
        Url = url;
        FoundOn = foundOn;
        HasCredentials = url.UserInfo.Length > 0;
        _nameFromAddress = FileNameSanitizer.Sanitize(FileNameResolver.FromUrl(url));
        _isDuplicate = isDuplicate;
    }

    public Uri Url { get; }

    /// <summary>The page this address was found on: its referrer. Null for a pasted address.</summary>
    public Uri? FoundOn { get; }

    /// <summary>For a web page, where its links point from: the address it ended at after redirects.</summary>
    public Uri PageUrl => Check.Probe?.FinalUrl ?? Url;

    /// <summary>A web page that is not being read right now (Find links on this page).</summary>
    public bool CanReadPage => Check.State == LinkState.WebPage && !PageReading;

    public string Site => Url.Host;

    /// <summary>The address, and the page it was found on.</summary>
    public string AddressTip => FoundOn is null ? Url.AbsoluteUri : Loc.Format("Grab_FoundOnTip", Url.AbsoluteUri, FoundOn.AbsoluteUri);

    public bool HasCredentials { get; }

    /// <summary>The server's name for the file once checked; until then the address's last part.</summary>
    public string FileName => Check.Probe?.SuggestedFileName ?? _nameFromAddress;

    public long? Size => Check.State == LinkState.File ? Check.Probe?.TotalBytes : null;

    public string SizeText => Check.State is LinkState.File or LinkState.WebPage ? Fmt.Size(Check.Probe?.TotalBytes) : string.Empty;

    public LinkGroup Group => LinkGroups.Of(Check.State, FileName);

    /// <summary>A video page needs a quality chosen (D-71): it is opened, not ticked.</summary>
    public bool CanTick => Check.State != LinkState.VideoPage;

    public string Icon => Check.State switch
    {
        LinkState.WebPage => "",                     // Globe
        LinkState.VideoPage => NavIcons.ForCategory(CategoryKind.Video),
        _ when Check.IsFailure => "",                // Error
        _ => CategoryKinds.OfFile(FileName) is var kind && kind != CategoryKind.Other
            ? NavIcons.ForCategory(kind)
            : "",                                    // Page: a file of a kind OB2 has no icon for
    };

    public string StatusText
    {
        get
        {
            if (AddError is not null)
            {
                return Loc.Format("Grab_CouldNotAdd", AddError);
            }

            var text = Check.State switch
            {
                LinkState.File => Loc.Get(Check.Probe!.SupportsRanges ? "Grab_Ready" : "Grab_ReadyNoResume"),
                LinkState.WebPage when PageReading => Loc.Get("Grab_PageReading"),
                LinkState.WebPage when _pageError is not null => Loc.Get("Grab_PageUnreadable"),
                LinkState.WebPage when _pageLinks is 0 => Loc.Get("Grab_PageNoFiles"),
                LinkState.WebPage when _pageLinks is { } count => Loc.Format("Grab_PageFiles", count),
                _ => Loc.Get("Grab_State_" + Check.State),
            };
            if (IsDuplicate)
            {
                text += "  ·  " + Loc.Get("Grab_InList");
            }

            return HasCredentials ? text + "  ·  " + Loc.Get("Grab_HasPassword") : text;
        }
    }

    public string StatusTip => AddError ?? Check.Message ?? _pageError ?? AddressTip;

    /// <summary>What reading this web page found: how many file links, or why it could not be read.</summary>
    public void PageFound(int links, string? error)
    {
        _pageLinks = links;
        _pageError = error;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusTip));
    }

    /// <summary>A tick the user (or Tick all / none) set; a check that ends later leaves it alone.</summary>
    public void Tick(bool value)
    {
        if (CanTick)
        {
            IsChecked = value;
        }
    }

    /// <summary>
    /// Shows a check's result, and ticks the row if it is a file: not a web page, a video page or
    /// a failure, not already in OB2's list, and without a password in its address (§11.1: never
    /// added without the user's say). A tick the user set or cleared meanwhile is left alone.
    /// </summary>
    public void Apply(LinkCheckResult result, bool finalAddressKnown)
    {
        Check = result;
        IsDuplicate |= finalAddressKnown;
        if (!_touched)
        {
            _automatic = true;
            IsChecked = result.State == LinkState.File && !IsDuplicate && !HasCredentials;
            _automatic = false;
        }
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (!_automatic)
        {
            _touched = true;
        }
    }

    /// <summary>What UI Automation (and so a screen reader) names the row by.</summary>
    public override string ToString() => FileName;
}

/// <summary>One "Show" box above the list: a kind of link and how many there are.</summary>
public sealed partial class LinkFilterViewModel(LinkGroup group, string name) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label), nameof(HasLinks))]
    private int _count;

    [ObservableProperty]
    private bool _isShown = true;

    public LinkGroup Group { get; } = group;

    public string Label => Loc.Format("Grab_GroupCount", name, Count);

    /// <summary>Only kinds the list holds get a box.</summary>
    public bool HasLinks => Count > 0;
}
