using System.IO;
using System.Reflection;
using WpfStudio.Contracts;

namespace WpfStudio.PreviewHost;

/// <summary>Loads the previewed project from a private copy of its output. Each runtime supplies its own binding in a partial part.</summary>
internal sealed partial class PreviewAssemblyResolver : IDisposable
{
    private readonly string? _shadowDirectory;
    private readonly string? _shadowToken;
    private string? _directory;
    private string? _assemblyPath;
    private Assembly? _assembly;

    public PreviewAssemblyResolver(string? shadowDirectory, string? shadowToken)
    {
        _shadowDirectory = shadowDirectory;
        _shadowToken = shadowToken;
        Subscribe();
    }

    public Assembly? Load(PreviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AssemblyPath)) return null;
        string path = PreviewOutputStage.ValidateAssemblyPath(request);
        if (_assemblyPath is not null && !string.Equals(_assemblyPath, path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Restart the preview host before switching project assemblies.");
        if (_assembly is not null) return _assembly;
        _directory = PreviewOutputStage.OutputDirectory(path, _shadowDirectory, _shadowToken);
        if (!IsAlreadyStaged(_directory)) PreviewOutputStage.CopyOutputTree(Path.GetDirectoryName(path)!, _directory);
        StageDependencies(request);
        string shadowAssembly = Path.Combine(_directory, Path.GetFileName(path));
        _assembly = LoadProjectAssembly(shadowAssembly);
        _assemblyPath = path;
        return _assembly;
    }

    public void Dispose()
    {
        Unsubscribe();
        // The owning client removes only its verified temp directory after this
        // process exits and Windows releases mapped managed/native DLL files.
    }
}
