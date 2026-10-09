using OB2.Core.Time;

namespace OB2.Tests.Common;

/// <summary>A clock the test moves by hand (AC-10).</summary>
public sealed class FakeClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = start;

    public void Advance(TimeSpan by) => UtcNow += by;
}
