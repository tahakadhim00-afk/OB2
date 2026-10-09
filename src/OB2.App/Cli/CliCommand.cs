using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using OB2.Core.Downloads;
using OB2.Core.Engine;
using OB2.Core.Http;
using OB2.Core.Integrity;
using OB2.Storage;
using OB2.Storage.Paths;
using OB2.Storage.Repositories;

namespace OB2.App.Cli;

/// <summary>
/// Headless test entry point (Phase 2):
/// <c>ob2.exe get &lt;url&gt; [--out &lt;dir&gt;] [--sha256 &lt;hex&gt; | --md5 &lt;hex&gt;] [--name &lt;file&gt;] [--connections &lt;n&gt;] [--data &lt;dir&gt;]</c>.
/// Running the same command again resumes an unfinished download of that URL into that folder.
/// </summary>
internal static partial class CliCommand
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitChecksumMismatch = 2;
    public const int ExitPaused = 3;
    public const int ExitUsage = 64;

    private const string Usage = """
        Usage: ob2.exe get <url> [--out <dir>] [--sha256 <hex> | --md5 <hex>] [--name <file>]
                               [--connections <1-32>] [--data <dir>]
        Run the same command again to resume an interrupted download.
        """;

    public static bool IsCli(string[] args) => args.Length > 0 && string.Equals(args[0], "get", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        AttachToParentConsole();
        try
        {
            return RunAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitFailed;
        }
        finally
        {
            Console.Out.Flush();
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (!CliArguments.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(Usage);
            return ExitUsage;
        }

        var paths = options.DataRoot is null ? AppPaths.Resolve(AppPathEnvironment.Current) : AppPaths.ForRoot(options.DataRoot);
        using var storage = await StorageBootstrapper.InitializeAsync(paths);
        var repository = new SqliteDownloadRepository(storage.ConnectionFactory);
        repository.MarkInterruptedAsPaused();

        var saveFolder = Path.GetFullPath(options.OutputFolder
            ?? storage.Settings.Folders.DefaultSaveFolder
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));

        using var engine = new DownloadEngine(repository, () => EngineOptions.FromSettings(storage.Settings), loggerFactory: storage.LoggerFactory);

        var item = FindResumable(repository, options.Url, saveFolder);
        if (item is null)
        {
            item = await engine.AddAsync(new DownloadRequest
            {
                Url = options.Url,
                SaveFolder = saveFolder,
                FileName = options.FileName,
                Connections = options.Connections,
                Checksum = options.Checksum,
                Source = DownloadSource.Cli,
            });
            Console.WriteLine($"Downloading {item.FileName} ({FormatSize(item.TotalBytes)}) to {saveFolder}");
        }
        else
        {
            if (options.Checksum is { } checksum)
            {
                item.ChecksumAlgo = checksum.AlgorithmName;
                item.ChecksumValue = checksum.HexValue;
                repository.Update(item);
            }

            Console.WriteLine($"Resuming {item.FileName} at {FormatSize(item.DownloadedBytes)} of {FormatSize(item.TotalBytes)}");
        }

        var id = item.Id;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("Pausing…");
            _ = engine.PauseAsync(id);
        };

        engine.Start(id);
        var wait = engine.WaitAsync(id);
        while (!wait.IsCompleted)
        {
            await Task.WhenAny(wait, Task.Delay(1000));
            if (engine.GetProgress(id) is { } progress)
            {
                var percent = progress.Fraction is { } f ? (f * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : FormatSize(progress.DownloadedBytes);
                Console.WriteLine($"{percent}  {FormatSize((long)progress.BytesPerSecond)}/s");
            }
        }

        var final = await wait ?? throw new InvalidOperationException("The download record disappeared.");
        switch (final.Status)
        {
            case DownloadStatus.Completed:
                Console.WriteLine($"Saved {final.DestPath} ({FormatSize(final.TotalBytes)})");
                return ExitOk;
            case DownloadStatus.Paused:
                Console.WriteLine("Paused. Run the same command again to resume.");
                return ExitPaused;
            default:
                Console.Error.WriteLine($"Failed [{final.ErrorCode}]: {final.ErrorMessage}");
                return final.ErrorCode == ErrorCodes.ChecksumMismatch ? ExitChecksumMismatch : ExitFailed;
        }
    }

    private static DownloadItem? FindResumable(SqliteDownloadRepository repository, Uri url, string saveFolder) =>
        repository.List()
            .Where(d => d.Status != DownloadStatus.Completed
                && string.Equals(d.Url, url.AbsoluteUri, StringComparison.Ordinal)
                && string.Equals(d.SaveFolder, saveFolder, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Id)
            .FirstOrDefault();

    internal static string FormatSize(long? bytes)
    {
        if (bytes is not { } b)
        {
            return "unknown size";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = b;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.##} {units[unit]}");
    }

    /// <summary>A GUI-subsystem exe has no console; borrow the caller's unless output is redirected.</summary>
    private static void AttachToParentConsole()
    {
        if (!Console.IsOutputRedirected)
        {
            AttachConsole(AttachParentProcess);
        }
    }

    private const int AttachParentProcess = -1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}

internal sealed record CliArguments(Uri Url, string? OutputFolder, Checksum? Checksum, string? FileName, int? Connections, string? DataRoot)
{
    public static bool TryParse(string[] args, out CliArguments options, out string error)
    {
        options = null!;
        error = string.Empty;
        string? url = null, output = null, name = null, data = null;
        Checksum? checksum = null;
        int? connections = null;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (url is not null)
                {
                    error = $"Unexpected argument '{arg}'.";
                    return false;
                }

                url = arg;
                continue;
            }

            if (i + 1 >= args.Length)
            {
                error = $"{arg} needs a value.";
                return false;
            }

            var value = args[++i];
            switch (arg.ToLowerInvariant())
            {
                case "--out":
                    output = value;
                    break;
                case "--name":
                    name = value;
                    break;
                case "--data":
                    data = value;
                    break;
                case "--sha256":
                case "--md5":
                    if (!Checksum.TryCreate(arg[2..], value, out checksum))
                    {
                        error = $"'{value}' is not a valid {arg[2..].ToUpperInvariant()} hex digest.";
                        return false;
                    }

                    break;
                case "--connections":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n is < 1 or > 32)
                    {
                        error = "--connections must be between 1 and 32.";
                        return false;
                    }

                    connections = n;
                    break;
                default:
                    error = $"Unknown option '{arg}'.";
                    return false;
            }
        }

        switch (UrlValidator.Check(url, out var parsed))
        {
            case UrlCheck.Ok:
            case UrlCheck.HasCredentials:
                options = new CliArguments(parsed!, output, checksum, name, connections, data);
                return true;
            case UrlCheck.UnsupportedScheme:
                error = "Only http and https URLs are supported.";
                return false;
            case UrlCheck.TooLong:
                error = $"The URL is longer than {UrlValidator.MaxLength} characters.";
                return false;
            default:
                error = url is null ? "Missing URL." : $"'{url}' is not a valid URL.";
                return false;
        }
    }
}
