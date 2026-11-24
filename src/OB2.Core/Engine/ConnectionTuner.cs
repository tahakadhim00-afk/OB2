namespace OB2.Core.Engine;

/// <summary>
/// D-90: finds how many connections a download should use. It starts at the configured number
/// and, while adding connections makes OB2 faster, adds a few more each step, up to a ceiling.
/// After a step it waits one window (the new connections are still connecting and ramping up),
/// then compares the next window's speed with the speed before the step: a gain of at least
/// <see cref="RequiredGain"/> keeps the step and tries another; less undoes it. D-91: undoing a
/// step no longer ends tuning, because one slow reading on a line whose speed swings (Starlink,
/// mobile) would otherwise leave a download on fewer connections than it could use for the rest
/// of the run. It waits <see cref="CoolDownWindows"/> instead, longer after each undone step, and
/// tries again. Throttling by the server still ends it. Deterministic, so each rule can be tested.
/// </summary>
public sealed class ConnectionTuner
{
    /// <summary>
    /// D-91: a speed limit holds a download back once the speed it limits is at least this share
    /// of it; below that, the limit is not what decides the speed and more connections may help.
    /// </summary>
    public const double NearLimitShare = 0.8;

    private readonly int _ceiling;
    private int _windows;
    private bool _settling;
    private int? _stepFrom;
    private double _before;
    private int _undone;
    private int _coolDown;

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

    /// <summary>
    /// D-91: the connections last known to be worth it: <see cref="Limit"/>, or while a step
    /// waits to be judged, the number before it.
    /// </summary>
    public int Proven => _stepFrom ?? Limit;

    /// <summary>Tuning is over: the ceiling was reached, or the server pushed back.</summary>
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

    /// <summary>
    /// D-91: the windows to wait before trying again after the <paramref name="undone"/>-th
    /// undone step: 10, 20, 40, then 80 (30 s, 1, 2 and 4 minutes at 3 s a window). A line that
    /// really is full costs one short try every few minutes; a slow moment costs half a minute.
    /// </summary>
    public static int CoolDownWindows(int undone) => 10 << Math.Clamp(undone - 1, 0, 3);

    /// <summary>
    /// D-91: whether a speed limit of <paramref name="limitBytesPerSecond"/> (null: none) is what
    /// holds back a speed of <paramref name="bytesPerSecond"/>. A limit far above the speed (a
    /// 50 MB/s cap on a 3 MB/s download) is not, so it must not stop connections being added.
    /// </summary>
    public static bool IsNearLimit(double bytesPerSecond, long? limitBytesPerSecond) =>
        limitBytesPerSecond is > 0 and var limit && bytesPerSecond >= NearLimitShare * limit;

    /// <summary>Called once per measuring window.</summary>
    /// <param name="bytesPerSecond">The speed measured over the window just ended.</param>
    /// <param name="canGrow">Whether more connections could be used now: enough left to download, a free connection to the server, no speed limit holding it back.</param>
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
                // More connections did not make it faster: the line or the server is the limit,
                // at least for now. The window after this one still has the connections being
                // given back in it, so the wait counts from the next.
                Limit = from;
                _coolDown = CoolDownWindows(++_undone);
                _settling = true;
                return Limit;
            }
        }

        if (_coolDown > 0)
        {
            _coolDown--;
            return Limit;
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
