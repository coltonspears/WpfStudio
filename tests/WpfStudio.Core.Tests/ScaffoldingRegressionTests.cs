using WpfStudio.Contracts;
using WpfStudio.Core.Wpf;

namespace WpfStudio.Core.Tests;

public sealed class ScaffoldingRegressionTests
{
    [Fact]
    public async Task FallbackScanRecognisesTheToolkitReferenceRegardlessOfCase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio-Scaffold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // An SDK that cannot be resolved makes MSBuild evaluation fail, so the service falls back to scanning the XML.
            var project = Path.Combine(directory, "Fallback.csproj");
            await File.WriteAllTextAsync(project, "<Project Sdk=\"WpfStudio.Tests.MissingSdk\"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"communitytoolkit.mvvm\" Version=\"8.4.2\" /></ItemGroup></Project>");
            var changes = await new ScaffoldingService().CreateAsync(project, directory, "Customer", ScaffoldKind.ViewModel);
            var change = Assert.Single(changes);
            Assert.EndsWith("CustomerViewModel.cs", change.Path);
        }
        finally { Directory.Delete(directory, true); }
    }
}
