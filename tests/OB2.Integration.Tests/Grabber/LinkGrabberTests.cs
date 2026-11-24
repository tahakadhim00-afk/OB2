using Microsoft.Data.Sqlite;
using OB2.Core.Downloads;
using OB2.Core.Engine;
using OB2.Core.Grabber;
using OB2.Core.Http;
using OB2.Core.Organization;
using OB2.Core.Services;
using OB2.Core.Settings;
using OB2.Integration.Tests.Infrastructure;
using OB2.Storage.Database;
using OB2.Storage.Repositories;
using OB2.Tests.Common;

namespace OB2.Integration.Tests.Grabber;

/// <summary>
/// D-87: Grab links against a real server, through the same calls the window makes: the links
/// in a pasted text, each checked with the engine's probe, then the wanted ones added to a queue
/// with what the check found, and the queue run.
/// </summary>
public sealed class LinkGrabberTests : IAsyncLifetime
{
    private readonly TempDirectory _temp = new();
    private TestHttpServer _server = null!;
    private DownloadEngine _engine = null!;
    private QueueManager _manager = null!;
    private DownloadService _service = null!;

    public async Task InitializeAsync()
    {
        _server = await TestHttpServer.StartAsync();
        var factory = new SqliteConnectionFactory(_temp.Combine("ob2.db"));
        new MigrationRunner(factory).Run();
        var downloads = new SqliteDownloadRepository(factory);
        var organization = new SqliteOrganizationRepository(factory);
        var settings = new AppSettings();
        settings.Folders.DefaultSaveFolder = _temp.Combine("dl");
        _engine = new DownloadEngine(downloads, () => EngineFixture.FastOptions with { Connections = 2 });
        _manager = new QueueManager(_engine, downloads, organization);
        _service = new DownloadService(_engine, downloads, organization, organization, _manager, () => settings);
    }

    public async Task DisposeAsync()
    {
        _manager.Dispose();
        _engine.Dispose();
        await _server.DisposeAsync();
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    /// <summary>As the window makes it.</summary>
    private LinkChecker Checker() => new(
        (url, referrer, ct) => _service.ProbeAsync(new AddDownloadOptions { Url = url, Referrer = referrer?.AbsoluteUri }, ct),
        (url, referrer, ct) => _service.ReadPageAsync(url, referrer, ct));

    [Fact]
    public async Task A_pasted_page_gives_the_files_it_links_to_and_they_are_checked_with_the_page_as_referrer()
    {
        _server.Pages["downloads"] = """
            <!doctype html><html><head><title>Downloads</title></head><body>
            <nav><a href="/">Home</a> <a href="/page/about">About</a></nav>
            <h1>Downloads</h1>
            <ul>
              <li><a href="/file/400000/setup.exe">Installer</a></li>
              <li><a href="../file/250000/manual.pdf">Manual</a></li>
              <li><a href="/page/mirror">More on the mirror page</a></li>
            </ul>
            </body></html>
            """;
        var checker = Checker();
        var pasted = _server.Url("/redirect?to=/page/downloads");

        var check = await checker.CheckAsync(pasted);
        check.State.Should().Be(LinkState.WebPage);
        var page = await checker.ReadPageAsync(check.Probe!.FinalUrl, null, null);

        page.Error.Should().BeNull();
        page.Links.Select(u => u.AbsolutePath).Should().Equal("/file/400000/setup.exe", "/file/250000/manual.pdf");
        var files = await Task.WhenAll(page.Links.Select(u => checker.CheckAsync(u, check.Probe.FinalUrl)));
        files.Select(f => (f.State, f.Probe!.SuggestedFileName, f.Probe.TotalBytes)).Should().Equal(
            (LinkState.File, "setup.exe", 400_000L), (LinkState.File, "manual.pdf", 250_000L));
        _server.Requests.Where(r => r.PathAndQuery.StartsWith("/file/", StringComparison.Ordinal)).Should()
            .OnlyContain(r => r.Header("Referer") == _server.Url("/page/downloads").AbsoluteUri);
        _server.Requests.Should().NotContain(r => r.PathAndQuery == "/page/mirror", "a page linked from the page is not read on its own");
    }

    [Fact]
    public async Task A_page_that_cannot_be_read_says_why()
    {
        var result = await Checker().ReadPageAsync(_server.Url("/status/403"), null, null);
        result.Links.Should().BeEmpty();
        result.Error.Should().Contain("403");
    }

    [Fact]
    public async Task Each_link_in_a_pasted_text_is_checked_and_sorted()
    {
        var zip = _server.FileUrl(300_000, "get", "disposition=Holiday photos.zip");
        var text = $"""
            Here is everything from the meeting:
            - the archive {zip.AbsoluteUri},
            - the talk ({_server.Url("/redirect?to=/file/200000/talk.mp4").AbsoluteUri}),
            - the article {_server.Url("/page/article").AbsoluteUri};
            - an old link {_server.Url("/status/404").AbsoluteUri} and a private one {_server.Url("/status/403").AbsoluteUri}.
            - the recording https://youtu.be/dQw4w9WgXcQ
            """;

        var links = LinkExtractor.Extract(text);
        links.Should().HaveCount(6);
        var checker = Checker();
        var results = await Task.WhenAll(links.Select(u => checker.CheckAsync(u)));

        results.Select(r => r.State).Should().Equal(
            LinkState.File, LinkState.File, LinkState.WebPage, LinkState.NotFound, LinkState.AccessDenied, LinkState.VideoPage);
        results[0].Probe!.SuggestedFileName.Should().Be("Holiday photos.zip");
        results[0].Probe!.TotalBytes.Should().Be(300_000);
        results[0].Probe!.SupportsRanges.Should().BeTrue();
        results[1].Probe!.SuggestedFileName.Should().Be("talk.mp4");
        results[1].Probe!.FinalUrl.AbsolutePath.Should().Be("/file/200000/talk.mp4");
        results[3].Message.Should().Contain("404");

        results.Select((r, i) => LinkGroups.Of(r.State, r.Probe?.SuggestedFileName ?? links[i].Segments[^1])).Should().Equal(
            LinkGroup.Compressed, LinkGroup.Video, LinkGroup.WebPages, LinkGroup.NotWorking, LinkGroup.NotWorking, LinkGroup.Video);

        // Checking asks for headers only: the page's body and the files' bytes never came.
        _server.Requests.Should().NotContain(r => r.PathAndQuery.StartsWith("/file/", StringComparison.Ordinal) && r.Method == "GET" && r.Header("Range") != "bytes=0-0");
        _server.BytesSent.Should().BeLessThan(1000, "a probe reads one byte of a file at most");
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_is_a_failure_with_its_reason()
    {
        var result = await Checker().CheckAsync(new Uri("http://127.0.0.1:9/missing.zip"));
        result.State.Should().Be(LinkState.Failed);
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_wanted_links_go_into_one_queue_with_what_the_check_found_and_the_queue_runs_them()
    {
        var urls = Enumerable.Range(1, 5).Select(i => _server.FileUrl(150_000 + i, $"part{i}.pdf")).ToList();
        var checker = Checker();
        var checks = await Task.WhenAll(urls.Select(u => checker.CheckAsync(u)));
        var queue = _service.DefaultQueueId;
        var requestsBefore = _server.Requests.Count;

        // As "Download Now" does: each added with its check, not started, then the queue started.
        var items = new List<DownloadItem>();
        foreach (var check in checks)
        {
            items.Add(await _service.AddAsync(new AddDownloadOptions { Url = check.Probe!.FinalUrl, QueueId = queue, StartNow = false }, check.Probe));
        }

        _server.Requests.Count.Should().Be(requestsBefore, "adding uses what the check found instead of asking again");
        items.Should().OnlyContain(i => i.Status == DownloadStatus.Queued && i.QueueId == queue);
        items.Select(i => i.TotalBytes).Should().Equal(Enumerable.Range(1, 5).Select(i => (long?)(150_000 + i)));

        _manager.StartQueue(queue);
        foreach (var item in items)
        {
            var done = await _engine.WaitAsync(item.Id).WaitAsync(TimeSpan.FromMinutes(1));
            done!.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
            done.DestPath.Should().Be(Path.Combine(_temp.Combine("dl"), "Documents", Path.GetFileName(done.DestPath)));
            new FileInfo(done.DestPath).Length.Should().Be(done.TotalBytes);
        }
    }

    [Fact]
    public async Task A_link_added_before_its_check_ended_is_named_by_its_address_and_asks_the_server_when_it_starts()
    {
        var url = _server.FileUrl(120_000, "get", "disposition=Real name.zip");
        var guess = new ProbeResult(url, null, false, "get", null, null, null);

        var item = await _service.AddAsync(new AddDownloadOptions { Url = url, SaveFolder = _temp.Combine("one"), StartNow = true }, guess);
        var done = await _engine.WaitAsync(item.Id).WaitAsync(TimeSpan.FromMinutes(1));

        done!.Status.Should().Be(DownloadStatus.Completed, done.ErrorMessage);
        done.TotalBytes.Should().Be(120_000);
        Path.GetDirectoryName(done.DestPath).Should().Be(_temp.Combine("one"));
    }
}
