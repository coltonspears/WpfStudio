using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WpfStudio.RetentionFixture;

public sealed class RetainedPageModel
{
    public string Name = "Closed customer page";
    public int CustomerId = 42;
    public byte[] Payload = new byte[65_536];
    public byte[] Shared = Cache.Shared;
    public RetainedPageModel() { Publisher.Changed += OnChanged; }
    private void OnChanged(object? sender, EventArgs e) { CustomerId++; }
}
public static class Cache
{
    public static readonly List<RetainedPageModel> Pages = new List<RetainedPageModel>();
    public static readonly byte[] Shared = new byte[131_072];
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Grow() { Pages.Add(new RetainedPageModel()); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Clear() { Pages.Clear(); Publisher.Clear(); }
}
public static class Publisher
{
    public static event EventHandler? Changed;
    public static void Raise() { Changed?.Invoke(null, EventArgs.Empty); }
    public static void Clear() { Changed = null; }
}
internal static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--dump")
        {
            using var process = Process.GetProcessById(int.Parse(args[1]));
            using var file = File.Create(args[2]);
            if (!MiniDumpWriteDump(process.Handle, process.Id, file.SafeFileHandle, 0x2 | 0x4 | 0x800 | 0x1000, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return;
        }
        Cache.Grow(); Collect(); Console.WriteLine("READY");
        while (true)
        {
            var command = Console.ReadLine();
            if (command is null || command == "exit") return;
            if (command == "grow") Cache.Grow();
            if (command == "clear") Cache.Clear();
            Collect(); Console.WriteLine("DONE");
        }
    }
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    [DllImport("Dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(IntPtr process, int processId, SafeFileHandle file, int type,
        IntPtr exception, IntPtr userStream, IntPtr callback);
}
