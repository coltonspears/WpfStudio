using System.IO;
using System.Reflection;
using WpfStudio.Contracts;

namespace WpfStudio.PreviewHost;

/// <summary>
/// Creates the domain project code runs in, set up the way the project's own process would be: based in its output, with
/// its configuration file and target framework. That is what gives it its binding redirects, the GAC, its relative file
/// lookups and its compatibility switches. The transport stays in the default domain, so the many libraries it depends
/// on can never collide with the project's own copies of them.
/// </summary>
public static class FrameworkPreviewDomain
{
    internal const string StagedOutputKey = "WpfStudio.Preview.StagedOutput";
    /// <summary>Under the application base, the only place the runtime probes, and out of the output's root listing.</summary>
    private const string HostFolder = ".wpfstudio-preview";
    private static readonly string[] HostFiles = ["WpfStudio.PreviewHost.dll", "WpfStudio.Contracts.dll", "WpfStudio.Inspection.Protocol.dll",
        "WpfStudio.Wpf.PropertyEditing.dll", "WpfStudio.Wpf.Diagnostics.dll", "Newtonsoft.Json.dll"];

    /// <param name="readTargetFramework">Reads an assembly's target framework moniker without loading it.</param>
    public static FrameworkEngineServer Create(PreviewRequest? request, string? shadowDirectory, string? shadowToken, string? sessionId, int parentProcessId,
        FrameworkHostServices services, Func<string, string?> readTargetFramework)
    {
        var (applicationBase, staged, assembly) = Stage(request, shadowDirectory, shadowToken);
        string? configuration = assembly is null ? null : assembly + ".config";
        var setup = new AppDomainSetup
        {
            ApplicationName = "WpfStudio.Preview",
            ApplicationBase = applicationBase,
            PrivateBinPath = HostFolder,
            ConfigurationFile = configuration is not null && File.Exists(configuration) ? configuration : null,
            TargetFrameworkName = assembly is null ? null : readTargetFramework(assembly),
            AppDomainManagerAssembly = typeof(PreviewAppDomainManager).Assembly.FullName,
            AppDomainManagerType = typeof(PreviewAppDomainManager).FullName,
        };
        var domain = AppDomain.CreateDomain("WpfStudio Preview", null, setup);
        domain.SetData(StagedOutputKey, staged);
        var server = (FrameworkEngineServer)domain.CreateInstanceAndUnwrap(typeof(FrameworkEngineServer).Assembly.FullName!, typeof(FrameworkEngineServer).FullName!);
        server.Start(shadowDirectory, shadowToken, sessionId, parentProcessId, services);
        return server;
    }

    /// <summary>
    /// Copies the project's output and this host's private files. A request it cannot stage still gets a domain: the engine
    /// repeats these checks inside its render, where the failure becomes a diagnostic like any other.
    /// </summary>
    private static (string ApplicationBase, string? Staged, string? Assembly) Stage(PreviewRequest? request, string? shadowDirectory, string? shadowToken)
    {
        string applicationBase;
        try { applicationBase = Path.Combine(PreviewOutputStage.ValidateShadowDirectory(shadowDirectory, shadowToken), "output"); }
        // Without its own temporary directory this host loads no project code, so its own folder is safe to run from.
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException) { return (AppDomain.CurrentDomain.BaseDirectory, null, null); }
        string? staged = null, assembly = null;
        Directory.CreateDirectory(applicationBase);
        if (!string.IsNullOrWhiteSpace(request?.AssemblyPath))
            try
            {
                string path = PreviewOutputStage.ValidateAssemblyPath(request!);
                string output = PreviewOutputStage.OutputDirectory(path, shadowDirectory, shadowToken);
                PreviewOutputStage.CopyOutputTree(Path.GetDirectoryName(path)!, output);
                PreviewOutputStage.StageReferences(request!, output);
                (staged, assembly) = (output, Path.Combine(output, Path.GetFileName(path)));
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException) { /* Reported by the engine. */ }
        string host = Path.Combine(applicationBase, HostFolder);
        Directory.CreateDirectory(host);
        foreach (string file in HostFiles)
            File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, file), Path.Combine(host, file), overwrite: true);
        return (applicationBase, staged, assembly);
    }

    /// <summary>
    /// This host's own copy of a library the project also has at another version. The runtime stops at the project's file
    /// and reports a mismatch, so an exact match is answered from here, side by side with the project's.
    /// </summary>
    internal static string? PrivateHostAssembly(AssemblyName name)
    {
        string candidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, HostFolder, name.Name + ".dll");
        if (!File.Exists(candidate)) return null;
        try
        {
            var definition = AssemblyName.GetAssemblyName(candidate);
            return AssemblyName.ReferenceMatchesDefinition(name, definition) && name.Version == definition.Version ? candidate : null;
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException) { return null; }
    }
}
