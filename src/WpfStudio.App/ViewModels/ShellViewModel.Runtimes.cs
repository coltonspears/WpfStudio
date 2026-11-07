using Microsoft.Extensions.Logging;
using WpfStudio.App.Features.Runtimes;
using WpfStudio.Contracts;

namespace WpfStudio.App.ViewModels;

/// <summary>The shell as the runtime check's host: the banner that lists runtimes and SDKs the workspace needs and the machine lacks.</summary>
public sealed partial class ShellViewModel : IRuntimeCheckHost
{
    public RuntimeCheckViewModel Runtimes { get; }

    void IRuntimeCheckHost.OpenUrl(string url) => _desktop.OpenUrl(url);

    /// <summary>
    /// Checks in the background once a workspace has loaded, or failed to: a missing SDK is a likely reason for the
    /// failure. The check is advice, so its own failure is only logged.
    /// </summary>
    private void CheckRuntimes(string workspacePath)
    {
        if (_disposed) return;
        Runtimes.CheckAsync(workspacePath, Workspace?.Path.Equals(workspacePath, StringComparison.OrdinalIgnoreCase) == true ? Workspace.Projects : [])
            .Forget(fault => _logger.LogWarning(fault, "The runtime check failed"));
    }
}
