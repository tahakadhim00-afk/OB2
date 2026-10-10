using System.Diagnostics;
using System.Text;

namespace OB2.Integration.Tests.Infrastructure;

/// <summary>Runs the real <c>OB2.exe</c> in CLI mode.</summary>
public static class Ob2Process
{
    public static string ExePath { get; } = FindExe();

    public static Process Start(params string[] args)
    {
        var info = new ProcessStartInfo(ExePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        return Process.Start(info) ?? throw new InvalidOperationException("OB2.exe did not start.");
    }

    public static async Task<(int ExitCode, string Output)> RunAsync(TimeSpan timeout, params string[] args)
    {
        using var process = Start(args);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"OB2.exe did not finish within {timeout}. Output so far: {await stdout}");
        }

        return (process.ExitCode, await stdout + await stderr);
    }

    private static string FindExe()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OB2.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new FileNotFoundException("Could not find the repository root (OB2.sln).");
        }

        var exe = Path.Combine(dir.FullName, "src", "OB2.App", "bin", configuration, "net10.0-windows", "OB2.exe");
        return File.Exists(exe) ? exe : throw new FileNotFoundException("Build OB2.App first.", exe);
    }
}
