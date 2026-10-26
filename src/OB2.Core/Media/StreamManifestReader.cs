using System.Text;
using OB2.Core.Http;
using OB2.Core.Settings;

namespace OB2.Core.Media;

/// <summary>
/// D-80: fetches and reads a stream's manifest the way the page's player did, with the browser's
/// headers and cookies (<see cref="StreamRequest"/>), so OB2 needs no yt-dlp for it.
/// </summary>
public sealed class StreamManifestReader
{
    /// <summary>Manifests are text; a film's DASH manifest is a few KB, a long HLS playlist a few hundred.</summary>
    private const int MaxManifestBytes = 16 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _userAgent;
    private readonly TimeSpan _timeout;

    public StreamManifestReader(HttpClient http, string? defaultUserAgent = null, TimeSpan? timeout = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _userAgent = defaultUserAgent ?? DownloadSettings.DefaultUserAgent;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// The stream's tracks. For an HLS master playlist the best quality's playlist is read too,
    /// for the video's length (hence the sizes) and to learn whether it is live or protected.
    /// </summary>
    /// <exception cref="MediaException">Not a stream, copy-protected, live, or nothing to download.</exception>
    /// <exception cref="HttpStatusException">The server refused the request.</exception>
    public async Task<StreamManifest> ReadAsync(StreamRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (text, url) = await GetTextAsync(request.Url, request, cancellationToken).ConfigureAwait(false);
        var manifest = HlsPlaylist.IsHls(text) ? HlsPlaylist.Parse(text, url)
            : DashManifest.IsDash(text) ? DashManifest.Parse(text, url)
            : throw new MediaException("The address is not an HLS or DASH stream.");
        if (manifest.Duration is null && manifest.Tracks.Where(t => t.PlaylistUrl is not null).MaxBy(t => t.Bandwidth ?? 0) is { } best)
        {
            var (playlist, playlistUrl) = await GetTextAsync(best.PlaylistUrl!, request, cancellationToken).ConfigureAwait(false);
            manifest = manifest with { Duration = HlsPlaylist.ParseMedia(playlist, playlistUrl).Duration };
        }

        return manifest with { Url = request.Url };
    }

    /// <summary>One track's pieces: listed in a DASH manifest, or read from the track's HLS playlist.</summary>
    public async Task<IReadOnlyList<StreamPiece>> PiecesAsync(StreamTrack track, StreamRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.Pieces is { } pieces)
        {
            return pieces;
        }

        var (text, url) = await GetTextAsync(track.PlaylistUrl!, request, cancellationToken).ConfigureAwait(false);
        return HlsPlaylist.ParseMedia(text, url).Pieces;
    }

    /// <summary>An HLS AES-128 key: 16 bytes from the address the playlist names.</summary>
    public async Task<byte[]> KeyAsync(Uri url, StreamRequest request, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var message = DownloadHttpClient.CreateRequest(HttpMethod.Get, url, request.Context(_userAgent));
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw HttpStatusException.From(response);
        }

        var key = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        return key.Length == 16 ? key : throw new MediaException("The stream's key is not a 16-byte AES key.");
    }

    /// <returns>The text and the address it came from after redirects, which relative addresses in it resolve against.</returns>
    private async Task<(string Text, Uri Url)> GetTextAsync(Uri url, StreamRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var message = DownloadHttpClient.CreateRequest(HttpMethod.Get, url, request.Context(_userAgent));
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw HttpStatusException.From(response);
        }

        if (response.Content.Headers.ContentLength > MaxManifestBytes)
        {
            throw new MediaException("The address is too large to be a stream's manifest.");
        }

        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxManifestBytes)
            {
                throw new MediaException("The address is too large to be a stream's manifest.");
            }
        }

        return (Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), response.RequestMessage?.RequestUri ?? url);
    }
}
