using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OB2.Core.Files;

/// <summary>Creation and pre-allocation of <c>.pgpart</c> files (§10.3).</summary>
public static partial class PartFile
{
    private const uint FsctlSetSparse = 0x000900C4;

    /// <summary>
    /// Creates (or truncates) the partial file. With <paramref name="sparse"/> the file is
    /// marked sparse, so writes far into it do not force NTFS to zero-fill the gap first.
    /// </summary>
    public static void Create(string path, bool sparse)
    {
        using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        if (sparse)
        {
            SetSparse(handle, true);
        }
    }

    /// <summary>
    /// Sets the file to its final length. <c>SetFileValidData</c> needs an admin-only
    /// privilege the portable app never has, so <see cref="FileStream.SetLength"/> is used
    /// (decision D-19).
    /// </summary>
    public static void Preallocate(FileStream file, long length)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length != length)
        {
            file.SetLength(length);
        }
    }

    /// <summary>Clears the sparse flag once every byte is written, so the finished file is ordinary.</summary>
    public static void TryClearSparse(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            SetSparse(handle, false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <returns>False when the file system has no sparse support (FAT32, exFAT) or not on Windows.</returns>
    private static bool SetSparse(SafeFileHandle handle, bool sparse)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        // FILE_SET_SPARSE_BUFFER is a single BOOLEAN.
        byte flag = sparse ? (byte)1 : (byte)0;
        return DeviceIoControl(handle, FsctlSetSparse, ref flag, 1, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        ref byte inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    /// <summary>True if Windows reports the file as sparse (used by tests).</summary>
    public static bool IsSparse(string path) =>
        (File.GetAttributes(path) & FileAttributes.SparseFile) == FileAttributes.SparseFile;
}
