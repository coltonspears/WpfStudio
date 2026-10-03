using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WpfStudio.Profiling;

internal static class ProcessArchitecture
{
    public static Architecture Get(Process process)
    {
        if (!OperatingSystem.IsWindows()) return RuntimeInformation.ProcessArchitecture;
        if (!IsWow64Process2(process.Handle, out var processMachine, out var nativeMachine))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return (processMachine == 0 ? nativeMachine : processMachine) switch
        {
            0x014c => Architecture.X86,
            0x8664 => Architecture.X64,
            0xAA64 => Architecture.Arm64,
            _ => throw new PlatformNotSupportedException("This process architecture is not supported.")
        };
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
}
