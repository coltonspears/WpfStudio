using System.IO;
using System.Reflection;
using WpfStudio.PreviewDependencies;

namespace WpfStudio.PreviewHost;

// .NET Framework: this domain's application base is the copied output and its configuration file is the project's, so the
// runtime binds as it does for the application itself: the GAC, the output, and the project's binding redirects.
// These events only cover what that leaves out.
internal sealed partial class PreviewAssemblyResolver
{
    private void Subscribe() => AppDomain.CurrentDomain.AssemblyResolve += Resolve;

    private void Unsubscribe() => AppDomain.CurrentDomain.AssemblyResolve -= Resolve;

    /// <summary>The default domain copies the output before creating this domain, because the runtime reads its configuration file from there.</summary>
    private static bool IsAlreadyStaged(string directory) =>
        AppDomain.CurrentDomain.GetData(FrameworkPreviewDomain.StagedOutputKey) is string staged
        && string.Equals(staged, directory, StringComparison.OrdinalIgnoreCase);

    private Assembly LoadProjectAssembly(string shadowAssembly)
    {
        PrependNativeSearchPath();
        // By name, so it lands in the same context as everything else the runtime binds. A real run loads it this way too.
        try { return Assembly.Load(AssemblyName.GetAssemblyName(shadowAssembly)); }
        catch (FileNotFoundException) { return LoadFromPath(shadowAssembly); }
    }

    /// <summary>UnsafeLoadFrom because a vendor's library copied from a download keeps its mark of the web, and this is a build the user asked to preview.</summary>
    private static Assembly LoadFromPath(string path) => Assembly.UnsafeLoadFrom(path);

    private static XamlNamespaceLibrary? ReadLibraryMetadata(string path) => FrameworkEngineServer.ReadLibrary(path);

    // .NET Framework has no native-library event. The output is already searched as the assembly's own directory; the
    // process search path adds the HintPath folders vendors often keep their native parts in.
    private void PrependNativeSearchPath()
    {
        if (_searchDirectories.Count == 0) return;
        Environment.SetEnvironmentVariable("PATH", string.Join(";", _searchDirectories) + ";" + Environment.GetEnvironmentVariable("PATH"));
    }

    private Assembly? Resolve(object? sender, ResolveEventArgs arguments)
    {
        var name = new AssemblyName(arguments.Name);
        if (_directory is null || string.IsNullOrWhiteSpace(name.Name) || name.Name!.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        // A reference the project's configuration does not redirect still gets the version the build produced, each time it is asked for.
        string candidate = string.IsNullOrWhiteSpace(name.CultureName) ? Path.Combine(_directory, name.Name + ".dll") :
            Path.Combine(_directory, name.CultureName, name.Name + ".dll");
        if (File.Exists(candidate)) return LoadFromPath(candidate);
        candidate = Path.ChangeExtension(candidate, ".exe");
        if (File.Exists(candidate)) return LoadFromPath(candidate);
        if (_missingDependencies.TryGetValue(name.Name, out var missing))
            throw new FileNotFoundException($"Preview dependency '{name.Name}' is missing from the built output, its HintPath folder and the restored package cache. Restore and rebuild the selected project. Expected: {missing}", missing);
        if (StageFromSearchDirectories(name.Name + ".dll", name.CultureName) is { } staged) return LoadFromPath(staged);
        // Satellite and serializer probes are optional; anything else is reported if the render fails.
        if (string.IsNullOrEmpty(name.CultureName) && !name.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)
            && !name.Name.EndsWith(".XmlSerializers", StringComparison.OrdinalIgnoreCase))
            lock (_unresolved) _unresolved.Add(name.Name);
        return null;
    }
}
