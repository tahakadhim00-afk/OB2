using System.Diagnostics;
using System.Text;

namespace OB2.Core.Media;

/// <summary>Runs a console tool and hands over its output line by line. Replaceable in tests.</summary>
public interface IProcessRunner
{
    /// <returns>The exit code.</returns>
    /// <exception cref="OperationCanceledException">Cancelled; the process and its children were killed.</exception>
    Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken);
}

public sealed class ProcessRunner : IProcessRunner
{
    public static ProcessRunner Instance { get; } = new();

    public async Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(onOutput);
        ArgumentNullException.ThrowIfNull(onError);
        cancellationToken.ThrowIfCancellationRequested();

        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(fileName) ?? string.Empty,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // For a yt-dlp run by Python. The standalone yt-dlp.exe ignores these, so yt-dlp is also
        // given --encoding utf-8 (MediaTools.CommonArguments).
        info.Environment["PYTHONUTF8"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onOutput(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onError(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Pausing: yt-dlp may be running ffmpeg, so the whole tree goes. Its .part files stay for resuming.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        process.WaitForExit(); // drains the asynchronous output readers
        return process.ExitCode;
    }
}
