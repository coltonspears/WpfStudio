using System.Text.RegularExpressions;
using Microsoft.Build.Locator;

namespace WpfStudio.Workspace;

public static class ToolchainResolver
{
    /// <summary>The CLI resolves global.json including its roll-forward policy, exactly as a command-line build does.</summary>
    public static async Task<string> RegisterAsync(string workspacePath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(workspacePath))!;
        var version = await ProcessRunner.CaptureAsync(ProcessRunner.DotNet(directory, "--version"), cancellationToken);
        if (version.ExitCode != 0) throw new InvalidOperationException($"The SDK required by this workspace is unavailable. {version.Output} {version.Error}");
        var sdkVersion = version.Output.Trim();
        var installations = await ProcessRunner.CaptureAsync(ProcessRunner.DotNet(directory, "--list-sdks"), cancellationToken);
        foreach (var line in installations.Output.Split('\n'))
        {
            var match = Regex.Match(line.Trim(), @"^(\S+)\s+\[(.+)\]$");
            if (match.Success && match.Groups[1].Value == sdkVersion)
            {
                if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterMSBuildPath(Path.Combine(match.Groups[2].Value, sdkVersion));
                return sdkVersion;
            }
        }
        throw new InvalidOperationException($"Could not locate the resolved .NET SDK {sdkVersion}.");
    }
}
