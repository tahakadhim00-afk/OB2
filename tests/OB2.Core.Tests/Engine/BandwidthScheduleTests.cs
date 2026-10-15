using System.Globalization;
using OB2.Core.Engine;
using OB2.Core.Settings;
using OB2.Tests.Common;

namespace OB2.Core.Tests.Engine;

public sealed class BandwidthScheduleTests
{
    private static readonly DateTime Wednesday = new(2026, 10, 14);

    [Theory]
    [InlineData("02:00", true)]   // inside an overnight window
    [InlineData("12:00", false)]  // the middle of the day
    [InlineData("01:00", true)]   // the moment it opens
    [InlineData("08:00", false)]  // the moment it closes, exclusive
    public void Overnight_window_opens_and_closes_on_the_hour(string time, bool expected)
    {
        var window = new BandwidthWindowSettings { Enabled = true, Start = "01:00", Stop = "08:00" };

        BandwidthSchedule.IsOpen(window, Wednesday.Add(TimeOnly.Parse(time, CultureInfo.InvariantCulture).ToTimeSpan()))
            .Should().Be(expected);
    }

    [Fact]
    public void Disabled_or_unparsable_window_is_closed()
    {
        var disabled = new BandwidthWindowSettings { Enabled = false, Start = "01:00", Stop = "08:00" };
        BandwidthSchedule.IsOpen(disabled, Wednesday.AddHours(2)).Should().BeFalse();

        // A damaged setting must leave the normal limit alone rather than throttle everything.
        var broken = new BandwidthWindowSettings { Enabled = true, Start = "not a time" };
        BandwidthSchedule.IsOpen(broken, Wednesday.AddHours(2)).Should().BeFalse();
    }

    [Fact]
    public void Window_applies_only_on_its_days()
    {
        var window = new BandwidthWindowSettings
        {
            Enabled = true,
            Start = "09:00",
            Stop = "17:00",
            Days = [DayOfWeek.Monday],
        };

        BandwidthSchedule.IsOpen(window, Wednesday.AddHours(12)).Should().BeFalse("Wednesday is not listed");
        BandwidthSchedule.IsOpen(window, Wednesday.AddDays(-2).AddHours(12)).Should().BeTrue("that Monday is");
    }

    [Fact]
    public void Apply_swaps_the_limit_as_the_window_opens_and_closes()
    {
        var limiter = new TokenBucket();
        var settings = new SpeedLimitSettings
        {
            GlobalBytesPerSecond = 2 * 1024 * 1024,
            Window = new BandwidthWindowSettings { Enabled = true, Start = "01:00", Stop = "08:00", BytesPerSecond = null },
        };
        var clock = new FakeClock(new DateTimeOffset(Wednesday.AddHours(12), TimeSpan.Zero));
        var schedule = new BandwidthSchedule(limiter, () => settings, clock, TimeZoneInfo.Utc);

        schedule.Apply();
        schedule.IsWindowOpen.Should().BeFalse();
        limiter.BytesPerSecond.Should().Be(2 * 1024 * 1024, "the daytime limit applies");

        clock.UtcNow = new DateTimeOffset(Wednesday.AddDays(1).AddHours(2), TimeSpan.Zero);
        schedule.Apply();
        schedule.IsWindowOpen.Should().BeTrue();
        limiter.BytesPerSecond.Should().BeNull("the window lifts the limit overnight");

        clock.UtcNow = new DateTimeOffset(Wednesday.AddDays(1).AddHours(9), TimeSpan.Zero);
        schedule.Apply();
        limiter.BytesPerSecond.Should().Be(2 * 1024 * 1024, "the daytime limit comes back");
    }

    [Fact]
    public void Window_can_throttle_instead_of_lifting()
    {
        // The reverse case: unlimited normally, slower during working hours.
        var limiter = new TokenBucket();
        var settings = new SpeedLimitSettings
        {
            GlobalBytesPerSecond = null,
            Window = new BandwidthWindowSettings { Enabled = true, Start = "09:00", Stop = "17:00", BytesPerSecond = 512 * 1024 },
        };
        var clock = new FakeClock(new DateTimeOffset(Wednesday.AddHours(10), TimeSpan.Zero));

        new BandwidthSchedule(limiter, () => settings, clock, TimeZoneInfo.Utc).Apply();

        limiter.BytesPerSecond.Should().Be(512 * 1024);
    }
}
