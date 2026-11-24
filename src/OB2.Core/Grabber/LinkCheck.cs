using System.Net;
using OB2.Core.Http;
using OB2.Core.Media;
using OB2.Core.Organization;

namespace OB2.Core.Grabber;

/// <summary>D-87: what checking one grabbed link found.</summary>
public enum LinkState
{
    /// <summary>Not checked yet, or the check is running.</summary>
    Checking,

    /// <summary>The server answered with a file: name, size and resume support are known.</summary>
    File,

    /// <summary>The server answered with a web page (HTML), not a file: a download would save the page.</summary>
    WebPage,

    /// <summary>A YouTube video: it needs a quality chosen in the Download Video window (D-71), so it is not checked.</summary>
    VideoPage,

    /// <summary>404 or 410.</summary>
    NotFound,

    /// <summary>401 or 403: the server wants a sign-in or refuses this address.</summary>
    AccessDenied,

    /// <summary>The server did not answer in time.</summary>
    NoAnswer,

    /// <summary>Any other failure: no such host, refused connection, another status.</summary>
    Failed,
}

/// <param name="Probe">The server's answer, for <see cref="LinkState.File"/> and <see cref="LinkState.WebPage"/>.</param>
/// <param name="Message">What went wrong, for the failures; meant for the user.</param>
public sealed record LinkCheckResult(LinkState State, ProbeResult? Probe = null, string? Message = null)
{
    public bool IsFailure => State is LinkState.NotFound or LinkState.AccessDenied or LinkState.NoAnswer or LinkState.Failed;

    /// <summary>A file or a web page, from what the server said it sends.</summary>
    public static LinkCheckResult FromProbe(ProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        return new LinkCheckResult(IsWebPage(probe.ContentType) ? LinkState.WebPage : LinkState.File, probe);
    }

    public static LinkCheckResult FromError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error switch
        {
            HttpStatusException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone } e => new(LinkState.NotFound, Message: e.Message),
            HttpStatusException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } e => new(LinkState.AccessDenied, Message: e.Message),
            TimeoutException e => new(LinkState.NoAnswer, Message: e.Message),
            // HttpRequestException says "No such host is known. (example.invalid:443)" and the like.
            _ => new(LinkState.Failed, Message: error.Message),
        };
    }

    /// <summary>
    /// HTML is what a site sends for a page: an article, a sign-in form, a file host's countdown,
    /// "this file was removed". A real download of an .html file is rare, and can still be ticked.
    /// </summary>
    private static bool IsWebPage(string? contentType) =>
        contentType is not null
        && (contentType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
            || contentType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// D-87: checks grabbed links with the same request the Add window makes for one address (HEAD,
/// falling back to a one-byte GET, FR-2), a few at a time so a pasted page of 300 links does not
/// open 300 connections at once. Only links the user asked to find are ever checked.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification =
    "SemaphoreSlim holds an OS handle only once AvailableWaitHandle is read, which this class never does. The window closes while checks are still ending, and disposing it would make their Release throw.")]
public sealed class LinkChecker
{
    /// <summary>Checks running at once, across all sites.</summary>
    public const int DefaultParallel = 4;

    private readonly Func<Uri, Uri?, CancellationToken, Task<ProbeResult>> _probe;
    private readonly Func<Uri, Uri?, CancellationToken, Task<(string Html, Uri Url)>>? _readPage;
    private readonly SemaphoreSlim _slots;

    /// <param name="probe">Asks the server about one address, with the page it was found on as referrer: <see cref="Services.DownloadService.ProbeAsync"/> in the app.</param>
    /// <param name="readPage">Fetches a page's HTML, for <see cref="ReadPageAsync"/>: <see cref="PageReader.ReadAsync"/> in the app.</param>
    public LinkChecker(
        Func<Uri, Uri?, CancellationToken, Task<ProbeResult>> probe,
        Func<Uri, Uri?, CancellationToken, Task<(string Html, Uri Url)>>? readPage = null,
        int parallel = DefaultParallel)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentOutOfRangeException.ThrowIfLessThan(parallel, 1);
        _probe = probe;
        _readPage = readPage;
        _slots = new SemaphoreSlim(parallel, parallel);
    }

    /// <param name="referrer">The page the link was found on: many sites refuse a file link without it.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled (the window closed).</exception>
    public async Task<LinkCheckResult> CheckAsync(Uri url, Uri? referrer = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (VideoSites.YouTubeVideo(url) is not null)
        {
            return new LinkCheckResult(LinkState.VideoPage); // the page is not the file (D-71)
        }

        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return LinkCheckResult.FromProbe(await _probe(url, referrer, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            return LinkCheckResult.FromError(ex);
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>
    /// The file links on a web page (<see cref="PageLinks"/>), read in one of the same slots as
    /// the checks. A page that cannot be read gives its reason instead of an error.
    /// </summary>
    /// <param name="page">The page's address after redirects, as its check found it.</param>
    /// <param name="referrer">The page this one was found on, if any.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled (the window closed).</exception>
    public async Task<PageReadResult> ReadPageAsync(Uri page, Uri? referrer, IReadOnlyCollection<string>? fileExtensions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (_readPage is null)
        {
            throw new InvalidOperationException("This checker was made without a way to read pages.");
        }

        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (html, url) = await _readPage(page, referrer, cancellationToken).ConfigureAwait(false);
            return new PageReadResult(PageLinks.Find(html, url, fileExtensions));
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            return new PageReadResult([], ex.Message);
        }
        finally
        {
            _slots.Release();
        }
    }
}

/// <param name="Links">The file links found on the page, in page order.</param>
/// <param name="Error">Why the page could not be read; null when it was.</param>
public sealed record PageReadResult(IReadOnlyList<Uri> Links, string? Error = null);

/// <summary>D-87: the boxes Grab links filters by. Every link is in exactly one.</summary>
public enum LinkGroup
{
    Video,
    Music,
    Documents,
    Pictures,
    Compressed,
    Programs,
    OtherFiles,
    WebPages,
    NotWorking,
}

public static class LinkGroups
{
    /// <summary>
    /// A link that failed is "not working" and a web page is a web page, whatever its address
    /// ends in; a video page is a video; anything else goes by its file name's extension (the
    /// address's until the server has named it), with the kinds categories use (D-86).
    /// </summary>
    public static LinkGroup Of(LinkState state, string? fileName) => state switch
    {
        LinkState.NotFound or LinkState.AccessDenied or LinkState.NoAnswer or LinkState.Failed => LinkGroup.NotWorking,
        LinkState.WebPage => LinkGroup.WebPages,
        LinkState.VideoPage => LinkGroup.Video,
        _ => CategoryKinds.OfFile(fileName) switch
        {
            CategoryKind.Video => LinkGroup.Video,
            CategoryKind.Music => LinkGroup.Music,
            CategoryKind.Documents => LinkGroup.Documents,
            CategoryKind.Pictures => LinkGroup.Pictures,
            CategoryKind.Compressed => LinkGroup.Compressed,
            CategoryKind.Programs => LinkGroup.Programs,
            _ => LinkGroup.OtherFiles,
        },
    };
}
