using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WpfStudio.Hosting;

/// <summary>Which process the operating system says is at the other end of a named pipe.</summary>
public static class NamedPipeIdentity
{
    public static bool TryGetServerProcessId(PipeStream pipe, out int processId)
    {
        var known = GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var id);
        processId = (int)id;
        return known;
    }

    public static bool TryGetClientProcessId(PipeStream pipe, out int processId)
    {
        var known = GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var id);
        processId = (int)id;
        return known;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}
