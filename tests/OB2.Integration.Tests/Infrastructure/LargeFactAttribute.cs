namespace OB2.Integration.Tests.Infrastructure;

/// <summary>A test that moves gigabytes or runs for minutes; runs only with <c>OB2_RUN_LARGE=1</c>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LargeFactAttribute : FactAttribute
{
    public LargeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OB2_RUN_LARGE") != "1")
        {
            Skip = "Large test: set OB2_RUN_LARGE=1 to run.";
        }
    }
}
