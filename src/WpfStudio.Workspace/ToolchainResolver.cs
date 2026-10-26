using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Build.Locator;

namespace WpfStudio.Workspace;

public static class ToolchainResolver
{
    // The worker resolves at startup and again when the workspace loads. MSBuildLocator can only register once per
    // process anyway, so one resolution per directory avoids repeating the CLI launches on every load.
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> Resolutions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The CLI resolves global.json including its roll-forward policy, exactly as a command-line build does.</summary>
    public static async Task<string> RegisterAsync(string workspacePath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(workspacePath))!;
        var resolution = Resolutions.GetOrAdd(directory, key => new(() => ResolveAsync(key)));
        try { return await resolution.Value.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch when (resolution.Value.IsFaulted)
        {
            // A missing SDK can be installed while the worker is alive; let the next load retry.
            Resolutions.TryRemove(new(directory, resolution));
            throw;
        }
    }

    private static async Task<string> ResolveAsync(string directory)
    {
        // Both commands are independent; each pays the CLI's startup cost, so run them side by side.
        // They are short-lived and shared by every caller, so no single caller's cancellation stops them.
        var versionTask = ProcessRunner.CaptureAsync(ProcessRunner.DotNet(directory, "--version"), CancellationToken.None);
        var installationsTask = ProcessRunner.CaptureAsync(ProcessRunner.DotNet(directory, "--list-sdks"), CancellationToken.None);
        await Task.WhenAll(versionTask, installationsTask).ConfigureAwait(false);
        var version = await versionTask.ConfigureAwait(false);
        if (version.ExitCode != 0) throw new InvalidOperationException($"The SDK required by this workspace is unavailable. {version.Output} {version.Error}");
        var sdkVersion = version.Output.Trim();
        var installations = await installationsTask.ConfigureAwait(false);
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
