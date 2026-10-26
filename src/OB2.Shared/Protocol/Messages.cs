using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace OB2.Shared.Protocol;

/// <summary>Message type names (PRD §8.3). Changing these requires bumping <see cref="ProtocolInfo.Version"/>.</summary>
public static class MessageTypes
{
    public const string AddDownload = "add_download";
    public const string Ack = "ack";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string GetStatus = "get_status";
    public const string Status = "status";
    public const string PauseAll = "pause_all";
    public const string ResumeAll = "resume_all";
    public const string Error = "error";

    /// <summary>D-72: what a video page offers, for the quality menu on the browser's video button.</summary>
    public const string GetMediaFormats = "get_media_formats";
    public const string MediaFormats = "media_formats";

    /// <summary>App-internal: a second <c>ob2.exe</c> asks the running one to show its window.</summary>
    public const string Activate = "activate";

    /// <summary>
    /// How long the app may take to answer. Listing a video's formats runs yt-dlp, which takes
    /// seconds, and longer on a slow line (D-72); everything else is answered at once.
    /// </summary>
    public static TimeSpan ReplyTime(string? type) => type == GetMediaFormats ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(10);
}

/// <summary>Error codes (PRD §8.3) plus <see cref="InvalidMessage"/> (decision D-50).</summary>
public static class ProtocolErrors
{
    public const string AppNotRunning = "APP_NOT_RUNNING";
    public const string InvalidUrl = "INVALID_URL";
    public const string UnsupportedScheme = "UNSUPPORTED_SCHEME";
    public const string PipeTimeout = "PIPE_TIMEOUT";
    public const string ProtocolVersionMismatch = "PROTOCOL_VERSION_MISMATCH";
    public const string InternalError = "INTERNAL_ERROR";

    /// <summary>Malformed JSON, missing fields, or a frame above 1 MB.</summary>
    public const string InvalidMessage = "INVALID_MESSAGE";

    /// <summary>D-72: yt-dlp, ffmpeg and Deno are not installed yet; the Download Video window installs them.</summary>
    public const string MediaToolsMissing = "MEDIA_TOOLS_MISSING";

    /// <summary>D-72: yt-dlp could not list the page; the message is its reason, meant for the user.</summary>
    public const string MediaFailed = "MEDIA_FAILED";
}

/// <summary>The envelope every message uses: <c>{ v, id, type, payload }</c>.</summary>
public sealed record ProtocolMessage
{
    public int V { get; init; } = ProtocolInfo.Version;

    public string Id { get; init; } = Guid.NewGuid().ToString();

    public required string Type { get; init; }

    public JsonElement? Payload { get; init; }

    public T? PayloadAs<T>() =>
        Payload is { ValueKind: JsonValueKind.Object } payload ? payload.Deserialize(ProtocolJson.TypeInfo<T>()) : default;

    public static ProtocolMessage Create<T>(string type, T payload, string? id = null) => new()
    {
        Type = type,
        Id = id ?? Guid.NewGuid().ToString(),
        Payload = JsonSerializer.SerializeToElement(payload, ProtocolJson.TypeInfo<T>()),
    };

    public static ProtocolMessage Simple(string type, string? id = null) => new() { Type = type, Id = id ?? Guid.NewGuid().ToString() };

    public static ProtocolMessage ErrorReply(string id, string code, string message) =>
        Create(MessageTypes.Error, new ErrorPayload(code, message), id);
}

/// <summary>
/// JSON for the protocol uses source-generated metadata only, so host.exe can be trimmed
/// (decision D-68): camelCase names, nulls left out, names matched case-insensitively.
/// </summary>
public static class ProtocolJson
{
    public static JsonSerializerOptions Options => ProtocolJsonContext.Default.Options;

    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> is not listed in <see cref="ProtocolJsonContext"/>.</exception>
    public static JsonTypeInfo<T> TypeInfo<T>() =>
        ProtocolJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new InvalidOperationException($"{typeof(T).Name} is not a protocol type; add it to ProtocolJsonContext.");
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ProtocolMessage))]
[JsonSerializable(typeof(AddDownloadPayload))]
[JsonSerializable(typeof(AckPayload))]
[JsonSerializable(typeof(ErrorPayload))]
[JsonSerializable(typeof(PongPayload))]
[JsonSerializable(typeof(StatusPayload))]
[JsonSerializable(typeof(MediaFormatsRequest))]
[JsonSerializable(typeof(MediaFormatsPayload))]
[JsonSerializable(typeof(MediaSubtitlePayload))]
public sealed partial class ProtocolJsonContext : JsonSerializerContext;

/// <summary><c>add_download</c> payload, exactly as in §8.3.</summary>
public sealed record AddDownloadPayload
{
    public string? Url { get; init; }

    public string? FinalUrl { get; init; }

    public string? Referrer { get; init; }

    public string? FilenameHint { get; init; }

    public string? MimeType { get; init; }

    public long? FileSizeHint { get; init; }

    public string? UserAgent { get; init; }

    /// <summary>Cookie header value. Travels over the pipe only; never logged (§8.3).</summary>
    public string? Cookies { get; init; }

    public Dictionary<string, string>? Headers { get; init; }

    public string? Source { get; init; }

    public bool AutoStart { get; init; }

    /// <summary>
    /// The address is a video page for yt-dlp, not a file (D-71): the app opens the Download Video
    /// window to choose quality and audio. Optional; v stays 1 (an added field, like D-50).
    /// </summary>
    public bool? MediaPage { get; init; }

    /// <summary>
    /// D-72: with <see cref="MediaPage"/>, the yt-dlp format selector picked on the browser's
    /// button (<c>137+140</c>), from a <c>media_formats</c> reply. The app downloads it without
    /// asking again, unless confirmation is on. D-74: or a subtitle track's <c>format</c>
    /// (<c>subs:en</c>, <c>auto-subs:en-orig</c>).
    /// </summary>
    public string? MediaFormat { get; init; }

    /// <summary>D-72: the finished file's extension that goes with <see cref="MediaFormat"/> (D-74: srt, ass or vtt for subtitles).</summary>
    public string? MediaContainer { get; init; }
}

/// <summary><c>get_media_formats</c> payload (D-72): the video page whose qualities the button lists.</summary>
public sealed record MediaFormatsRequest
{
    public string? Url { get; init; }

    /// <summary>
    /// D-79: the HLS and DASH manifests the page's player loaded, oldest first (at most
    /// <see cref="ProtocolValidator.MaxStreams"/>). Tried when yt-dlp can't read the page itself.
    /// D-80: each with the request the browser made for it.
    /// </summary>
    public IReadOnlyList<MediaStreamPayload>? Streams { get; init; }

    /// <summary>D-79: the tab's title, for a video read from a stream, whose own name is "master" or "index".</summary>
    public string? Title { get; init; }

    /// <summary>D-80: the browser's user agent, which the streams were requested with.</summary>
    public string? UserAgent { get; init; }
}

/// <summary>
/// D-80: one stream of a <c>get_media_formats</c> request, as the browser requested it. Players'
/// servers check more than the address: movie-box.co's refuse anything without its token header,
/// <c>Origin</c> and <c>Referer</c>.
/// </summary>
public sealed record MediaStreamPayload
{
    public string? Url { get; init; }

    /// <summary>The <c>Referer</c> the browser sent for it.</summary>
    public string? Referrer { get; init; }

    /// <summary>The other request headers the browser sent (<c>Origin</c>, the player's own); never Cookie, Referer or User-Agent.</summary>
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>The Cookie header for the stream's address. Travels over the pipe only; never logged (§8.3).</summary>
    public string? Cookies { get; init; }
}

/// <summary>
/// <c>media_formats</c> reply (D-72): the page's title and the button's menu entries, best first.
/// D-74: <paramref name="Subtitles"/> lists the tracks OB2 can save, empty when the video has none.
/// D-79: <paramref name="Url"/> is the address the options belong to, the page or one of the
/// request's streams exactly as it was sent; <c>add_download</c> sends that one back.
/// </summary>
public sealed record MediaFormatsPayload(string Title, double? DurationSeconds, IReadOnlyList<MediaOptionPayload> Options, IReadOnlyList<MediaSubtitlePayload>? Subtitles = null, string? Url = null);

/// <summary>One quality: <paramref name="Format"/> and <paramref name="Container"/> go back in <c>add_download</c>.</summary>
public sealed record MediaOptionPayload(string Format, string Container, bool AudioOnly, string? Quality, string? Codec, string? Track, long? Size);

/// <summary>
/// D-74: one subtitle track. <paramref name="Format"/> and one of <paramref name="Containers"/>
/// go back in <c>add_download</c> as <c>mediaFormat</c> and <c>mediaContainer</c>.
/// </summary>
/// <param name="Automatic">Made by the site's speech recognition (YouTube's auto-generated captions).</param>
public sealed record MediaSubtitlePayload(string Format, string Language, string Name, bool Automatic, IReadOnlyList<string> Containers);

public sealed record AckPayload(long? DownloadId, string Status);

public sealed record ErrorPayload(string Code, string Message);

public sealed record PongPayload(bool AppRunning, string HostVersion);

public sealed record DownloadSummary(long Id, string FileName, string Status, double? Progress, double BytesPerSecond, long? TotalBytes);

public sealed record CaptureRulesPayload(bool Enabled, long MinSizeBytes, IReadOnlyList<string> Extensions, IReadOnlyList<string> ExcludedDomains);

/// <summary><c>status</c> reply to <c>get_status</c>: active and recent downloads plus the capture rules.</summary>
public sealed record StatusPayload(IReadOnlyList<DownloadSummary> Active, IReadOnlyList<DownloadSummary> Recent, CaptureRulesPayload CaptureRules);
