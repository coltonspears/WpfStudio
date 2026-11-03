using System.ComponentModel;
using System.Diagnostics;

namespace WpfStudio.Hosting;

/// <summary>What every process the studio launches has in common, whatever it then talks over.</summary>
public static class ProcessLaunch
{
    /// <summary>Start info for a windowless child with redirected output. A managed .dll runs through the dotnet host, an executable directly.</summary>
    public static ProcessStartInfo Create(string program, string workingDirectory, IEnumerable<string> arguments, string? dotNetPath = null)
    {
        var managed = program.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(managed ? dotNetPath ?? "dotnet" : program)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (managed) start.ArgumentList.Add(program);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>Ends a process without throwing: callers run this inside their own cleanup, which must finish.</summary>
    public static void TryKill(Process process, bool entireProcessTree = false)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or AggregateException) { /* Already gone, or exiting on its own. */ }
    }
}
