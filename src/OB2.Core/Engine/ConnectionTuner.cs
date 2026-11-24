namespace OB2.Core.Engine;

/// <summary>
/// D-90: finds how many connections a download should use. It starts at the configured number
/// and, while adding connections makes OB2 faster, adds a few more each step, up to a ceiling.
/// After a step it waits one window (the new connections are still connecting and ramping up),
/// then compares the next window's speed with the speed before the step: a gain of at least
/// <see cref="RequiredGain"/> keeps the step and tries another; less undoes it and stops for
/// good. Throttling by the server also stops it. Deterministic, so each rule can be tested.
/// </summary>
public sealed class ConnectionTuner
{
    private readonly int _ceiling;
    private int _windows;
    private bool _settling;
    private int? _stepFrom;
    private double _before;

    /// <param name="start">The connections the download starts with.</param>
    /// <param name="ceiling">Never more than this (the per-server limit, and 32).</param>
    public ConnectionTuner(int start, int ceiling)
    {
        Limit = Math.Max(1, start);
        _ceiling = Math.Max(Limit, ceiling);
        Settled = Limit >= _ceiling;
    }

    /// <summary>The connections the download should use now.</summary>
    public int Limit { get; private set; }

    /// <summary>Tuning is over: the best number was found, the ceiling reached, or the server pushed back.</summary>
    public bool Settled { get; private set; }

    /// <summary>The connections added by one step: two, or a quarter of the current number when that is more.</summary>
    public static int StepFrom(int limit) => Math.Max(2, limit / 4);

    /// <summary>
    /// How much faster a step from <paramref name="from"/> to <paramref name="to"/> connections
    /// must make OB2 to stay: half of what the added connections would bring if each were as fast
    /// as the others (8 → 10 adds a quarter, so 12.5 %; 15 → 16 adds a fifteenth, so 3.3 %).
    /// Half, because real lines are noisy and a connection too many costs little.
    /// </summary>
    public static double RequiredGain(int from, int to) => 0.5 * (to - from) / Math.Max(1, from);

    /// <summary>Called once per measuring window.</summary>
    /// <param name="bytesPerSecond">The speed measured over the window just ended.</param>
    /// <param name="canGrow">Whether more connections could be used now: enough left to download, a free connection to the server, no speed limit.</param>
    /// <returns>The connections to use from now on.</returns>
    public int Next(double bytesPerSecond, bool canGrow)
    {
        if (Settled)
        {
            return Limit;
        }

        if (++_windows == 1 || _settling)
        {
            // The first window includes connecting and TCP's slow start; so does the one right
            // after a step for the new connections. Neither says what the connections can do.
            _settling = false;
            return Limit;
        }

        if (_stepFrom is { } from)
        {
            _stepFrom = null;
            if (bytesPerSecond < _before * (1 + RequiredGain(from, Limit)))
            {
                // More connections did not make it faster: the line or the server is the limit.
                Limit = from;
                Settled = true;
                return Limit;
            }
        }

        if (!canGrow || Limit >= _ceiling)
        {
            Settled = Limit >= _ceiling;
            return Limit;
        }

        _before = bytesPerSecond;
        _stepFrom = Limit;
        Limit = Math.Min(_ceiling, Limit + StepFrom(Limit));
        _settling = true;
        return Limit;
    }

    /// <summary>The server refused or throttled connections: never add any again.</summary>
    public void Throttled() => Settled = true;
}
