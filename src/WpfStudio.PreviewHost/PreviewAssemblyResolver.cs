using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using WpfStudio.Contracts;

namespace WpfStudio.PreviewHost;

internal sealed partial class PreviewAssemblyResolver : IDisposable
{
    private readonly string? _shadowDirectory;
    private readonly string? _shadowToken;
    private string? _directory;
    private string? _assemblyPath;
    private Assembly? _assembly;
    private AssemblyDependencyResolver? _dependencies;
    public PreviewAssemblyResolver(string? shadowDirectory, string? shadowToken)
    {
        _shadowDirectory = shadowDirectory;
        _shadowToken = shadowToken;
        AssemblyLoadContext.Default.Resolving += Resolve;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveNative;
    }

    public Assembly? Load(PreviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AssemblyPath)) return null;
        if (string.IsNullOrWhiteSpace(request.ProjectDirectory)) throw new InvalidOperationException("A project directory is required to load a project assembly.");
        string root = Path.GetFullPath(request.ProjectDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(request.AssemblyPath);
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidOperationException("The preview assembly must be an existing DLL inside the selected local project directory.");
        if (_assemblyPath is not null && !string.Equals(_assemblyPath, path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Restart the preview host before switching project assemblies.");
        if (_assembly is not null) return _assembly;
        string shadowRoot = ValidateShadowDirectory();
        string sourceDirectory = Path.GetDirectoryName(path)!;
        if (shadowRoot.StartsWith(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The project output directory cannot contain the preview's temporary directory.");
        _directory = Path.Combine(shadowRoot, "output");
        CopyOutputTree(sourceDirectory, _directory);
        StageDependencies(request);
        string shadowAssembly = Path.Combine(_directory, Path.GetFileName(path));
        _dependencies = new AssemblyDependencyResolver(shadowAssembly);
        _assemblyPath = path;
        _assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(shadowAssembly);
        return _assembly;
    }

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

    private bool IsShadowPath(string path) => _directory is not null && !string.IsNullOrWhiteSpace(path) &&
        Path.GetFullPath(path).StartsWith(_directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private string ValidateShadowDirectory()
    {
        if (string.IsNullOrWhiteSpace(_shadowDirectory) || string.IsNullOrWhiteSpace(_shadowToken))
            throw new InvalidOperationException("Project assembly previews require a client-owned shadow directory.");
        string path = Path.GetFullPath(_shadowDirectory);
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(path), temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("WpfStudio.Preview.", StringComparison.Ordinal) ||
            !Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            File.ReadAllText(Path.Combine(path, "owner.token")) != _shadowToken)
            throw new InvalidOperationException("The preview shadow directory is not owned by this client.");
        return path;
    }

    private static void CopyOutputTree(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("A project output dependency is a filesystem link. Copy it into the build output before previewing.");
        Directory.CreateDirectory(destination);
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("A project output dependency is a filesystem link. Copy it into the build output before previewing.");
            string target = Path.Combine(destination, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0) CopyOutputTree(entry, target);
            else File.Copy(entry, target, overwrite: true);
        }
    }

    public void Dispose()
    {
        AssemblyLoadContext.Default.Resolving -= Resolve;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll -= ResolveNative;
        // The owning client removes only its verified temp directory after this
        // process exits and Windows releases mapped managed/native DLL files.
    }
}
