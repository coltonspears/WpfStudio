using WpfStudio.Contracts;
using WpfStudio.Workspace;

namespace WpfStudio.Workspace.Tests;

public sealed class LinkedFileTests
{
    [Fact]
    public async Task EvaluatedLogicalPathsSurviveRoslynSnapshotForLinkedSourcesAndResources()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio-Linked-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(directory, "Controls");
        Directory.CreateDirectory(app);
        var code = Path.Combine(directory, "Shared.cs");
        var image = Path.Combine(directory, "logo.png");
        await File.WriteAllTextAsync(code, "public class Shared { public int Value => 42; }");
        await File.WriteAllBytesAsync(image, [1, 2, 3]);
        var project = Path.Combine(app, "Controls.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF></PropertyGroup><ItemGroup><Compile Include=\"../Shared.cs\" Link=\"Models/Shared.cs\"/><Resource Include=\"../logo.png\"><Link>Images/logo.png</Link></Resource></ItemGroup></Project>");
        try
        {
            Assert.Equal(0, (await new BuildService().RunAsync(new BuildRequest(project, BuildOperation.Restore))).ExitCode);
            await using var client = new WorkspaceClient();
            var snapshot = await client.LoadAsync(new LoadWorkspaceRequest(project));
            var files = Assert.Single(snapshot.Projects).Files;
            Assert.Equal("Models/Shared.cs", Assert.Single(files, file => file.Path == code).LogicalPath?.Replace('\\', '/'));
            Assert.Equal("Images/logo.png", Assert.Single(files, file => file.Path == image).LogicalPath?.Replace('\\', '/'));
            var update = await client.UpdateDocumentAsync(new UpdateDocumentRequest(code, await File.ReadAllTextAsync(code), 1));
            Assert.True(update.Accepted);
            Assert.DoesNotContain(update.Diagnostics, diagnostic => diagnostic.Severity == "Error");
        }
        finally { Directory.Delete(directory, true); }
    }
}
