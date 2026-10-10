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
    public const string CaptureRulesUpdated = "capture_rules_updated";
    public const string Error = "error";

    /// <summary>App-internal: a second <c>ob2.exe</c> asks the running one to show its window.</summary>
    public const string Activate = "activate";
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
}

public sealed record AckPayload(long? DownloadId, string Status);

public sealed record ErrorPayload(string Code, string Message);

public sealed record PongPayload(bool AppRunning, string HostVersion);

public sealed record DownloadSummary(long Id, string FileName, string Status, double? Progress, double BytesPerSecond, long? TotalBytes);

public sealed record CaptureRulesPayload(bool Enabled, long MinSizeBytes, IReadOnlyList<string> Extensions, IReadOnlyList<string> ExcludedDomains);

/// <summary><c>status</c> reply to <c>get_status</c>: active and recent downloads plus the capture rules.</summary>
public sealed record StatusPayload(IReadOnlyList<DownloadSummary> Active, IReadOnlyList<DownloadSummary> Recent, CaptureRulesPayload CaptureRules);
