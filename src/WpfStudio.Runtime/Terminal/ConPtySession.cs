using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace WpfStudio.Runtime.Terminal;

/// <summary>A real Windows pseudoconsole. Dedicated readers/writers avoid synchronous pipe deadlocks.</summary>
public sealed class ConPtySession : IAsyncDisposable
{
    private readonly Channel<byte[]> output = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private readonly Channel<byte[]> input = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly CancellationTokenSource lifetime = new();
    private readonly FileStream inputStream;
    private readonly FileStream outputStream;
    private readonly Task reader;
    private readonly Task writer;
    private readonly Process process;
    private nint pseudoConsole;
    private int disposed;
    public int ProcessId => process.Id;
    public ChannelReader<byte[]> Output => output.Reader;
    public event Action? Closed;
    public event Action<string>? Error;

    public ConPtySession(string executable, string arguments, string workingDirectory, short columns = 120, short rows = 30)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) throw new PlatformNotSupportedException("The terminal requires Windows 10 1809 or newer.");
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException(workingDirectory);
        Check(Native.CreatePipe(out var readInput, out var writeInput, 0, 0));
        SafeFileHandle? readOutput = null, writeOutput = null;
        nint attributes = 0;
        bool attributesInitialized = false;
        try
        {
            Check(Native.CreatePipe(out readOutput, out writeOutput, 0, 0));
            Marshal.ThrowExceptionForHR(Native.CreatePseudoConsole(new Coord(columns, rows), readInput, writeOutput, 0, out pseudoConsole));
            nuint size = 0;
            Native.InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            Check(Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
            attributesInitialized = true;
            Check(Native.UpdateProcThreadAttribute(attributes, 0, 0x00020016, pseudoConsole, (nuint)nint.Size, 0, 0));
            // Explicit null standard handles prevent a redirected IDE/test-host console from being inherited instead of ConPTY.
            var startup = new StartupInfoEx { StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>(), dwFlags = 0x00000100 }, AttributeList = attributes };
            var command = new StringBuilder('"' + executable + '"' + (string.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments));
            Check(Native.CreateProcess(executable, command, 0, 0, false, 0x00080000 | 0x00000400, 0, workingDirectory, ref startup, out var information));
            try { process = Process.GetProcessById(information.ProcessId); }
            finally { Native.CloseHandle(information.Process); Native.CloseHandle(information.Thread); }
            inputStream = new FileStream(writeInput, FileAccess.Write, 4096, false);
            outputStream = new FileStream(readOutput, FileAccess.Read, 16384, false);
            reader = Task.Run(ReadOutputAsync);
            writer = Task.Run(WriteInputAsync);
        }
        catch
        {
            writeInput.Dispose(); readOutput?.Dispose();
            if (pseudoConsole != 0) { Native.ClosePseudoConsole(pseudoConsole); pseudoConsole = 0; }
            throw;
        }
        finally
        {
            readInput.Dispose(); writeOutput?.Dispose();
            if (attributesInitialized) Native.DeleteProcThreadAttributeList(attributes);
            if (attributes != 0) Marshal.FreeHGlobal(attributes);
        }
    }
    public ValueTask WriteAsync(string text, CancellationToken cancellationToken = default) => input.Writer.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
    public void Resize(int columns, int rows)
    {
        if (Volatile.Read(ref disposed) != 0 || pseudoConsole == 0) return;
        Marshal.ThrowExceptionForHR(Native.ResizePseudoConsole(pseudoConsole, new Coord((short)Math.Clamp(columns, 1, 500), (short)Math.Clamp(rows, 1, 300))));
    }
    private async Task ReadOutputAsync()
    {
        var buffer = new byte[16384];
        try
        {
            int count;
            while ((count = outputStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (Volatile.Read(ref disposed) != 0) continue; // Drain the final ConPTY frame during teardown.
                try { await output.Writer.WriteAsync(buffer.AsSpan(0, count).ToArray(), lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { if (Volatile.Read(ref disposed) == 0 && ex is not IOException) Error?.Invoke(ex.Message); }
        finally { output.Writer.TryComplete(); }
    }
    private async Task WriteInputAsync()
    {
        try
        {
            await foreach (var bytes in input.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            { inputStream.Write(bytes); inputStream.Flush(); }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { if (!lifetime.IsCancellationRequested) Error?.Invoke(ex.Message); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Closed?.Invoke();
        await lifetime.CancelAsync();
        input.Writer.TryComplete();
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
        var console = Interlocked.Exchange(ref pseudoConsole, 0);
        if (console != 0) await Task.Run(() => Native.ClosePseudoConsole(console)).ConfigureAwait(false);
        inputStream.Dispose();
        outputStream.Dispose();
        await Task.WhenAll(reader, writer).ConfigureAwait(false);
        process.Dispose(); lifetime.Dispose();
    }
    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Coord(short X, short Y);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int cb; public nint lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public nint AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public nint Process, Thread; public int ProcessId, ThreadId; }
    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, int size);
        [DllImport("kernel32.dll")] internal static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console);
        [DllImport("kernel32.dll")] internal static extern int ResizePseudoConsole(nint console, Coord size);
        [DllImport("kernel32.dll")] internal static extern void ClosePseudoConsole(nint console);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnSize);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(nint list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcess(string? application, StringBuilder command, nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(nint handle);
    }
}
