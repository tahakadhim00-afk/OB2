using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OB2.Shared.Ipc;

/// <summary>
/// D-83: lets the app bring its window to the front for a request. Windows lets only the process
/// the user is working in, or one it started, take the foreground; any other process that tries
/// just flashes its taskbar button. Chrome starts a fresh host.exe for each message (through
/// cmd.exe, which passes the right on), and a second OB2.exe is started by whatever the user
/// clicked, so both may take the foreground, and AllowSetForegroundWindow hands that right to the
/// app at the other end of the pipe.
/// </summary>
public static partial class ForegroundHandoff
{
    /// <summary>
    /// Hands this process's right to take the foreground to the process serving
    /// <paramref name="pipe"/>. False when this process has no such right (Windows refuses), or
    /// off Windows; nothing else changes, and the app falls back to a window on top.
    /// </summary>
    public static bool AllowServer(NamedPipeClientStream pipe) =>
        ServerProcessId(pipe) is { } pid && AllowSetForegroundWindow((uint)pid);

    /// <summary>The id of the process serving <paramref name="pipe"/>, or null if Windows can't tell.</summary>
    public static int? ServerProcessId(NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!OperatingSystem.IsWindows() || !pipe.IsConnected)
        {
            return null;
        }

        return GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) ? (int)pid : null;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);
}
