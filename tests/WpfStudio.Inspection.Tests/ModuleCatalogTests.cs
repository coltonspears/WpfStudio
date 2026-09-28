using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using WpfStudio.Runtime.Inspection;
using Xunit.Abstractions;

namespace WpfStudio.Inspection.Tests;

public sealed class ModuleCatalogTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("net8.0-windows")]
    [InlineData("net9.0-windows")]
    [InlineData("net10.0-windows")]
    public async Task LoadedModuleIdentityAndCompiledSourceHintsComeFromTheRealApplication(string framework)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        await using var session = new InspectionSession();
        await using var app = new FixtureApplication(framework);
        app.Start(InspectionAgentUnderTest.CreateEnvironment(new Dictionary<string, string>(), session));
        session.ExpectProcess(app.ProcessId);
        var ready = await app.WaitForReadyAsync(timeout.Token);
        InspectionAgentUnderTest.AssertLoadedAgent(ready);
        var hello = await session.WaitForConnectionAsync(timeout.Token);
        Assert.Contains("modules", hello.Capabilities);

        var catalog = await session.GetModulesAsync(timeout.Token);
        Assert.False(catalog.Truncated, catalog.Status);
        Assert.InRange(catalog.Modules.Count, 1, 512);
        var application = Assert.Single(catalog.Modules, module => module.AssemblyName == "WpfStudio.InspectionFixture");
        Assert.True(application.IsResourceAssembly);
        Assert.Equal(Path.GetFullPath(app.Program), application.Path, ignoreCase: true);
        Assert.StartsWith("WpfStudio.InspectionFixture,", application.AssemblyFullName);
        using (var stream = File.OpenRead(app.Program))
        using (var reader = new PEReader(stream))
        {
            var metadata = reader.GetMetadataReader();
            Assert.Equal(metadata.GetGuid(metadata.GetModuleDefinition().Mvid), application.ModuleVersionId);
        }
        Assert.DoesNotContain(catalog.Modules, module => module.AssemblyName is
            "WpfStudio.Inspection.Agent" or "WpfStudio.Inspection.Protocol" or "WpfStudio.Wpf.PropertyEditing" or "WpfStudio.Wpf.Diagnostics");
        Assert.Contains(catalog.Modules, module => module.AssemblyName == "PresentationFramework" && !module.IsResourceAssembly);
        Assert.All(catalog.Modules, module => Assert.NotEqual(Guid.Empty, module.ModuleVersionId));

        var tree = await RunningApplicationTests.WaitForTreeAsync(session,
            snapshot => snapshot.Nodes.Any(node => node.Name == "GoodText") &&
                snapshot.Nodes.Any(node => node.Name == "PopupText"), timeout.Token);
        var good = Assert.Single(tree.Nodes, node => node.Name == "GoodText");
        var source = Assert.IsType<WpfStudio.Inspection.Protocol.InspectionSourceHint>(good.Source);
        Assert.Contains("primarywindow.xaml", source.Uri, StringComparison.OrdinalIgnoreCase);
        Assert.True(source.Line > 0);
        Assert.True(source.Column > 0);
        output.WriteLine($"{framework}: {source.Uri} ({source.Line},{source.Column}), resource owner {application.AssemblyFullName}, loaded MVID {application.ModuleVersionId}");
        // The element's CLR type is in PresentationFramework, while the compiled
        // source hint refers to the fixture resource. They are distinct owners.
        Assert.Equal("System.Windows.Controls.TextBlock", good.Type);
        Assert.Contains(tree.Nodes, node => node.Name == "PopupText" && node.Source is null);

        var again = await session.GetModulesAsync(timeout.Token);
        Assert.Equal(application, Assert.Single(again.Modules, module => module.AssemblyName == application.AssemblyName));
        Assert.Equal("ping", await app.SendAsync("ping", timeout.Token));
        await session.DisconnectAsync(timeout.Token);
        Assert.Equal("ping", await app.SendAsync("ping", timeout.Token));
    }
}
