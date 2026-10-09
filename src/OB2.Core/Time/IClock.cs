namespace OB2.Core.Time;

/// <summary>
/// Time source abstraction so scheduling and timestamps can be tested with a simulated clock (AC-10).
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
