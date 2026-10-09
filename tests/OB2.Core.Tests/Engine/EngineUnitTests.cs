using OB2.Core.Downloads;
using OB2.Core.Engine;
using OB2.Core.Http;
using OB2.Core.Integrity;
using OB2.Core.Logging;
using OB2.Tests.Common;

namespace OB2.Core.Tests.Engine;

public sealed class SegmentPlannerTests
{
    [Fact]
    public void Unknown_size_gives_one_open_ended_segment()
    {
        var plan = SegmentPlanner.Plan(null, supportsRanges: true, connections: 8, minSegmentBytes: 1024);

        plan.Should().ContainSingle().Which.IsOpenEnded.Should().BeTrue();
    }

    [Fact]
    public void No_range_support_gives_one_whole_file_segment()
    {
        var plan = SegmentPlanner.Plan(10_000_000, supportsRanges: false, connections: 8, minSegmentBytes: 1024);

        plan.Should().ContainSingle();
        plan[0].Start.Should().Be(0);
        plan[0].End.Should().Be(9_999_999);
    }

    [Theory]
    [InlineData(1_000_000_000L, 8, 8)]
    [InlineData(1_000_000_000L, 32, 32)]
    [InlineData(1_000_000_000L, 64, 32)]
    [InlineData(3_000L, 8, 2)]
    [InlineData(1_500L, 8, 1)]
    [InlineData(10L, 8, 1)]
    public void Splits_into_contiguous_ranges(long size, int connections, int expectedCount)
    {
        var plan = SegmentPlanner.Plan(size, supportsRanges: true, connections, minSegmentBytes: 1024);

        plan.Should().HaveCount(expectedCount);
        plan[0].Start.Should().Be(0);
        plan[^1].End.Should().Be(size - 1);
        for (var i = 1; i < plan.Count; i++)
        {
            plan[i].Start.Should().Be(plan[i - 1].End + 1);
            plan[i].Index.Should().Be(i);
        }

        plan.Sum(s => s.Length!.Value).Should().Be(size);
    }

    [Fact]
    public void Zero_size_needs_no_segments()
    {
        SegmentPlanner.Plan(0, supportsRanges: true, connections: 8, minSegmentBytes: 1024).Should().BeEmpty();
    }
}

public sealed class ChecksumTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("md5", "900150983cd24fb0d6963f7d28e17f72")]
    [InlineData("MD5", "900150983CD24FB0D6963F7D28E17F72")]
    [InlineData("sha256", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("SHA-256", "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD")]
    public async Task Known_vectors_for_abc_match(string algorithm, string hex)
    {
        var path = _temp.Combine("abc.txt");
        await File.WriteAllTextAsync(path, "abc");

        Checksum.TryCreate(algorithm, hex, out var checksum).Should().BeTrue();

        (await checksum!.MatchesAsync(path)).Should().BeTrue();
    }

    [Theory]
    [InlineData("sha1", "a9993e364706816aba3e25717850c26c9cd0d89d")]
    [InlineData("md5", "too-short")]
    [InlineData("sha256", "900150983cd24fb0d6963f7d28e17f72")]
    [InlineData("md5", "zz0150983cd24fb0d6963f7d28e17f72")]
    [InlineData(null, "900150983cd24fb0d6963f7d28e17f72")]
    public void Invalid_input_is_rejected(string? algorithm, string hex)
    {
        Checksum.TryCreate(algorithm, hex, out _).Should().BeFalse();
    }
}

public sealed class UrlValidatorTests
{
    [Theory]
    [InlineData("https://example.com/a.zip", UrlCheck.Ok)]
    [InlineData("http://example.com/a.zip", UrlCheck.Ok)]
    [InlineData("https://user:pass@example.com/a.zip", UrlCheck.HasCredentials)]
    [InlineData("ftp://example.com/a.zip", UrlCheck.UnsupportedScheme)]
    [InlineData("file:///C:/Windows/win.ini", UrlCheck.UnsupportedScheme)]
    [InlineData("javascript:alert(1)", UrlCheck.UnsupportedScheme)]
    [InlineData("example.com/a.zip", UrlCheck.Invalid)]
    [InlineData("", UrlCheck.Invalid)]
    public void Classifies(string url, UrlCheck expected)
    {
        UrlValidator.Check(url, out _).Should().Be(expected);
    }

    [Fact]
    public void Rejects_urls_over_2048_characters()
    {
        UrlValidator.Check("https://example.com/" + new string('a', 2048), out _).Should().Be(UrlCheck.TooLong);
    }
}

public sealed class LogRedactorTests
{
    [Theory]
    [InlineData("https://cdn.example.com/f/setup.exe?token=SECRET&sig=abc", "https://cdn.example.com/f/setup.exe?…")]
    [InlineData("https://user:pass@example.com/a.zip", "https://example.com/a.zip")]
    [InlineData("http://example.com:8080/a.zip#frag", "http://example.com:8080/a.zip")]
    [InlineData("not a url", "[unparsable url]")]
    public void Strips_secrets(string url, string expected)
    {
        LogRedactor.Url(url).Should().Be(expected);
    }
}

public sealed class RequestHeaderTests
{
    [Fact]
    public void Credentials_and_transport_headers_are_not_persisted()
    {
        var headers = new Dictionary<string, string>
        {
            ["Accept-Language"] = "en-US",
            ["Cookie"] = "a=b",
            ["authorization"] = "Bearer x",
            ["Range"] = "bytes=0-",
            ["Host"] = "evil.test",
        };

        DownloadHttpClient.PersistableHeaders(headers).Should().ContainSingle().Which.Key.Should().Be("Accept-Language");
    }

    [Fact]
    public void Custom_headers_cannot_override_host_or_range()
    {
        var context = new RequestContext("UA", null, null, new Dictionary<string, string> { ["Host"] = "evil.test", ["Range"] = "bytes=5-", ["X-Test"] = "1" });

        using var request = DownloadHttpClient.CreateRequest(HttpMethod.Get, new Uri("https://example.com/a"), context);

        request.Headers.Host.Should().BeNull();
        request.Headers.Range.Should().BeNull();
        request.Headers.GetValues("X-Test").Should().Equal("1");
    }
}

public sealed class SegmentTests
{
    [Fact]
    public void Tracks_position_and_completion()
    {
        var segment = new Segment { Index = 1, Start = 100, End = 199 };

        segment.Length.Should().Be(100);
        segment.AddDownloaded(60);
        segment.Position.Should().Be(160);
        segment.IsComplete.Should().BeFalse();
        segment.AddDownloaded(40);
        segment.IsComplete.Should().BeTrue();
    }
}
