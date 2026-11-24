using OB2.Core.Engine;

namespace OB2.Core.Tests.Engine;

/// <summary>D-90: connections are added while that makes downloads faster, and only then.</summary>
public sealed class ConnectionTunerTests
{
    /// <summary>Speed as a function of connections: each adds 4 MB/s until the line's limit.</summary>
    private static double Line(int connections, double perConnection = 4, double line = 1000) => Math.Min(connections * perConnection, line);

    /// <summary>Runs windows until the tuner settles, the speed in each window coming from the connections it chose before.</summary>
    private static (int Limit, List<int> Steps) Run(ConnectionTuner tuner, Func<int, double> speedOf, bool canGrow = true, int windows = 50)
    {
        var steps = new List<int> { tuner.Limit };
        for (var i = 0; i < windows && !tuner.Settled; i++)
        {
            var next = tuner.Next(speedOf(tuner.Limit), canGrow);
            if (next != steps[^1])
            {
                steps.Add(next);
            }
        }

        return (tuner.Limit, steps);
    }

    [Fact]
    public void A_server_that_limits_each_connection_gets_connections_up_to_the_ceiling()
    {
        var (limit, steps) = Run(new ConnectionTuner(8, 16), c => Line(c));
        limit.Should().Be(16);
        steps.Should().Equal(8, 10, 12, 15, 16);
    }

    [Fact]
    public void When_the_line_is_full_the_last_step_is_undone_and_only_tried_again_later()
    {
        // 4 MB/s per connection on a 40 MB/s line: 10 connections fill it.
        var tuner = new ConnectionTuner(8, 16);
        var (limit, steps) = Run(tuner, c => Line(c, line: 40), windows: 15);
        steps.Should().Equal(8, 10, 12, 10);
        limit.Should().Be(10);
        tuner.Settled.Should().BeFalse("D-91: going back is not the end; the line may have had a slow moment");
    }

    [Fact]
    public void A_gain_well_under_what_the_added_connections_should_bring_is_not_worth_them()
    {
        // 8 -> 10 is a quarter more connections; 9 % faster is under the 12.5 % asked.
        var tuner = new ConnectionTuner(8, 16);
        var (limit, steps) = Run(tuner, c => c == 8 ? 100 : 109, windows: 10);
        steps.Should().Equal(8, 10, 8);
        limit.Should().Be(8);
    }

    [Fact]
    public void After_going_back_it_waits_longer_each_time_before_trying_again()
    {
        // A full line: every try is undone. Count the windows between one undo and the next try.
        var tuner = new ConnectionTuner(8, 16);
        var waits = new List<int>();
        var sinceUndo = -1;
        for (var i = 0; i < 1000 && waits.Count < 5; i++)
        {
            var before = tuner.Limit;
            var next = tuner.Next(100, canGrow: true);
            if (next < before)
            {
                sinceUndo = 0;
            }
            else if (next > before && sinceUndo >= 0)
            {
                waits.Add(sinceUndo);
            }
            else if (sinceUndo >= 0)
            {
                sinceUndo++;
            }
        }

        // One window for the connections being given back, then the cool-down, then the window
        // whose speed the next try is judged against.
        waits.Should().Equal(
            1 + ConnectionTuner.CoolDownWindows(1),
            1 + ConnectionTuner.CoolDownWindows(2),
            1 + ConnectionTuner.CoolDownWindows(3),
            1 + ConnectionTuner.CoolDownWindows(4),
            1 + ConnectionTuner.CoolDownWindows(5));
        tuner.Settled.Should().BeFalse("it keeps trying, ever less often");
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(4, 80)]
    [InlineData(9, 80)]
    public void The_wait_doubles_up_to_80_windows(int undone, int windows) =>
        ConnectionTuner.CoolDownWindows(undone).Should().Be(windows);

    [Fact]
    public void A_slow_moment_costs_one_wait_and_then_the_connections_are_added_after_all()
    {
        // 0.3 MB/s per connection, like the servers in the owner's log, but the window that
        // judges the first step comes out slow (a dip on the line).
        var tuner = new ConnectionTuner(8, 32);
        var dip = true;
        double SpeedOf(int c)
        {
            if (dip && tuner.Limit == 10)
            {
                return Line(8, 0.3); // no gain seen for 8 -> 10
            }

            return Line(c, 0.3);
        }

        var steps = new List<int> { tuner.Limit };
        for (var i = 0; i < 200 && !tuner.Settled; i++)
        {
            var next = tuner.Next(SpeedOf(tuner.Limit), canGrow: true);
            if (next < steps[^1])
            {
                dip = false;
            }

            if (next != steps[^1])
            {
                steps.Add(next);
            }
        }

        steps.Should().Equal(8, 10, 8, 10, 12, 15, 18, 22, 27, 32);
        tuner.Settled.Should().BeTrue("the ceiling was reached");
    }

    [Fact]
    public void A_step_not_judged_yet_is_not_proven()
    {
        var tuner = new ConnectionTuner(8, 16);
        tuner.Next(100, true);
        tuner.Next(100, true).Should().Be(10);
        tuner.Proven.Should().Be(8, "nothing has shown that 10 helps yet");
        tuner.Next(0, true);
        tuner.Next(150, true);
        tuner.Proven.Should().BeGreaterThanOrEqualTo(10, "10 was faster");
    }

    [Theory]
    [InlineData(3_000_000.0, null, false)]
    [InlineData(3_000_000.0, 51_200_000L, false)] // the owner's 50,000 KB/s cap on a 3 MB/s download
    [InlineData(2_000_000.0, 3_000_000L, false)]
    [InlineData(2_400_000.0, 3_000_000L, true)]
    [InlineData(3_000_000.0, 3_000_000L, true)]
    public void A_speed_limit_holds_a_download_back_only_near_it(double speed, long? limit, bool near) =>
        ConnectionTuner.IsNearLimit(speed, limit).Should().Be(near);

    [Fact]
    public void A_small_last_step_is_judged_by_what_it_adds()
    {
        // 15 -> 16 adds a fifteenth: 6.7 % faster is enough (3.3 % asked), though under 10 %.
        var tuner = new ConnectionTuner(15, 16);
        tuner.Next(60, true);
        tuner.Next(60, true).Should().Be(16);
        tuner.Next(0, true);
        tuner.Next(64, true).Should().Be(16);
    }

    [Theory]
    [InlineData(8, 10, 0.125)]
    [InlineData(10, 12, 0.10)]
    [InlineData(15, 16, 1.0 / 30)]
    public void The_gain_asked_is_half_the_share_of_connections_added(int from, int to, double gain) =>
        ConnectionTuner.RequiredGain(from, to).Should().BeApproximately(gain, 1e-9);

    [Fact]
    public void The_first_window_and_the_one_after_each_step_are_not_judged()
    {
        var tuner = new ConnectionTuner(8, 16);
        tuner.Next(1, canGrow: true).Should().Be(8, "the first window is connecting and slow start");
        tuner.Next(100, canGrow: true).Should().Be(10, "a step from a measured window");
        tuner.Next(1, canGrow: true).Should().Be(10, "the new connections are still starting: a slow window says nothing");
        tuner.Next(100, canGrow: true).Should().Be(8, "the next window is judged: no gain, undone");
    }

    [Fact]
    public void Nothing_is_added_while_more_could_not_be_used_and_adding_resumes_after()
    {
        var tuner = new ConnectionTuner(8, 16);
        tuner.Next(32, canGrow: false);
        for (var i = 0; i < 5; i++)
        {
            tuner.Next(32, canGrow: false).Should().Be(8);
        }

        tuner.Settled.Should().BeFalse("a free connection to the server may come later");
        tuner.Next(32, canGrow: true).Should().Be(10);
    }

    [Fact]
    public void A_server_that_pushes_back_never_gets_more()
    {
        var tuner = new ConnectionTuner(8, 16);
        tuner.Next(32, true);
        tuner.Throttled();
        tuner.Next(32, true).Should().Be(8);
        tuner.Settled.Should().BeTrue();
    }

    [Fact]
    public void Starting_at_or_above_the_ceiling_means_nothing_to_tune()
    {
        new ConnectionTuner(16, 16).Settled.Should().BeTrue();
        new ConnectionTuner(20, 16).Limit.Should().Be(20, "a ceiling never takes connections away; the per-server limit does that");
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(8, 2)]
    [InlineData(12, 3)]
    [InlineData(16, 4)]
    public void Steps_are_two_or_a_quarter(int limit, int step) => ConnectionTuner.StepFrom(limit).Should().Be(step);
}
