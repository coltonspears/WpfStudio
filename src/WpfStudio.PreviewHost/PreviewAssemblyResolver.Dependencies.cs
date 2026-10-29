using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using WpfStudio.Contracts;
using WpfStudio.PreviewDependencies;

namespace WpfStudio.PreviewHost;

internal sealed partial class PreviewAssemblyResolver
{
    private readonly Dictionary<string, string> _missingDependencies = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _searchDirectories = [];
    private readonly HashSet<string> _unresolved = new(StringComparer.OrdinalIgnoreCase);

    public void ForgetUnresolved() { lock (_unresolved) _unresolved.Clear(); }

    /// <summary>Assemblies requested since the last call that were in neither the output, the staged references nor a HintPath folder.</summary>
    public IReadOnlyList<string> UnresolvedAssemblies()
    {
        string[] names;
        lock (_unresolved) { names = [.. _unresolved.Order(StringComparer.OrdinalIgnoreCase)]; _unresolved.Clear(); }
        string searched = _searchDirectories.Count == 0 ? "the build output and restored packages"
            : $"the build output, restored packages and HintPath folders {string.Join(", ", _searchDirectories)}";
        return names.Select(name => $"Assembly '{name}' was not found in {searched}. Reference it from the XAML's project, or build the project that copies it.").ToArray();
    }

    private void StageDependencies(PreviewRequest request)
    {
        _searchDirectories = PreviewDependencyCatalog.SearchDirectories(request);
        foreach (var asset in PreviewDependencyCatalog.Read(request).Assets)
        {
            string destination = PreviewDependencyCatalog.Within(_directory!, asset.RelativePath);
            if (File.Exists(destination)) continue;
            if (!File.Exists(asset.Source))
            {
                _missingDependencies[Path.GetFileNameWithoutExtension(asset.RelativePath)] = asset.Source;
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(asset.Source, destination);
        }
    }

    /// <summary>
    /// Copies an assembly that is in a HintPath folder but not in the output into the shadow directory, the way
    /// MSBuild would find it beside its references. Returns the staged path, or null when no folder has it.
    /// </summary>
    private string? StageFromSearchDirectories(string fileName, string? culture = null)
    {
        if (_directory is null || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        string relative = string.IsNullOrWhiteSpace(culture) ? fileName : Path.Combine(culture, fileName);
        foreach (string directory in _searchDirectories)
        {
            string source = Path.Combine(directory, relative);
            if (!File.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) continue;
            string destination = PreviewDependencyCatalog.Within(_directory, relative.Replace('\\', '/'));
            if (!File.Exists(destination))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
            return destination;
        }
        return null;
    }

    /// <summary>
    /// Loads the libraries that declare the document's XAML namespaces (XmlnsDefinition), so the XAML reader can map
    /// URL namespaces such as <c>http://schemas.vendor.com/wpf</c> to types. Only assemblies the build placed or
    /// staged at the output root are considered, then HintPath folders, so design-time and per-platform copies
    /// in subfolders are never loaded. Returns diagnostics for namespaces no library declares and libraries
    /// that fail to load, instead of failing the whole preview.
    /// </summary>
    public IReadOnlyList<PreviewDiagnostic> PrepareXamlNamespaces(string text)
    {
        if (_directory is null) return [];
        var use = XamlNamespaceIndex.Read(text);
        if (use.Declared.Count == 0) return [];
        var diagnostics = new List<PreviewDiagnostic>();
        var defined = LoadedXamlNamespaces();
        var output = Directory.EnumerateFiles(_directory, "*.dll").Concat(Directory.EnumerateFiles(_directory, "*.exe"))
            .Order(StringComparer.OrdinalIgnoreCase).Select(ReadLibrary).OfType<XamlNamespaceLibrary>().ToArray();
        var selection = XamlNamespaceIndex.Select(use, output, defined);
        var libraries = selection.Libraries.ToList();
        if (selection.Unresolved.Count > 0 && _searchDirectories.Count > 0)
        {
            // MSBuild normally reports these as references; this covers projects it could not evaluate.
            var names = output.Select(library => library.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hinted = _searchDirectories.SelectMany(directory => Directory.EnumerateFiles(directory, "*.dll").Take(2_000))
                .Select(ReadLibrary).OfType<XamlNamespaceLibrary>().Where(library => !names.Contains(library.Name)).ToArray();
            var fallback = XamlNamespaceIndex.Select(new(selection.Unresolved.ToHashSet(StringComparer.Ordinal), selection.Unresolved.ToHashSet(StringComparer.Ordinal)), hinted, []);
            foreach (var library in fallback.Libraries)
                if (StageFromSearchDirectories(Path.GetFileName(library.Path)) is { } staged)
                {
                    libraries.Add(library with { Path = staged });
                    diagnostics.Add(new($"Loaded {library.Name} from HintPath folder {Path.GetDirectoryName(library.Path)} for xmlns \"{string.Join("\", \"", library.Namespaces)}\". MSBuild did not report it as a reference of this project.", "Information"));
                }
            selection = selection with { Unresolved = fallback.Unresolved };
        }
        foreach (var library in libraries)
        {
            try
            {
                if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly => string.Equals(assembly.GetName().Name, library.Name, StringComparison.OrdinalIgnoreCase))) continue;
                // Load by name first so the build's dependency manifest picks the copy a real run would use.
                try { Assembly.Load(AssemblyName.GetAssemblyName(library.Path)); }
                catch (FileNotFoundException) { AssemblyLoadContext.Default.LoadFromAssemblyPath(library.Path); }
            }
            catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                diagnostics.Add(new($"{library.Name} declares xmlns \"{library.Namespaces.FirstOrDefault()}\" but could not be loaded: {exception.Message}", "Warning"));
            }
        }
        foreach (var uri in selection.Unresolved)
            diagnostics.Add(new($"No referenced assembly declares xmlns \"{uri}\" with [XmlnsDefinition]. Build the project, and check that the library " +
                "is referenced by the XAML's project or one of its project references.", "Warning"));
        return diagnostics;
    }

    private readonly Dictionary<string, (DateTime Written, XamlNamespaceLibrary? Library)> _libraries = new(StringComparer.OrdinalIgnoreCase);
    private XamlNamespaceLibrary? ReadLibrary(string path)
    {
        var written = File.GetLastWriteTimeUtc(path);
        if (_libraries.TryGetValue(path, out var cached) && cached.Written == written) return cached.Library;
        var library = XamlNamespaceIndex.ReadLibrary(path);
        _libraries[path] = (written, library);
        return library;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Assembly, string[]> LoadedNamespaces = new();
    private static IEnumerable<string> LoadedXamlNamespaces() =>
        AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic).SelectMany(assembly => LoadedNamespaces.GetValue(assembly, static assembly =>
        {
            try
            {
                return assembly.GetCustomAttributesData()
                    .Where(attribute => attribute.AttributeType.FullName == "System.Windows.Markup.XmlnsDefinitionAttribute" && attribute.ConstructorArguments.Count > 0)
                    .Select(attribute => attribute.ConstructorArguments[0].Value as string).OfType<string>().ToArray();
            }
            catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or TypeLoadException) { return []; }
        }));
}
