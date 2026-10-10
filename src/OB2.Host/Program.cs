using System.Diagnostics;
using OB2.Shared;
using OB2.Shared.Ipc;
using OB2.Shared.Protocol;

// Chrome Native Messaging host (PRD §7.2, §8.2, §8.3).
// Chrome starts host.exe with the caller's origin as the first argument and talks over
// stdin/stdout using length-prefixed JSON. Each request is forwarded to the desktop app over
// the named pipe; the reply goes back to Chrome. Nothing here logs message contents
// (cookies travel only over the pipe, §8.3).

var baseDirectory = AppContext.BaseDirectory;
var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
var origin = args.FirstOrDefault(a => a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase));
if (origin is not null && !string.Equals(origin.TrimEnd('/'), $"chrome-extension://{ProtocolInfo.ExtensionId}", StringComparison.OrdinalIgnoreCase))
{
    // §11.1: Chrome enforces allowed_origins; this is a second check.
    return 2;
}

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();

while (true)
{
    ProtocolMessage? request;
    try
    {
        request = await MessageFraming.ReadAsync(stdin, CancellationToken.None);
    }
    catch (ProtocolException ex)
    {
        await MessageFraming.WriteAsync(stdout, ProtocolMessage.ErrorReply(string.Empty, ProtocolErrors.InvalidMessage, ex.Message), CancellationToken.None);
        return 1; // the stream is out of sync; Chrome reconnects with a fresh host
    }

    if (request is null)
    {
        return 0; // Chrome closed the port
    }

    var reply = ProtocolValidator.Validate(request) ?? await HostBridge.HandleAsync(request, baseDirectory, appData);
    await MessageFraming.WriteAsync(stdout, reply, CancellationToken.None);
}

internal static class HostBridge
{
    /// <summary>
    /// §8.3: the host answers within 2 seconds when the app is running. A running app always has
    /// a pipe instance waiting, so connecting takes milliseconds; the timeout only decides how long
    /// "not running" takes to detect (it is paid before every launch).
    /// </summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);

    /// <summary>§8.3: wait up to 15 seconds for a freshly started app.</summary>
    private static readonly TimeSpan LaunchWait = TimeSpan.FromSeconds(15);

    public static async Task<ProtocolMessage> HandleAsync(ProtocolMessage request, string baseDirectory, string appData)
    {
        var version = typeof(HostBridge).Assembly.GetName().Version?.ToString(3) ?? "0";
        var pipe = HostLocator.ReadPipeName(baseDirectory, appData);

        if (request.Type == MessageTypes.Ping)
        {
            var running = pipe is not null && await PipeClient.IsListeningAsync(pipe, ConnectTimeout);
            return ProtocolMessage.Create(MessageTypes.Pong, new PongPayload(running, version), request.Id);
        }

        if (pipe is not null)
        {
            try
            {
                return await PipeClient.SendAsync(pipe, request, ConnectTimeout, ReplyTimeout);
            }
            catch (TimeoutException) when (request.Type == MessageTypes.AddDownload)
            {
                // Not running: start it below.
            }
            catch (TimeoutException)
            {
                return ProtocolMessage.ErrorReply(request.Id, ProtocolErrors.AppNotRunning, "OB2 is not running.");
            }
            catch (Exception ex) when (ex is IOException or ProtocolException)
            {
                return ProtocolMessage.ErrorReply(request.Id, ProtocolErrors.InternalError, "The connection to OB2 failed.");
            }
        }
        else if (request.Type != MessageTypes.AddDownload)
        {
            return ProtocolMessage.ErrorReply(request.Id, ProtocolErrors.AppNotRunning, "OB2 has not been started on this PC yet.");
        }

        return await LaunchAndSendAsync(request, baseDirectory, appData);
    }

    /// <summary>§8.3 / AC-6: start OB2 with <c>--from-host</c> and hold the message until its pipe is up.</summary>
    private static async Task<ProtocolMessage> LaunchAndSendAsync(ProtocolMessage request, string baseDirectory, string appData)
    {
        var exe = HostLocator.AppExePath(baseDirectory);
        if (!File.Exists(exe))
        {
            return ProtocolMessage.ErrorReply(request.Id, ProtocolErrors.AppNotRunning, "OB2.exe was not found next to host.exe.");
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe, "--from-host") { UseShellExecute = false, WorkingDirectory = baseDirectory })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return ProtocolMessage.ErrorReply(request.Id, ProtocolErrors.AppNotRunning, "OB2 could not be started.");
        }

        var deadline = DateTime.UtcNow + LaunchWait;
        while (DateTime.UtcNow < deadline)
        {
            // The app writes the pipe name on its first start, so read it again until it appears.
            var pipe = HostLocator.ReadPipeName(baseDirectory, appData);
            if (pipe is null)
            {
                await Task.Delay(100);
                continue;
            }

            try
            {
                // Connecting waits for the pipe to be created, so the message goes the moment the app listens.
                return await PipeClient.SendAsync(pipe, request, deadline - DateTime.UtcNow, ReplyTimeout);
            }
            catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException)
            {
                // The instance closed under us, or the deadline passed between the check and the call.
                await Task.Delay(50);
            }
            catch (TimeoutException)
            {
                break;
            }
        }

        return ProtocolMessage.ErrorReply(request.Id, ProtocolErrors.PipeTimeout, "OB2 did not start within 15 seconds.");
    }
}
