namespace OB2.App.Services;

/// <summary>
/// D-85: end-to-end tests of the updater need a release feed and a signing key of their own. A
/// build made with <c>-p:Ob2UpdateTest=true</c> reads them from <c>OB2_TEST_UPDATE_FEED</c> and
/// <c>OB2_TEST_UPDATE_KEY</c>. build/publish.ps1 never sets that property, so a release has
/// neither: it reads GitHub and trusts only <see cref="Core.Updates.ReleaseKey"/>.
/// </summary>
internal static class UpdateTesting
{
#if UPDATE_TEST
    public static Uri? Feed =>
        Uri.TryCreate(Environment.GetEnvironmentVariable("OB2_TEST_UPDATE_FEED"), UriKind.Absolute, out var feed) ? feed : null;

    public static IReadOnlyList<string>? Keys =>
        Environment.GetEnvironmentVariable("OB2_TEST_UPDATE_KEY") is { Length: > 0 } key ? [key] : null;
#else
    public static Uri? Feed => null;

    public static IReadOnlyList<string>? Keys => null;
#endif
}
