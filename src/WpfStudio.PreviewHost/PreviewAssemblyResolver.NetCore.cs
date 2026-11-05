using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using WpfStudio.PreviewDependencies;

namespace WpfStudio.PreviewHost;

// .NET: project code shares the default load context with this host, which falls back to the copied output through its events.
internal sealed partial class PreviewAssemblyResolver
{
    private AssemblyDependencyResolver? _dependencies;

    private void Subscribe()
    {
        AssemblyLoadContext.Default.Resolving += Resolve;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveNative;
    }

    private void Unsubscribe()
    {
        AssemblyLoadContext.Default.Resolving -= Resolve;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll -= ResolveNative;
    }

    private static bool IsAlreadyStaged(string directory) => false;

    private Assembly LoadProjectAssembly(string shadowAssembly)
    {
        _dependencies = new AssemblyDependencyResolver(shadowAssembly);
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(shadowAssembly);
    }

    private static Assembly LoadFromPath(string path) => AssemblyLoadContext.Default.LoadFromAssemblyPath(path);

    private static XamlNamespaceLibrary? ReadLibraryMetadata(string path) => XamlNamespaceLibraryReader.Read(path);

    private bool IsShadowPath(string path) => _directory is not null && !string.IsNullOrWhiteSpace(path) &&
        Path.GetFullPath(path).StartsWith(_directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        if (_directory is null || string.IsNullOrWhiteSpace(name.Name) || name.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        string? candidate = _dependencies?.ResolveAssemblyToPath(name);
        if (candidate is not null && IsShadowPath(candidate) && File.Exists(candidate)) return context.LoadFromAssemblyPath(candidate);
        candidate = string.IsNullOrWhiteSpace(name.CultureName) ? Path.Combine(_directory, name.Name + ".dll") :
            Path.Combine(_directory, name.CultureName, name.Name + ".dll");
        if (IsShadowPath(candidate) && File.Exists(candidate)) return context.LoadFromAssemblyPath(candidate);
        if (_missingDependencies.TryGetValue(name.Name, out var missing))
            throw new FileNotFoundException($"Preview dependency '{name.Name}' is missing from the built output, its HintPath folder and the restored package cache. Restore and rebuild the selected project. Expected: {missing}", missing);
        if (StageFromSearchDirectories(name.Name + ".dll", name.CultureName) is { } staged) return context.LoadFromAssemblyPath(staged);
        // Satellite and serializer probes are optional; anything else is reported if the render fails.
        if (string.IsNullOrEmpty(name.CultureName) && !name.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)
            && !name.Name.EndsWith(".XmlSerializers", StringComparison.OrdinalIgnoreCase))
            lock (_unresolved) _unresolved.Add(name.Name);
        return null;
    }

    private IntPtr ResolveNative(Assembly assembly, string name)
    {
        if (!IsShadowPath(assembly.Location)) return IntPtr.Zero;
        string? path = _dependencies?.ResolveUnmanagedDllToPath(name);
        if (path is not null && IsShadowPath(path) && File.Exists(path)) return NativeLibrary.Load(path);
        // Vendor libraries often keep their native parts beside the managed DLL in the HintPath folder.
        foreach (string file in name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? [name] : new[] { name + ".dll", name })
            if (StageFromSearchDirectories(file) is { } staged) return NativeLibrary.Load(staged);
        return IntPtr.Zero;
    }
}
