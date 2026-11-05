using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;

namespace System
{
    /// <summary>Members the host's shared code uses that only .NET has.</summary>
    internal static class HostCompatibility
    {
        extension(Environment)
        {
            public static int ProcessId
            {
                get { using var process = Process.GetCurrentProcess(); return process.Id; }
            }

            public static long TickCount64 => (long)GetTickCount64();
        }

        extension(OperatingSystem)
        {
            /// <summary>From the kernel, because Environment.OSVersion reports what this process's manifest declared support for.</summary>
            public static bool IsWindowsVersionAtLeast(int major, int minor, int build)
            {
                var version = new OsVersionInfo { Size = Marshal.SizeOf<OsVersionInfo>() };
                if (RtlGetVersion(ref version) != 0) return false;
                return new Version(version.Major, version.Minor, version.Build) >= new Version(major, minor, build);
            }
        }

        extension(Marshal)
        {
            public static int GetLastPInvokeError() => Marshal.GetLastWin32Error();
            /// <summary>Through a call that records the thread's error, the only way to change what GetLastWin32Error returns here.</summary>
            public static void SetLastPInvokeError(int error) => SetLastError(error);
        }

        extension(Type type)
        {
            public bool IsByRefLike => type.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.IsByRefLikeAttribute");
        }

        extension(SHA256)
        {
            public static byte[] HashData(byte[] data)
            {
                using var hash = SHA256.Create();
                return hash.ComputeHash(data);
            }

            public static byte[] HashData(Stream data)
            {
                using var hash = SHA256.Create();
                return hash.ComputeHash(data);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern void SetLastError(int error);
        [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
        [DllImport("ntdll.dll")] private static extern int RtlGetVersion(ref OsVersionInfo version);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OsVersionInfo
        {
            public int Size, Major, Minor, Build, PlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
        }
    }
}
