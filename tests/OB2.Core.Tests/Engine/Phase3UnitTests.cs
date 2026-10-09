using System.Diagnostics;
using System.Net;
using OB2.Core.Engine;
using OB2.Core.Http;

namespace OB2.Core.Tests.Engine;

public sealed class TokenBucketTests
{
    [Fact]
    public async Task Unlimited_never_waits()
    {
        var bucket = new TokenBucket();
        var watch = Stopwatch.StartNew();

        for (var i = 0; i < 1000; i++)
        {
            await bucket.WaitAsync(1 << 20, CancellationToken.None);
        }

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(200));
        bucket.IsLimited.Should().BeFalse();
    }

    [Fact]
    public async Task Paces_to_the_configured_rate_after_a_one_second_burst()
    {
        var bucket = new TokenBucket(200_000);
        await Task.Delay(1100); // fill the burst allowance
        var watch = Stopwatch.StartNew();

        for (var sent = 0; sent < 600_000; sent += 20_000)
        {
            await bucket.WaitAsync(20_000, CancellationToken.None);
        }

        // 600 KB at 200 KB/s with a 200 KB burst needs about 2 s.
        watch.Elapsed.TotalSeconds.Should().BeInRange(1.7, 3.0);
    }

    [Fact]
    public async Task Rate_can_be_cleared()
    {
        var bucket = new TokenBucket(1000) { BytesPerSecond = null };
        var watch = Stopwatch.StartNew();

        await bucket.WaitAsync(10_000_000, CancellationToken.None);

        bucket.IsLimited.Should().BeFalse();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(100));
    }

    [Theory]
    [InlineData(null, 262144, 262144)]
    [InlineData(1_000_000L, 262144, 100_000)]
    [InlineData(10_000L, 262144, 4096)]
    public void Suggests_smaller_reads_when_limited(long? rate, int max, int expected)
    {
        new TokenBucket(rate).SuggestedChunk(max).Should().Be(expected);
    }
}

public sealed class RetryPolicyTests
{
    private readonly RetryPolicy _policy = new(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(2, 2.0)]
    [InlineData(3, 4.0)]
    [InlineData(5, 16.0)]
    [InlineData(6, 30.0)]
    [InlineData(20, 30.0)]
    public void Backoff_doubles_with_jitter_and_a_cap(int attempt, double nominalSeconds)
    {
        for (var i = 0; i < 50; i++)
        {
            _policy.DelayFor(attempt).TotalSeconds.Should().BeInRange(nominalSeconds * 0.8, nominalSeconds * 1.2);
        }
    }

    [Fact]
    public void Retry_after_header_wins_but_is_capped()
    {
        _policy.DelayFor(1, new HttpStatusException(HttpStatusCode.TooManyRequests, null, TimeSpan.FromSeconds(7))).Should().Be(TimeSpan.FromSeconds(7));
        _policy.DelayFor(1, new HttpStatusException(HttpStatusCode.ServiceUnavailable, null, TimeSpan.FromHours(1))).Should().Be(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Gone, false)]
    public void Classifies_http_statuses(HttpStatusCode code, bool transient)
    {
        RetryPolicy.IsTransient(new HttpStatusException(code, null)).Should().Be(transient);
    }

    [Fact]
    public void Classifies_other_errors()
    {
        RetryPolicy.IsTransient(new IOException("reset")).Should().BeTrue();
        RetryPolicy.IsTransient(new TimeoutException()).Should().BeTrue();
        RetryPolicy.IsTransient(new HttpRequestException("refused")).Should().BeTrue();
        RetryPolicy.IsTransient(new DownloadFailedException(ErrorCodes.DiskFull, "full")).Should().BeFalse();
        RetryPolicy.IsTransient(new RangeNotSupportedException("x")).Should().BeFalse();
        RetryPolicy.IsTransient(new InvalidOperationException()).Should().BeFalse();
        RetryPolicy.IsThrottling(new HttpStatusException(HttpStatusCode.TooManyRequests, null)).Should().BeTrue();
        RetryPolicy.IsThrottling(new HttpStatusException(HttpStatusCode.NotFound, null)).Should().BeFalse();
    }
}

public sealed class HostConnectionLimiterTests
{
    [Fact]
    public async Task Caps_connections_per_host_but_not_across_hosts()
    {
        var limiter = new HostConnectionLimiter(2);
        var a = new Uri("https://a.test/x");

        using var first = await limiter.AcquireAsync(a, CancellationToken.None);
        using var second = await limiter.AcquireAsync(a, CancellationToken.None);
        var third = limiter.AcquireAsync(a, CancellationToken.None);
        using var otherHost = await limiter.AcquireAsync(new Uri("https://b.test/x"), CancellationToken.None);

        await Task.Delay(100);
        third.IsCompleted.Should().BeFalse("the host already has two connections");

        first.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(2))).Dispose();
    }
}
