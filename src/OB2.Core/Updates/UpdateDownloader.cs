using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using OB2.Core.Downloads;
using OB2.Core.Engine;
using OB2.Core.Integrity;
using OB2.Core.Media;

namespace OB2.Core.Updates;

/// <summary>Progress of the update's download.</summary>
public sealed record UpdateDownloadProgress(long Received, long? Total, double BytesPerSecond);

/// <summary>An update downloaded, checked and unpacked into <paramref name="Folder"/>, ready for <see cref="UpdateInstaller"/>.</summary>
public sealed record PreparedUpdate(Version Version, string Folder);

/// <summary>
/// D-85: fetches a release and makes sure it is the developer's before anything is installed.
/// The small checksum file and its signature come first: a release OB2 would refuse (D-84) is
/// refused before its zip is downloaded. The zip comes through a private instance of OB2's
/// engine, like the video tools (D-71), so a slow line still finishes and a stopped download
/// continues next time; it must then match the signed checksum, unpack, and hold an OB2.exe of
/// the release's own version (else an updated copy would keep offering the same update).
/// </summary>
public sealed class UpdateDownloader : IDisposable
{
    private static readonly EngineOptions DownloadOptions = new()
    {
        Connections = 8,
        MaxRetries = 10,
        ReadIdleTimeout = TimeSpan.FromSeconds(90),
        UserAgent = "OB2-download-manager",
    };

    /// <summary>The checksum file and its signature are a few hundred bytes.</summary>
    private const int MaxSmallFileBytes = 64 * 1024;

    private readonly string _folder;
    private readonly IReadOnlyList<string> _trustedKeys;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly HttpClient _http;
    private readonly object _engineLock = new();
    private DownloadEngine? _engine;
    private ToolDownloadStore? _store;

    /// <param name="folder">Where the download and the unpacked update go: <c>&lt;data root&gt;\updates</c>.</param>
    /// <param name="trustedKeys">The keys a release must be signed with; <see cref="ReleaseKey.Trusted"/> unless a test gives its own.</param>
    public UpdateDownloader(string folder, IReadOnlyList<string>? trustedKeys = null, ILoggerFactory? loggerFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _folder = folder;
        _trustedKeys = trustedKeys ?? ReleaseKey.Trusted;
        _loggerFactory = loggerFactory;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(DownloadOptions.UserAgent);
    }

    public string Folder => _folder;

    /// <summary>False in a build without a release key: such a copy can only point to the release page.</summary>
    public bool HasTrustedKey => _trustedKeys.Count > 0;

    private string DownloadsFolder => Path.Combine(_folder, "downloads");

    /// <exception cref="UpdateException">The release is not the developer's, is damaged, or could not be fetched.</exception>
    /// <exception cref="OperationCanceledException">Stopped; the partial zip is kept for next time.</exception>
    public async Task<PreparedUpdate> DownloadAsync(UpdateRelease release, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!release.CanInstall)
        {
            throw new UpdateException("This release has no files OB2 can install. Download it from its page instead.");
        }

        if (_trustedKeys.Count == 0)
        {
            throw new UpdateException("This copy of OB2 has no release key to check an update with. Download it from its page instead.");
        }

        Directory.CreateDirectory(_folder);
        var sums = await GetSmallAsync(release.Checksums!, cancellationToken).ConfigureAwait(false);
        var signature = Encoding.ASCII.GetString(await GetSmallAsync(release.Signature!, cancellationToken).ConfigureAwait(false));
        if (!ReleaseSignature.Verify(sums, signature, _trustedKeys))
        {
            throw new UpdateException("This update is not signed by OB2's developer, so OB2 did not install it.");
        }

        var expected = Sha256Sums.Find(Encoding.ASCII.GetString(sums), UpdateChecker.ZipAsset)
            ?? throw new UpdateException("The update's checksum file does not list OB2-portable.zip, so OB2 did not install it.");

        var version = ReleaseVersion.Text(release.Version);
        var (id, zipPath) = await FetchAsync(release.Zip!, $"OB2-{version}.zip", progress, cancellationToken).ConfigureAwait(false);
        string actual;
        var stream = File.OpenRead(zipPath);
        await using (stream.ConfigureAwait(false))
        {
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        if (actual != expected)
        {
            await Engine.DeleteAsync(id, deleteFile: true).ConfigureAwait(false);
            throw new UpdateException("The downloaded update is damaged: it does not match its checksum. Try again.");
        }

        var staging = Path.Combine(_folder, version);
        DeleteFolder(staging);
        try
        {
            // Refuses an entry that would land outside the folder.
            ZipFile.ExtractToDirectory(zipPath, staging);
            var exe = Path.Combine(staging, UpdateInstaller.AppExe);
            if (!File.Exists(exe))
            {
                throw new UpdateException("The update has no OB2.exe, so OB2 did not install it.");
            }

            if (FileVersionInfo.GetVersionInfo(exe).ProductVersion is { Length: > 0 } product
                && ReleaseVersion.TryParse(product.Split('+')[0], out var inZip) && inZip != release.Version)
            {
                throw new UpdateException($"The update's OB2.exe is version {ReleaseVersion.Text(inZip)}, not {version}, so OB2 did not install it.");
            }
        }
        catch (Exception ex) when (ex is UpdateException or InvalidDataException or IOException)
        {
            DeleteFolder(staging);
            await Engine.DeleteAsync(id, deleteFile: true).ConfigureAwait(false);
            throw ex as UpdateException ?? new UpdateException("The update could not be unpacked. Try again.", ex);
        }

        await Engine.DeleteAsync(id, deleteFile: true).ConfigureAwait(false); // unpacked: the zip is done with
        return new PreparedUpdate(release.Version, staging);
    }

    public void Dispose()
    {
        _engine?.Dispose(); // pauses a running download; it continues next time
        _http.Dispose();
    }

    private DownloadEngine Engine
    {
        get
        {
            lock (_engineLock)
            {
                if (_engine is null)
                {
                    _store = new ToolDownloadStore(Path.Combine(DownloadsFolder, "state.json"));
                    _engine = new DownloadEngine(_store, () => DownloadOptions, loggerFactory: _loggerFactory);
                    _engine.RecoverInterrupted();
                }

                return _engine;
            }
        }
    }

    /// <summary>Downloads (or continues) the zip. A partial zip of an older release is dropped first.</summary>
    private async Task<(long Id, string Path)> FetchAsync(Uri url, string fileName, IProgress<UpdateDownloadProgress>? progress, CancellationToken ct)
    {
        var engine = Engine;
        foreach (var stale in _store!.List().Where(d => d.Url != url.AbsoluteUri).ToList())
        {
            await engine.DeleteAsync(stale.Id, deleteFile: true).ConfigureAwait(false);
        }

        var item = _store.List().FirstOrDefault(d => d.Url == url.AbsoluteUri);
        if (item is { Status: DownloadStatus.Completed } && !File.Exists(item.DestPath))
        {
            await engine.DeleteAsync(item.Id, deleteFile: false).ConfigureAwait(false);
            item = null;
        }

        item ??= await engine.AddAsync(new DownloadRequest { Url = url, SaveFolder = DownloadsFolder, FileName = fileName }, cancellationToken: ct).ConfigureAwait(false);
        if (item.Status != DownloadStatus.Completed)
        {
            engine.Start(item.Id);
            var id = item.Id;
            using (ct.Register(() => _ = engine.PauseAsync(id)))
            {
                while (engine.IsActive(id))
                {
                    if (engine.GetProgress(id) is { } p)
                    {
                        progress?.Report(new UpdateDownloadProgress(p.DownloadedBytes, p.TotalBytes, p.BytesPerSecond));
                    }

                    await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
                }
            }

            item = _store.Get(id)!;
            ct.ThrowIfCancellationRequested();
            if (item.Status != DownloadStatus.Completed)
            {
                throw new UpdateException($"The update could not be downloaded: {item.ErrorMessage ?? "the download stopped"}. Try again to continue where it stopped.");
            }
        }

        progress?.Report(new UpdateDownloadProgress(item.TotalBytes ?? 0, item.TotalBytes, 0));
        return (item.Id, item.DestPath);
    }

    private async Task<byte[]> GetSmallAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException($"The update's {Path.GetFileName(url.AbsolutePath)} could not be downloaded ({(int)response.StatusCode}). Try again later.");
            }

            if (response.Content.Headers.ContentLength > MaxSmallFileBytes)
            {
                throw new UpdateException($"The update's {Path.GetFileName(url.AbsolutePath)} is too large to be what OB2 expects, so OB2 did not install it.");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return bytes.Length <= MaxSmallFileBytes
                ? bytes
                : throw new UpdateException($"The update's {Path.GetFileName(url.AbsolutePath)} is too large to be what OB2 expects, so OB2 did not install it.");
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException("OB2 could not reach GitHub to download the update. Check the internet connection and try again.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException("GitHub did not answer in time. Try again later.", ex);
        }
    }

    private static void DeleteFolder(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
