using System.Reflection;

namespace WpfStudio.PreviewHost;

/// <summary>
/// The manager of the domain project code runs in. .NET Framework asks it for the entry assembly, which is how this host
/// presents the project as the application without running its Main, and without private reflection into WPF.
/// </summary>
public sealed class PreviewAppDomainManager : AppDomainManager
{
    private Assembly? _entryAssembly;

    /// <summary>Null until the project is chosen: WPF only accepts <c>Application.ResourceAssembly</c> while there is no entry assembly.</summary>
    public override Assembly? EntryAssembly => _entryAssembly;

    /// <summary>Runs in the new domain before any other code, so the engine's own first reference to a library the project also has is already covered.</summary>
    public override void InitializeNewDomain(AppDomainSetup appDomainInfo)
    {
        InitializationFlags = AppDomainManagerInitializationOptions.None;
        AppDomain.CurrentDomain.AssemblyResolve += (_, arguments) =>
            FrameworkPreviewDomain.PrivateHostAssembly(new AssemblyName(arguments.Name)) is { } path ? Assembly.LoadFrom(path) : null;
    }

    internal static void SetEntryAssembly(Assembly? assembly) =>
        ((PreviewAppDomainManager)AppDomain.CurrentDomain.DomainManager)._entryAssembly = assembly;
}
