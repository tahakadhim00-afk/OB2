using System.Text;

namespace OB2.Core.IO;

/// <summary>
/// Writes files so that readers see either the complete old content or the complete new
/// content, never a partial write (PRD §11.3). Content goes to a temp file in the same
/// directory, is flushed to disk, and then replaces the target in a single rename.
/// </summary>
public static class AtomicFile
{
    private const string TempSuffix = ".tmp";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        return WriteAllBytesAsync(path, Utf8NoBom.GetBytes(contents), cancellationToken);
    }

    public static Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default) =>
        WriteAsync(path, (stream, ct) => stream.WriteAsync(contents, ct).AsTask(), cancellationToken);

    /// <summary>
    /// Streams content into <paramref name="path"/> atomically. If <paramref name="writeContent"/>
    /// throws, the target file is left untouched and the temp file is removed.
    /// </summary>
    public static async Task WriteAsync(string path, Func<Stream, CancellationToken, Task> writeContent, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(writeContent);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Path has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

        // Same directory as the target, so the final rename never crosses volumes.
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}{TempSuffix}");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, FileOptions.Asynchronous))
            {
                await writeContent(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            // MoveFileEx(MOVEFILE_REPLACE_EXISTING): atomic on the same NTFS volume.
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort; a stray temp file is harmless and has a recognisable name.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
