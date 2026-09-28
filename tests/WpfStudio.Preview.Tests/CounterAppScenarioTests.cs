using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using WpfStudio.Contracts;
using WpfStudio.Core.Wpf;
using WpfStudio.Runtime.Design;

namespace WpfStudio.Preview.Tests;

public sealed class CounterAppScenarioTests
{
    [Theory]
    [InlineData(PreviewMode.Source, "Initial", "0")]
    [InlineData(PreviewMode.Source, "Counted", "42")]
    [InlineData(PreviewMode.Source, "Large count", "999999")]
    [InlineData(PreviewMode.Compiled, "Initial", "0")]
    [InlineData(PreviewMode.Compiled, "Counted", "42")]
    [InlineData(PreviewMode.Compiled, "Large count", "999999")]
    public async Task BundledCatalogScenariosRenderTheActualCounterView(PreviewMode mode, string scenarioName, string expectedCount)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "CounterAppFixture");
        string sourcePath = Path.Combine(directory, "MainWindow.xaml");
        string assemblyPath = Path.Combine(directory, "CounterApp.dll");
        Assert.True(File.Exists(assemblyPath), "The coordinated build must copy the CounterApp output before this test runs.");
        Assert.True(File.Exists(Path.Combine(directory, "Themes", "Colors.xaml")));
        AssertCounterAssemblyIsNotLoadedInTestProcess();
        string source = await File.ReadAllTextAsync(sourcePath);
        var catalog = await PreviewScenarioCatalog.LoadAsync(directory, sourcePath);
        Assert.Empty(catalog.Warnings); Assert.NotNull(catalog.Fingerprint);
        var scenario = Assert.Single(catalog.Scenarios, scenario => scenario.Name == scenarioName);
        string executable = PreviewHostUnderTest.ExecutablePath;
        await using var client = new PreviewClient(executable);
        // CounterApp's empty Application.Resources compiles into generated C#;
        // only MainWindow and its theme dictionary have BAML resources.
        var snapshot = await client.RenderAsync(new(sourcePath, source, 721, 500, 350,
            assemblyPath, directory, mode, "CounterApp.MainWindow", ApplicationResourcePath: null, Scenario: scenario));
        Assert.True(snapshot.Success, string.Join("\n", snapshot.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.NotEmpty(snapshot.PngBytes!);
        using var host = Process.GetProcessById(client.ProcessId!.Value);
        Assert.Equal(Path.GetFullPath(executable), Path.GetFullPath(host.MainModule!.FileName), ignoreCase: true);
        Assert.Equal(scenario, snapshot.Scenario!.Configuration);
        Assert.Equal(assemblyPath, snapshot.Scenario.AssemblyPath);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(assemblyPath))), snapshot.Scenario.AssemblySha256);

        var countBindings = new List<(PreviewNode Node, PreviewProperty Property, PreviewInspection Inspection)>();
        foreach (var node in snapshot.Nodes.Where(node => node.Type == "System.Windows.Controls.TextBlock"))
        {
            var inspection = await client.InspectAsync(new(snapshot.Version, node.Id));
            foreach (var property in inspection.Properties.Where(property => property.Name == "Text" && property.BindingPath == "Count"))
                countBindings.Add((node, property, inspection));
        }
        var count = Assert.Single(countBindings);
        Assert.Equal(expectedCount, count.Property.Value);
        Assert.Equal("Active", count.Property.BindingStatus);
        Assert.Equal("CounterApp.CounterViewModel", count.Property.DataContextType);
        Assert.Contains(count.Inspection.Properties, property => property.Name == "Foreground" && property.Value == "#FF7BB5D5");
        Assert.Contains(count.Inspection.Properties, property => property.Name == "DataContext" && property.ValueSource == "Scenario" && !property.CanWriteSource);
        Assert.DoesNotContain(count.Inspection.Diagnostics, diagnostic => diagnostic.Severity == "Error");
        if (mode == PreviewMode.Source)
        {
            Assert.Null(snapshot.Build);
            Assert.Equal(sourcePath, count.Node.Source!.Path);
            Assert.Contains("Text=\"{Binding Count}\"", source.Substring(count.Node.Source.Start, count.Node.Source.Length));
        }
        else
        {
            Assert.Equal("CounterApp.MainWindow", snapshot.Build!.ViewTypeName);
            Assert.Null(snapshot.Build.ApplicationResourcePath);
            Assert.All(snapshot.Nodes, node => Assert.Null(node.Source));
        }
        AssertCounterAssemblyIsNotLoadedInTestProcess();
    }

    private static void AssertCounterAssemblyIsNotLoadedInTestProcess() =>
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name == "CounterApp");
}
