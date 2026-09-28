using System.IO;

namespace WpfStudio.Preview.Tests;

internal static class PreviewHostUnderTest
{
    /// <summary>Explicit package validation never falls back to a source-tree host.</summary>
    public static string ExecutablePath
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("WPFSTUDIO_PREVIEW_HOST_UNDER_TEST");
            string candidate = configured ?? Path.Combine(AppContext.BaseDirectory, "WpfStudio.PreviewHost.exe");
            if (!Path.IsPathFullyQualified(candidate) || !candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("WPFSTUDIO_PREVIEW_HOST_UNDER_TEST must name an absolute preview-host executable path.");
            string path = Path.GetFullPath(candidate);
            string directory = Path.GetDirectoryName(path)!;
            foreach (string required in new[] { path, Path.Combine(directory, "WpfStudio.PreviewHost.dll"),
                Path.Combine(directory, "WpfStudio.PreviewHost.runtimeconfig.json"), Path.Combine(directory, "WpfStudio.PreviewHost.deps.json"),
                Path.Combine(directory, "WpfStudio.Wpf.PropertyEditing.dll"), Path.Combine(directory, "WpfStudio.Wpf.Diagnostics.dll"),
                Path.Combine(directory, "WpfStudio.Inspection.Protocol.dll") })
                if (!File.Exists(required)) throw new FileNotFoundException("The explicitly selected preview host is incomplete; no fallback is allowed.", required);
            return path;
        }
    }
}
