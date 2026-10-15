using OB2.Core.Organization;
using OB2.Core.Settings;
using OB2.Core.Time;

namespace OB2.Core.Engine;

/// <summary>
/// Applies the global speed limit, swapping in the window's limit while the window is open
/// (FR-9). Reuses <see cref="ScheduleEvaluator"/> so a window that runs past midnight is treated
/// exactly like an overnight queue schedule rather than by a second set of rules.
/// </summary>
public sealed class BandwidthSchedule
{
    private readonly TokenBucket _limiter;
    private readonly Func<SpeedLimitSettings> _settings;
    private readonly IClock _clock;
    private readonly TimeZoneInfo _timeZone;

    public BandwidthSchedule(TokenBucket limiter, Func<SpeedLimitSettings> settings, IClock clock, TimeZoneInfo? timeZone = null)
    {
        _limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    /// <summary>True when the last <see cref="Apply"/> found the window open.</summary>
    public bool IsWindowOpen { get; private set; }

    /// <summary>The rate currently in force, for the status bar. Null is unlimited.</summary>
    public long? EffectiveBytesPerSecond { get; private set; }

    /// <summary>
    /// Sets the limiter to whichever limit applies now. Called on the same timer that drives the
    /// queue scheduler, so the rate changes within a tick of the window opening or closing.
    /// </summary>
    public void Apply()
    {
        var settings = _settings();
        var local = TimeZoneInfo.ConvertTime(_clock.UtcNow, _timeZone).DateTime;

        IsWindowOpen = IsOpen(settings.Window, local);
        EffectiveBytesPerSecond = IsWindowOpen ? settings.Window.BytesPerSecond : settings.GlobalBytesPerSecond;
        _limiter.BytesPerSecond = EffectiveBytesPerSecond;
    }

    /// <summary>
    /// Whether <paramref name="window"/> covers <paramref name="localNow"/>. A window whose
    /// times cannot be parsed counts as closed, so a damaged setting leaves the normal limit in
    /// place instead of throttling everything unexpectedly.
    /// </summary>
    public static bool IsOpen(BandwidthWindowSettings window, DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.Enabled || !TimeOnly.TryParse(window.Start, null, out var start))
        {
            return false;
        }

        TimeOnly? stop = TimeOnly.TryParse(window.Stop, null, out var parsed) ? parsed : null;
        return ScheduleEvaluator.IsInWindow(
            new QueueSchedule
            {
                Enabled = true,
                Start = start,
                Stop = stop,
                Days = window.Days is { Count: > 0 } ? window.Days.ToHashSet() : QueueSchedule.AllDays,
            },
            localNow);
    }
}
