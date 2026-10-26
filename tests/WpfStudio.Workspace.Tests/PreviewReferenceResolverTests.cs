using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WpfStudio.Workspace;

namespace WpfStudio.Workspace.Tests;

public sealed class PreviewReferenceResolverTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("WpfStudio-HintPath-").FullName;

    [Fact]
    public void SelectKeepsHintPathReferencesAndSkipsFrameworkAndPackageAssemblies()
    {
        var hint = Path.Combine(_root, "lib", "Vendor.Controls.dll").Replace("\\", "\\\\");
        var dependency = Path.Combine(_root, "lib", "Vendor.Core.dll").Replace("\\", "\\\\");
        var satellite = Path.Combine(_root, "lib", "de", "Vendor.Controls.resources.dll").Replace("\\", "\\\\");
        var project = Path.Combine(_root, "Shared", "bin", "Shared.dll").Replace("\\", "\\\\");
        var json = $$"""
        { "Items": {
          "ReferencePath": [
            { "FullPath": "{{hint}}", "ReferenceSourceTarget": "ResolveAssemblyReference", "ResolvedFrom": "{HintPathFromItem}" },
            { "FullPath": "{{project}}", "ReferenceSourceTarget": "ProjectReference" },
            { "FullPath": "/packs/ref/System.Runtime.dll", "FrameworkReferenceName": "Microsoft.NETCore.App" },
            { "FullPath": "/nuget/ref/Package.dll", "NuGetPackageId": "Package" }
          ],
          "ReferenceDependencyPaths": [ { "FullPath": "{{dependency}}" }, { "FullPath": "/lib/Vendor.xml" } ],
          "ReferenceSatellitePaths": [ { "FullPath": "{{satellite}}", "DestinationSubDirectory": "de\\" },
                                       { "FullPath": "{{satellite}}", "DestinationSubDirectory": "..\\" } ],
          "ProjectReference": [ { "FullPath": "/src/Shared/Shared.csproj" },
                                { "FullPath": "/src/Tool/Tool.csproj", "ReferenceOutputAssembly": "false" } ]
        } }
        """;
        var selection = PreviewReferenceResolver.Select(json);
        Assert.Equal(["Vendor.Controls.dll", "Shared.dll", "Vendor.Core.dll", "de/Vendor.Controls.resources.dll"], selection.Assemblies.Select(a => a.RelativePath));
        Assert.Equal([Path.Combine(_root, "lib")], selection.SearchDirectories);
        Assert.Equal(["/src/Shared/Shared.csproj"], selection.ProjectReferences);
    }

    [Fact]
    public async Task FollowsPrivateFalseHintPathsTheirDependenciesAndReferencedProjects()
    {
        // Vendor.Controls depends on Vendor.Core; only Controls is referenced, with Private=false, so neither is copied.
        var lib = Directory.CreateDirectory(Path.Combine(_root, "lib")).FullName;
        var core = Emit(Path.Combine(lib, "Vendor.Core.dll"), "namespace Vendor; public class Theme { }");
        Emit(Path.Combine(lib, "Vendor.Controls.dll"), "namespace Vendor; public class Gauge { public Theme Theme { get; } = new(); }", core);
        var tools = Directory.CreateDirectory(Path.Combine(_root, "tools")).FullName;
        Emit(Path.Combine(tools, "Vendor.Tools.dll"), "namespace Vendor.Tools; public class Helper { }");
        var framework = $"net{Environment.Version.Major}.0";
        Write("Shared/Shared.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>{framework}</TargetFramework></PropertyGroup>
            <ItemGroup><Reference Include="Vendor.Tools"><HintPath>..\tools\Vendor.Tools.dll</HintPath><Private>false</Private></Reference></ItemGroup></Project>
            """);
        var app = Write("App/App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>{framework}</TargetFramework></PropertyGroup>
            <ItemGroup><Reference Include="Vendor.Controls"><HintPath>..\lib\Vendor.Controls.dll</HintPath><Private>false</Private></Reference>
            <ProjectReference Include="..\Shared\Shared.csproj" /></ItemGroup></Project>
            """);
        var restore = await ProcessRunner.CaptureAsync(ProcessRunner.DotNet(Path.GetDirectoryName(app)!, "restore", app), CancellationToken.None);
        Assert.True(restore.ExitCode == 0, restore.Output + restore.Error);

        var references = await PreviewReferenceResolver.ResolveAsync(app, "Debug", framework, CancellationToken.None);

        Assert.Empty(references.Warnings);
        var names = references.Assemblies.Select(reference => reference.RelativePath).ToArray();
        Assert.Contains("Vendor.Controls.dll", names);
        Assert.Contains("Vendor.Core.dll", names);
        Assert.Contains("Vendor.Tools.dll", names);
        Assert.Contains("Shared.dll", names);
        Assert.DoesNotContain(names, name => name.StartsWith("System.", StringComparison.Ordinal));
        Assert.Contains(lib, references.SearchDirectories, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(tools, references.SearchDirectories, StringComparer.OrdinalIgnoreCase);
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.Replace('\\', Path.DirectorySeparatorChar));
        return path;
    }

    private static string Emit(string path, string source, params string[] references)
    {
        var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(file => Path.GetFileName(file) is "System.Runtime.dll" or "System.Private.CoreLib.dll");
        var compilation = CSharpCompilation.Create(Path.GetFileNameWithoutExtension(path), [CSharpSyntaxTree.ParseText(source)],
            platform.Concat(references).Select(file => MetadataReference.CreateFromFile(file)), new(OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
