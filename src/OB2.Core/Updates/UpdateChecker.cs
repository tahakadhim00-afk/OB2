using System.Net;
using System.Text.Json;

namespace OB2.Core.Updates;

/// <summary>
/// D-85: asks GitHub for OB2's latest release. The developer publishes a version by creating a
/// GitHub release (build/publish.ps1 -GitHub) with three files: the portable zip, its checksum
/// file, and the signature (D-84). Drafts and pre-releases are never "latest", so a version can
/// be tried as a pre-release before anyone is offered it. The request carries nothing but OB2's
/// version in the User-Agent, which GitHub requires.
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>The repository releases come from.</summary>
    public static readonly Uri GitHubLatestRelease = new("https://api.github.com/repos/tahakadhim00-afk/OB2/releases/latest");

    public const string ZipAsset = "OB2-portable.zip";
    public const string ChecksumsAsset = "SHA256SUMS.txt";
    public const string SignatureAsset = ChecksumsAsset + ReleaseSignature.FileSuffix;

    /// <summary>A release description is a few KB; this only stops a runaway answer.</summary>
    private const int MaxAnswerBytes = 2 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly Uri _latestRelease;

    public UpdateChecker(HttpClient http, Uri? latestRelease = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _latestRelease = latestRelease ?? GitHubLatestRelease;
    }

    /// <returns>The latest release if it is newer than <paramref name="current"/>; null if this copy is up to date.</returns>
    /// <exception cref="UpdateException">GitHub could not be reached or gave an answer that is not a release.</exception>
    public async Task<UpdateRelease?> CheckAsync(Version current, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        using var request = new HttpRequestMessage(HttpMethod.Get, _latestRelease);
        request.Headers.UserAgent.ParseAdd($"OB2/{ReleaseVersion.Text(current)}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null; // no release published yet
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                throw new UpdateException("GitHub is limiting requests from this connection. Try again in an hour.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}. Try again later.");
            }

            var body = await ReadLimitedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            return Parse(body, current);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException("OB2 could not reach GitHub. Check the internet connection and try again.", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("GitHub did not answer in time. Try again later.", ex);
        }
    }

    /// <summary>Reads GitHub's release JSON; null unless it is a full release newer than <paramref name="current"/>.</summary>
    /// <exception cref="UpdateException">The answer is not a release.</exception>
    public static UpdateRelease? Parse(ReadOnlySpan<byte> json, Version current)
    {
        ArgumentNullException.ThrowIfNull(current);
        try
        {
            var reader = new Utf8JsonReader(json);
            using var document = JsonDocument.ParseValue(ref reader);
            var release = document.RootElement;
            if (Bool(release, "draft") || Bool(release, "prerelease"))
            {
                return null;
            }

            if (!ReleaseVersion.TryParse(String(release, "tag_name"), out var version) || version <= current)
            {
                return null;
            }

            Uri? zip = null, sums = null, signature = null;
            long? zipSize = null;
            if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = String(asset, "name");
                    var url = Url(String(asset, "browser_download_url"));
                    switch (name)
                    {
                        case ZipAsset:
                            zip = url;
                            zipSize = asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) && bytes > 0 ? bytes : null;
                            break;
                        case ChecksumsAsset:
                            sums = url;
                            break;
                        case SignatureAsset:
                            signature = url;
                            break;
                    }
                }
            }

            var page = Url(String(release, "html_url")) ?? new Uri("https://github.com/tahakadhim00-afk/OB2/releases");
            var title = String(release, "name") is { Length: > 0 } name1 ? name1 : $"OB2 {ReleaseVersion.Text(version)}";
            return new UpdateRelease(version, title, String(release, "body") ?? string.Empty, page, zip, sums, signature, zipSize);
        }
        catch (JsonException ex)
        {
            throw new UpdateException("GitHub's answer about the latest release could not be read. Try again later.", ex);
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxAnswerBytes)
                {
                    throw new UpdateException("GitHub's answer about the latest release was too large to be one. Try again later.");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static Uri? Url(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp) ? url : null;
}
