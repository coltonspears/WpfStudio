using System.Diagnostics;
using System.IO;

namespace WpfStudio.App.Features.Packages;

public sealed record PackageSource(string Name, string Address)
{
    public override string ToString() => Name;
}

public sealed record InstalledPackage(string Id, string RequestedVersion, string ResolvedVersion, string Frameworks);
public sealed record PackageSearchItem(string Id, string Version, string Description, string Authors, long Downloads,
    string? ProjectUrl, IReadOnlyList<string> Versions)
{
    public string DownloadLabel => Downloads == 0 ? "" : $"{Downloads:N0} downloads";
}
public sealed record PackageInventory(IReadOnlyList<InstalledPackage> Packages, IReadOnlyList<PackageSource> Sources,
    IReadOnlyList<string> ConfigurationFiles, string? CentralVersionsFile, string? Warning);
public sealed record PackageProcessResult(int ExitCode, string Output);

public interface IPackageProcessRunner
{
    Task<PackageProcessResult> RunAsync(string directory, IReadOnlyList<string> arguments,
        IProgress<string>? output, CancellationToken cancellationToken);
}

public interface INuGetPackageService
{
    Task<PackageInventory> ReadProjectAsync(string projectPath, CancellationToken cancellationToken);
    Task<IReadOnlyList<PackageSearchItem>> SearchAsync(PackageSource source, string query, bool prerelease, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetVersionsAsync(PackageSource source, string packageId, bool prerelease, CancellationToken cancellationToken);
    Task<PackageProcessResult> ChangeAsync(string projectPath, string packageId, string? version, bool remove,
        IProgress<string>? output, CancellationToken cancellationToken);
}

/// <summary>Uses ArgumentList rather than a shell, and always respects the opened project's SDK selection.</summary>
public sealed class PackageProcessRunner : IPackageProcessRunner
{
    public static ProcessStartInfo CreateStartInfo(string directory, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var name in new[] { "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildExtensionsPath32",
                     "MSBuildExtensionsPath64", "MSBuildToolsPath", "MSBuildBinPath", "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR",
                     "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER", "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR" }) start.Environment.Remove(name);
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    public async Task<PackageProcessResult> RunAsync(string directory, IReadOnlyList<string> arguments,
        IProgress<string>? output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(CreateStartInfo(directory, arguments)) ?? throw new IOException("Unable to start dotnet. Install the SDK selected by global.json.");
        var captured = new System.Text.StringBuilder();
        var sync = new object();
        async Task PumpAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lock (sync)
                {
                    if (captured.Length < 2 * 1024 * 1024) captured.AppendLine(line.Length > 32768 ? line[..32768] : line);
                }
                output?.Report(line.Length > 32768 ? line[..32768] : line);
            }
        }
        using var registration = cancellationToken.Register(() => Kill(process));
        var readers = Task.WhenAll(PumpAsync(process.StandardOutput), PumpAsync(process.StandardError));
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await readers.ConfigureAwait(false);
            return new(process.ExitCode, captured.ToString());
        }
        catch
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await readers.ConfigureAwait(false); } catch (OperationCanceledException) { }
            throw;
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
