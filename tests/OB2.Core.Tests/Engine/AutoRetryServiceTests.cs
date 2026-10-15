using OB2.Core.Downloads;
using OB2.Core.Engine;
using OB2.Core.Settings;

namespace OB2.Core.Tests.Engine;

public sealed class AutoRetryServiceTests
{
    [Theory]
    // Worth another attempt: the line or the server was temporarily unavailable.
    [InlineData(ErrorCodes.Network, true)]
    [InlineData(ErrorCodes.Timeout, true)]
    [InlineData("HTTP_500", true)]
    [InlineData("HTTP_503", true)]
    [InlineData("HTTP_429", true)]
    [InlineData("HTTP_408", true)]
    // Hopeless: another attempt fails the same way, so retrying only wastes bandwidth.
    [InlineData(ErrorCodes.ChecksumMismatch, false)]
    [InlineData(ErrorCodes.DiskFull, false)]
    [InlineData(ErrorCodes.UnsafePath, false)]
    [InlineData(ErrorCodes.FileError, false)]
    [InlineData(ErrorCodes.RangeNotSupported, false)]
    [InlineData(ErrorCodes.ToolsMissing, false)]
    [InlineData("HTTP_404", false)]
    [InlineData("HTTP_403", false)]
    [InlineData("HTTP_401", false)]
    [InlineData(null, false)]
    public void Only_recoverable_failures_are_retried(string? code, bool expected) =>
        AutoRetryService.IsWorthRetrying(code).Should().Be(expected);

    [Fact]
    public async Task Failure_is_started_again_until_the_budget_runs_out()
    {
        var settings = new DownloadSettings { AutoRetryAttempts = 2, AutoRetryDelaySeconds = 0 };
        var started = new List<long>();
        using var service = new AutoRetryService(() => settings, started.Add);
        var item = Failed(7, ErrorCodes.Network);

        service.OnFailed(item);
        await WaitFor(() => started.Count >= 1);
        started.Should().Equal(7L);

        service.OnFailed(item);
        await WaitFor(() => started.Count >= 2);
        started.Should().Equal(7L, 7L);

        // Third failure is past the two allowed attempts.
        service.OnFailed(item);
        await Task.Delay(150);
        started.Should().HaveCount(2, "the budget is spent");
    }

    [Fact]
    public async Task Hopeless_failure_is_never_started_again()
    {
        var settings = new DownloadSettings { AutoRetryAttempts = 5, AutoRetryDelaySeconds = 0 };
        var started = new List<long>();
        using var service = new AutoRetryService(() => settings, started.Add);

        service.OnFailed(Failed(1, ErrorCodes.ChecksumMismatch));
        await Task.Delay(150);

        started.Should().BeEmpty();
    }

    [Fact]
    public async Task Disabled_by_default()
    {
        var started = new List<long>();
        using var service = new AutoRetryService(() => new DownloadSettings(), started.Add);

        service.OnFailed(Failed(1, ErrorCodes.Network));
        await Task.Delay(150);

        started.Should().BeEmpty("AutoRetryAttempts defaults to 0");
    }

    [Fact]
    public async Task Reset_restores_the_budget_and_cancels_a_pending_retry()
    {
        var settings = new DownloadSettings { AutoRetryAttempts = 1, AutoRetryDelaySeconds = 30 };
        var started = new List<long>();
        using var service = new AutoRetryService(() => settings, started.Add);
        var item = Failed(3, ErrorCodes.Network);

        service.OnFailed(item);      // waits 30s, so it has not fired yet
        service.Reset(item.Id);      // the user pressed Retry
        await Task.Delay(150);
        started.Should().BeEmpty("the pending retry was cancelled");

        // The budget is fresh, so a later failure is retried again.
        settings.AutoRetryDelaySeconds = 0;
        service.OnFailed(item);
        await WaitFor(() => started.Count >= 1);
        started.Should().Equal(3L);
    }

    [Fact]
    public async Task Dispose_cancels_pending_retries()
    {
        var settings = new DownloadSettings { AutoRetryAttempts = 3, AutoRetryDelaySeconds = 30 };
        var started = new List<long>();
        var service = new AutoRetryService(() => settings, started.Add);

        service.OnFailed(Failed(5, ErrorCodes.Timeout));
        service.Dispose();
        await Task.Delay(150);

        started.Should().BeEmpty();
    }

    private static DownloadItem Failed(long id, string? code) => new()
    {
        Id = id,
        Url = "https://example.com/a.bin",
        FileName = "a.bin",
        DestPath = @"C:\Downloads\a.bin",
        Status = DownloadStatus.Failed,
        ErrorCode = code,
    };

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }
    }
}
