using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace WpfStudio.Runtime.Terminal;

/// <summary>How a pseudoconsole session starts, beyond its program and folder.</summary>
public sealed record ConPtyOptions
{
    public short Columns { get; init; } = 120;
    public short Rows { get; init; } = 30;
    /// <summary>Variables set over the IDE's own environment for the program; null inherits it unchanged.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    /// <summary>Sees every chunk of output on the reader thread before the renderer does. It must not keep the thread long or change the bytes.</summary>
    public Action<byte[]>? ObserveOutput { get; init; }
    /// <summary>
    /// The program never waits for a renderer: with none attached, output collects up to a limit and the oldest is then
    /// dropped. Suits a task that runs while its tab has never been shown, which would otherwise stop when its first
    /// few screens of output fill the channel.
    /// </summary>
    public bool KeepOutputFlowing { get; init; }
}

/// <summary>A real Windows pseudoconsole. Dedicated readers/writers avoid synchronous pipe deadlocks.</summary>
public sealed class ConPtySession : IAsyncDisposable
{
    private readonly Channel<byte[]> output;
    private Action<byte[]>? observeOutput;
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
    /// <summary>The program's exit code once it has ended; -1 when it could not be read, as after the session is disposed first.</summary>
    public Task<int> Exited => exited.Task;
    public ChannelReader<byte[]> Output => output.Reader;
    public event Action? Closed;
    public event Action<string>? Error;

    public ConPtySession(string executable, string arguments, string workingDirectory, short columns = 120, short rows = 30)
        : this(executable, arguments, workingDirectory, new ConPtyOptions { Columns = columns, Rows = rows }) { }

    public ConPtySession(string executable, string arguments, string workingDirectory, ConPtyOptions options)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) throw new PlatformNotSupportedException("The terminal requires Windows 10 1809 or newer.");
        output = options.KeepOutputFlowing
            ? Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8192) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true })
            : Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        observeOutput = options.ObserveOutput;
        var (columns, rows) = (options.Columns, options.Rows);
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
            ProcessInformation information;
            var block = options.Environment is { } variables ? Marshal.StringToHGlobalUni(EnvironmentBlock(variables)) : 0;
            try { Check(Native.CreateProcess(executable, command, 0, 0, false, 0x00080000 | 0x00000400, block, workingDirectory, ref startup, out information)); }
            finally { if (block != 0) Marshal.FreeHGlobal(block); }
            // Opening Handle while the creation handle is still open retains the OS process object, so the id cannot be
            // reused by an unrelated process before Kill(entireProcessTree) looks it up on close.
            try { process = Process.GetProcessById(information.ProcessId); _ = process.Handle; }
            finally { Native.CloseHandle(information.Process); Native.CloseHandle(information.Thread); }
            process.EnableRaisingEvents = true;
            process.Exited += OnProcessExited;
            if (process.HasExited) OnProcessExited(null, EventArgs.Empty);
            inputStream = new FileStream(writeInput, FileAccess.Write, 4096, false);
            outputStream = new FileStream(readOutput, FileAccess.Read, 16384, false);
            // The pipe only offers blocking reads, and this one lasts as long as the session, so it gets its own thread rather than holding a pool thread.
            reader = Task.Factory.StartNew(ReadOutput, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
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
    private void OnProcessExited(object? sender, EventArgs args)
    {
        try { exited.TrySetResult(process.ExitCode); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { exited.TrySetResult(-1); }
    }
    /// <summary>The IDE's environment with <paramref name="variables"/> set over it, sorted as Windows expects, in CreateProcess's block format.</summary>
    internal static string EnvironmentBlock(IReadOnlyDictionary<string, string> variables)
    {
        var merged = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables()) merged[(string)entry.Key] = (string?)entry.Value ?? "";
        foreach (var (name, value) in variables) { if (name.Length > 0 && !name.Contains('=', StringComparison.Ordinal)) merged[name] = value; }
        var block = new StringBuilder();
        foreach (var (name, value) in merged) block.Append(name).Append('=').Append(value).Append('\0');
        return block.Append('\0').ToString();
    }
    /// <summary>Ends the program and everything it started, but leaves the pseudoconsole open, so its output can still be read.</summary>
    public void Terminate()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
    }
    public ValueTask WriteAsync(string text, CancellationToken cancellationToken = default) => input.Writer.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
    public void Resize(int columns, int rows)
    {
        if (Volatile.Read(ref disposed) != 0 || pseudoConsole == 0) return;
        Marshal.ThrowExceptionForHR(Native.ResizePseudoConsole(pseudoConsole, new Coord((short)Math.Clamp(columns, 1, 500), (short)Math.Clamp(rows, 1, 300))));
    }
    private void ReadOutput()
    {
        var buffer = new byte[16384];
        try
        {
            int count;
            while ((count = outputStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (Volatile.Read(ref disposed) != 0) continue; // Drain the final ConPTY frame during teardown.
                var chunk = buffer.AsSpan(0, count).ToArray();
                // An observer that fails stops observing; the terminal itself keeps reading.
                try { observeOutput?.Invoke(chunk); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { observeOutput = null; Error?.Invoke("Reading the output stopped: " + ex.Message); }
#pragma warning disable VSTHRD002 // This thread exists to block, and waits on nothing that needs it; a full channel is what pauses reading.
                try { output.Writer.WriteAsync(chunk, lifetime.Token).AsTask().GetAwaiter().GetResult(); }
#pragma warning restore VSTHRD002
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
        if (!exited.Task.IsCompleted)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); OnProcessExited(null, EventArgs.Empty); }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or Win32Exception) { }
        }
        exited.TrySetResult(-1);
        process.Exited -= OnProcessExited;
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
