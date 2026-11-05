using WpfStudio.Contracts;

namespace WpfStudio.PreviewHost;

public static class PreviewHostSessions
{
    /// <summary>Native interaction needs the launching editor's identity, and per-monitor awareness that matches its windows.</summary>
    public static PreviewHostSession Create(string? sessionId, int parentProcessId) =>
        new(sessionId ?? Guid.NewGuid().ToString("N"), Environment.ProcessId, parentProcessId, PreviewNativeNavigation.ProtocolVersion,
            Guid.TryParseExact(sessionId, "N", out _) && parentProcessId > 0 && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063));
}
