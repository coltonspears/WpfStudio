using System.Security.Cryptography;
using System.Text;
using WpfStudio.Contracts;
using WpfStudio.Workspace;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlFormattingIntegrationTests
{
    [Fact]
    public async Task FailedWorkerLoadCannotTreatEmptyOwnershipInventoryAsStandalone()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WpfStudio-XamlFormatting-Missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "View.xaml"), project = Path.Combine(directory, "Missing.csproj");
        const string text = "<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Button/><Button/></Grid>";
        try
        {
            await File.WriteAllTextAsync(path, text);
            await using var client = new WorkspaceClient();
            var snapshot = await client.LoadAsync(new(project, "Release"));
            Assert.Contains(snapshot.Issues, issue => issue.Severity == "Error");
            Assert.True(client.IsConnected);

            // The worker still supports file navigation, but absent type metadata
            // must not make authored content qualify for standalone assumptions.
            var failedLoad = await client.FormatXamlAsync(new(path, text, 1));
            Assert.Empty(failedLoad.Documents);
            Assert.NotEmpty(failedLoad.Warnings);
            var failedRequestedContext = await client.FormatXamlAsync(new(path, text, 1, project));
            Assert.Empty(failedRequestedContext.Documents);
            Assert.NotEmpty(failedRequestedContext.Warnings);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task WorkerFormatsUnsavedXamlWithCompilerTypesAndCheckedSourceIdentity()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WpfStudio-XamlFormatting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string project = Path.Combine(directory, "Fixture.csproj"), path = Path.Combine(directory, "View.xaml");
        string modelPath = Path.Combine(directory, "Custom.cs");
        const string model = "using System.Collections.Generic; using System.Windows.Markup; namespace Fixture; [ContentProperty(nameof(Children))] public class CustomBox { public CustomItems Children { get; } = new(); } public class CustomItems : List<object> { } public class CustomPanel : System.Windows.Controls.StackPanel { }";
        const string original = "<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBlock Text='Saved'/></Grid>";
        const string unsaved = "<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel><TextBlock><Run Text='A'/><Run Text='B'/></TextBlock><Button  Content = 'Unsaved &amp; safe' /></StackPanel></Grid>";
        try
        {
            await File.WriteAllTextAsync(project, "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF></PropertyGroup></Project>");
            await File.WriteAllTextAsync(path, original);
            await File.WriteAllTextAsync(modelPath, model);
            Assert.Equal(0, (await new BuildService().RunAsync(new(project, BuildOperation.Restore, "Release"))).ExitCode);
            await using var client = new WorkspaceClient();
            var snapshot = await client.LoadAsync(new(project, "Release"));
            Assert.DoesNotContain(snapshot.Issues, issue => issue.Severity == "Error");
            var formatted = await client.FormatXamlAsync(new(path, unsaved, 17, project));
            var document = Assert.Single(formatted.Documents);
            Assert.Equal(path, document.Path);
            Assert.Equal(17, document.Version);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(unsaved))), document.ExpectedTextHash);
            Assert.NotEmpty(document.Edits);
            string result = Apply(unsaved, document.Edits);
            Assert.Contains("\n", result);
            Assert.Contains("Content='Unsaved &amp; safe'", result);
            Assert.Matches("<Run Text='A'\\s*/><Run Text='B'\\s*/>", result);
            Assert.Empty(Assert.Single((await client.FormatXamlAsync(new(path, result, 18, project))).Documents).Edits);
            Assert.Equal(original, await File.ReadAllTextAsync(path));

            var malformed = await client.FormatXamlAsync(new(path, "<Grid><", 19, project));
            Assert.Empty(malformed.Documents);
            Assert.NotEmpty(malformed.Warnings);
            var standalone = await client.FormatXamlAsync(new(Path.Combine(directory, "Unowned.xaml"), unsaved, 20));
            Assert.NotEmpty(Assert.Single(standalone.Documents).Edits);
            var unownedExplicitContext = await client.FormatXamlAsync(new(Path.Combine(directory, "Unowned.xaml"), unsaved, 20, project));
            Assert.Empty(unownedExplicitContext.Documents);
            Assert.NotEmpty(unownedExplicitContext.Warnings);

            const string custom = "<local:CustomBox xmlns:local='clr-namespace:Fixture' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Button/><Button/></local:CustomBox>";
            var customEdits = Assert.Single((await client.FormatXamlAsync(new(path, custom, 21, project))).Documents).Edits;
            string customFormatted = Apply(custom, customEdits);
            Assert.Matches("<Button\\s*/>\\s+<Button", customFormatted);
            string significant = model.Replace("public class CustomItems", "[WhitespaceSignificantCollection] public class CustomItems", StringComparison.Ordinal);
            Assert.True((await client.UpdateDocumentAsync(new(modelPath, significant, 1, Analyze: false))).Accepted);
            var preservedEdits = Assert.Single((await client.FormatXamlAsync(new(path, custom, 22, project))).Documents).Edits;
            Assert.Matches("<Button\\s*/><Button", Apply(custom, preservedEdits));
            Assert.Equal(model, await File.ReadAllTextAsync(modelPath));
            string derivedPanel = custom.Replace("CustomBox", "CustomPanel", StringComparison.Ordinal);
            var derivedEdits = Assert.Single((await client.FormatXamlAsync(new(path, derivedPanel, 23, project))).Documents).Edits;
            Assert.Matches("<Button\\s*/>\\s+<Button", Apply(derivedPanel, derivedEdits));

            var wrongContext = await client.FormatXamlAsync(new(path, unsaved, 24, Path.Combine(directory, "Other.csproj")));
            Assert.Empty(wrongContext.Documents);
            Assert.NotEmpty(wrongContext.Warnings);
            await client.CloseDocumentAsync(modelPath);
            using (var locked = new FileStream(modelPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var refresh = await client.RefreshDiskDocumentsAsync(new([modelPath]));
                Assert.True(refresh.Truncated);
                var unavailable = await client.FormatXamlAsync(new(path, unsaved, 25, project));
                Assert.Empty(unavailable.Documents);
                Assert.NotEmpty(unavailable.Warnings);
            }
            Assert.False((await client.RefreshDiskDocumentsAsync(new([modelPath]))).Truncated);
            Assert.NotEmpty(Assert.Single((await client.FormatXamlAsync(new(path, unsaved, 26, project))).Documents).Edits);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string Apply(string text, IReadOnlyList<TextEdit> edits)
    {
        foreach (var edit in edits.OrderByDescending(edit => edit.Start)) text = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.NewText);
        return text;
    }
}
