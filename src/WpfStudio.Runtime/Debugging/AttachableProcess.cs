using System.Diagnostics;

namespace WpfStudio.Runtime.Debugging;

/// <summary>A running process the debugger can be attached to.</summary>
public sealed record AttachableProcess(int Id, string Name)
{
    /// <summary>Every running process but this one, by name and then ID.</summary>
    internal static AttachableProcess[] List()
    {
        var own = Environment.ProcessId;
        var processes = Process.GetProcesses();
        var list = new List<AttachableProcess>(processes.Length);
        foreach (var process in processes)
        {
            using (process)
            {
                try { if (process.Id != own && process.Id != 0) list.Add(new(process.Id, process.ProcessName)); }
                catch (InvalidOperationException) { } // It exited while the list was being read.
            }
        }
        return [.. list.OrderBy(process => process.Name, StringComparer.OrdinalIgnoreCase).ThenBy(process => process.Id)];
    }
}
