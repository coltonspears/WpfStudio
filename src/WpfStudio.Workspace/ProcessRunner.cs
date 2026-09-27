using System.Diagnostics;

namespace WpfStudio.Workspace;

internal static class ProcessRunner
{
    public static ProcessStartInfo DotNet(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        ClearInheritedToolchain(start);
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    internal static void ClearInheritedToolchain(ProcessStartInfo start)
    {
        // IDEs and test runners can inherit their own SDK's paths. Those must not override the opened project's global.json.
        foreach (var name in new[] { "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildExtensionsPath32", "MSBuildExtensionsPath64", "MSBuildToolsPath", "MSBuildBinPath", "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR", "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER", "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR" }) start.Environment.Remove(name);
    }

    public static async Task<(int ExitCode, string Output, string Error)> CaptureAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {start.FileName}.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var registration = cancellationToken.Register(() => Kill(process));
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch { Kill(process); throw; }
    }

    internal static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
