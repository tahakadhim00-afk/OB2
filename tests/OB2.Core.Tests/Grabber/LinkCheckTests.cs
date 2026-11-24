using System.Net;
using OB2.Core.Grabber;
using OB2.Core.Http;

namespace OB2.Core.Tests.Grabber;

/// <summary>D-87: what checking a grabbed link means for its row, and how many checks run at once.</summary>
public sealed class LinkCheckTests
{
    private static readonly Uri File = new("https://x.example/a.zip");

    private static ProbeResult Probe(string? contentType, long? size = 1000, string name = "a.zip") =>
        new(File, size, true, name, null, null, contentType);

    [Theory]
    [InlineData("application/zip")]
    [InlineData("application/octet-stream")]
    [InlineData("video/mp4")]
    [InlineData(null)]
    public void A_server_that_sends_anything_but_HTML_sends_a_file(string? contentType)
    {
        var result = LinkCheckResult.FromProbe(Probe(contentType));
        result.State.Should().Be(LinkState.File);
        result.Probe!.TotalBytes.Should().Be(1000);
        result.IsFailure.Should().BeFalse();
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("TEXT/HTML")]
    [InlineData("application/xhtml+xml")]
    public void HTML_is_a_web_page_not_a_file(string contentType) =>
        LinkCheckResult.FromProbe(Probe(contentType)).State.Should().Be(LinkState.WebPage);

    [Theory]
    [InlineData(HttpStatusCode.NotFound, LinkState.NotFound)]
    [InlineData(HttpStatusCode.Gone, LinkState.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized, LinkState.AccessDenied)]
    [InlineData(HttpStatusCode.Forbidden, LinkState.AccessDenied)]
    [InlineData(HttpStatusCode.InternalServerError, LinkState.Failed)]
    public void A_refusal_says_what_kind_of_refusal(HttpStatusCode status, LinkState expected)
    {
        var result = LinkCheckResult.FromError(new HttpStatusException(status, "Reason"));
        result.State.Should().Be(expected);
        result.Message.Should().Be($"The server answered {(int)status} Reason.");
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void No_answer_and_no_connection_keep_their_reason()
    {
        LinkCheckResult.FromError(new TimeoutException("The server did not answer within 33 seconds.")).Should()
            .Be(new LinkCheckResult(LinkState.NoAnswer, Message: "The server did not answer within 33 seconds."));
        LinkCheckResult.FromError(new HttpRequestException("No such host is known. (x.invalid:443)")).Should()
            .Be(new LinkCheckResult(LinkState.Failed, Message: "No such host is known. (x.invalid:443)"));
    }

    [Fact]
    public async Task A_YouTube_video_is_a_video_page_and_its_page_is_never_requested()
    {
        var asked = 0;
        var checker = new LinkChecker((_, _, _) => { asked++; return Task.FromResult(Probe("text/html")); });
        var result = await checker.CheckAsync(new Uri("https://youtu.be/dQw4w9WgXcQ"));
        result.State.Should().Be(LinkState.VideoPage);
        asked.Should().Be(0);
    }

    [Fact]
    public async Task A_failing_probe_becomes_its_row_status_instead_of_an_error()
    {
        var checker = new LinkChecker((_, _, _) => throw new HttpStatusException(HttpStatusCode.NotFound, "Not Found"));
        (await checker.CheckAsync(File)).State.Should().Be(LinkState.NotFound);
    }

    [Fact]
    public async Task No_more_than_the_limit_are_checked_at_once()
    {
        var running = 0;
        var most = 0;
        var checker = new LinkChecker(async (url, _, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            await Task.Delay(30, ct);
            Interlocked.Decrement(ref running);
            return Probe("application/zip", name: url.Segments[^1]);
        }, parallel: 3);

        var results = await Task.WhenAll(Enumerable.Range(1, 20).Select(i => checker.CheckAsync(new Uri($"https://x.example/{i}.zip"))));

        results.Should().OnlyContain(r => r.State == LinkState.File);
        results.Select(r => r.Probe!.SuggestedFileName).Should().Equal(Enumerable.Range(1, 20).Select(i => $"{i}.zip"));
        most.Should().Be(3);
    }

    [Fact]
    public async Task Closing_the_window_stops_the_checks_waiting_and_running()
    {
        using var cts = new CancellationTokenSource();
        var checker = new LinkChecker(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Probe("application/zip");
        }, parallel: 1);

        var running = checker.CheckAsync(new Uri("https://x.example/1.zip"), cancellationToken: cts.Token);
        var waiting = checker.CheckAsync(new Uri("https://x.example/2.zip"), cancellationToken: cts.Token);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => running).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => waiting).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_link_found_on_a_page_is_checked_with_the_page_as_referrer()
    {
        Uri? sent = null;
        var checker = new LinkChecker((_, referrer, _) => { sent = referrer; return Task.FromResult(Probe("application/zip")); });
        await checker.CheckAsync(File, new Uri("https://x.example/downloads"));
        sent.Should().Be(new Uri("https://x.example/downloads"));
    }

    [Fact]
    public async Task Reading_a_page_lists_its_file_links_against_the_address_it_ended_at()
    {
        var checker = new LinkChecker(
            (_, _, _) => Task.FromResult(Probe("text/html")),
            (_, _, _) => Task.FromResult(("<a href='a.zip'>A</a> <a href='/b.pdf'>B</a> <a href='about'>About</a>", new Uri("https://x.example/files/index.html"))));

        var result = await checker.ReadPageAsync(new Uri("https://x.example/files"), null, null);

        result.Error.Should().BeNull();
        result.Links.Select(u => u.AbsoluteUri).Should().Equal("https://x.example/files/a.zip", "https://x.example/b.pdf");
    }

    [Fact]
    public async Task A_page_that_cannot_be_read_gives_its_reason()
    {
        var checker = new LinkChecker(
            (_, _, _) => Task.FromResult(Probe("text/html")),
            (_, _, _) => throw new HttpStatusException(HttpStatusCode.Forbidden, "Forbidden"));

        var result = await checker.ReadPageAsync(new Uri("https://x.example/files"), null, null);

        result.Links.Should().BeEmpty();
        result.Error.Should().Be("The server answered 403 Forbidden.");
    }

    [Fact]
    public async Task Pages_are_read_in_the_same_slots_as_the_checks()
    {
        var running = 0;
        var most = 0;
        async Task<T> Slow<T>(T value, CancellationToken ct)
        {
            InterlockedMax(ref most, Interlocked.Increment(ref running));
            await Task.Delay(30, ct);
            Interlocked.Decrement(ref running);
            return value;
        }

        var checker = new LinkChecker(
            (_, _, ct) => Slow(Probe("application/zip"), ct),
            (url, _, ct) => Slow((string.Empty, url), ct),
            parallel: 2);

        await Task.WhenAll(Enumerable.Range(1, 6).Select(i => i % 2 == 0
            ? (Task)checker.CheckAsync(new Uri($"https://x.example/{i}.zip"))
            : checker.ReadPageAsync(new Uri($"https://x.example/page{i}"), null, null)));

        most.Should().Be(2);
    }

    [Theory]
    [InlineData(LinkState.File, "movie.MKV", LinkGroup.Video)]
    [InlineData(LinkState.Checking, "song.mp3", LinkGroup.Music)]
    [InlineData(LinkState.File, "report.pdf", LinkGroup.Documents)]
    [InlineData(LinkState.File, "photo.webp", LinkGroup.Pictures)]
    [InlineData(LinkState.File, "backup.tar.gz", LinkGroup.Compressed)]
    [InlineData(LinkState.File, "setup.exe", LinkGroup.Programs)]
    [InlineData(LinkState.File, "data.torrent", LinkGroup.OtherFiles)]
    [InlineData(LinkState.File, "download", LinkGroup.OtherFiles)]
    [InlineData(LinkState.WebPage, "a.zip", LinkGroup.WebPages)]
    [InlineData(LinkState.VideoPage, "watch", LinkGroup.Video)]
    [InlineData(LinkState.NotFound, "a.zip", LinkGroup.NotWorking)]
    [InlineData(LinkState.AccessDenied, "a.mp4", LinkGroup.NotWorking)]
    [InlineData(LinkState.NoAnswer, "a.mp4", LinkGroup.NotWorking)]
    [InlineData(LinkState.Failed, "a.mp4", LinkGroup.NotWorking)]
    public void Each_link_is_in_one_filter_group(LinkState state, string fileName, LinkGroup expected) =>
        LinkGroups.Of(state, fileName).Should().Be(expected);

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
