using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using WpfStudio.Contracts;
using WpfStudio.PreviewDependencies;

namespace WpfStudio.Runtime.Tests;

public sealed class PreviewDependencyCatalogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("WpfStudio-DependencyCatalog-").FullName;

    [Fact]
    public void RuntimeAssetsUseExactPackageFolderAndCurrentArchitectureWithCulturePreserved()
    {
        var request = Fixture();
        var catalog = PreviewDependencyCatalog.Read(request);
        Assert.Contains(catalog.Assets, asset => asset.Source == Path.Combine(_root, "cache", "package", "1.2.3", "runtimes", "win-x64", "lib", "net10.0", "Library.dll"));
        Assert.DoesNotContain(catalog.Assets, asset => asset.Source.Contains("linux", StringComparison.Ordinal));
        Assert.DoesNotContain(catalog.Assets, asset => asset.Source.Contains("ref/", StringComparison.Ordinal));
        Assert.Contains(catalog.Assets, asset => asset.RelativePath.Replace('\\', '/') == "fr/Library.resources.dll");
        Assert.Contains(catalog.Assets, asset => asset.Native && asset.RelativePath == "runtimes/win-x64/native/native.dll");
    }

    [Fact]
    public void BuiltCopyLocalLibraryWinsAndMissingFileArrivalChangesTheFingerprint()
    {
        var request = Fixture();
        string built = Path.Combine(_root, "bin", "Library.dll");
        File.WriteAllText(built, "built override");
        var before = PreviewDependencyCatalog.Read(request);
        string fingerprint = before.Fingerprint();
        Assert.Contains(before.Assets, asset => asset.Source == built);
        string missing = before.Assets.Single(asset => asset.Native).Source;
        Directory.CreateDirectory(Path.GetDirectoryName(missing)!);
        File.WriteAllText(missing, "restored");
        Assert.NotEqual(fingerprint, PreviewDependencyCatalog.Read(request).Fingerprint());
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("/absolute.dll")]
    [InlineData("C:/absolute.dll")]
    public void ManifestPathsCannotEscapeTheirDeclaredPackage(string path)
    {
        var request = Fixture(path);
        Assert.Throws<InvalidDataException>(() => PreviewDependencyCatalog.Read(request));
    }

    private PreviewRequest Fixture(string? runtimePath = null)
    {
        Assert.Equal(Architecture.X64, RuntimeInformation.ProcessArchitecture);
        string output = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        string assets = Path.Combine(_root, "custom-assets.json");
        string cache = Directory.CreateDirectory(Path.Combine(_root, "cache")).FullName;
        File.WriteAllText(assets, JsonSerializer.Serialize(new { packageFolders = new Dictionary<string, object> { [cache] = new { } } }));
        var files = new Dictionary<string, object>
        {
            ["runtime"] = new Dictionary<string, object> { [runtimePath ?? "lib/net10.0/Library.dll"] = new { } },
            ["compile"] = new Dictionary<string, object> { ["ref/net10.0/Library.dll"] = new { } },
            ["resources"] = new Dictionary<string, object> { ["lib/net10.0/fr/Library.resources.dll"] = new { locale = "fr" } },
            ["runtimeTargets"] = new Dictionary<string, object>
            {
                ["runtimes/win-x64/lib/net10.0/Library.dll"] = new { rid = "win-x64", assetType = "runtime" },
                ["runtimes/linux-x64/lib/net10.0/Library.dll"] = new { rid = "linux-x64", assetType = "runtime" },
                ["runtimes/win-x64/native/native.dll"] = new { rid = "win-x64", assetType = "native" }
            }
        };
        File.WriteAllText(Path.Combine(output, "Project.deps.json"), JsonSerializer.Serialize(new
        {
            runtimeTarget = new { name = "net10.0" },
            targets = new Dictionary<string, object> { ["net10.0"] = new Dictionary<string, object> { ["Package/1.2.3"] = files } },
            libraries = new Dictionary<string, object> { ["Package/1.2.3"] = new { type = "package", path = "package/1.2.3" } }
        }));
        return new("View.xaml", "", 1, AssemblyPath: Path.Combine(output, "Project.dll"), ProjectDirectory: _root, ProjectAssetsPath: assets);
    }

    public void Dispose()
    {
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(_root), ignoreCase: true);
        Directory.Delete(_root, true);
    }
}
