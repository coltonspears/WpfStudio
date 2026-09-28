using WpfStudio.Contracts;
using WpfStudio.Workspace;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlQualifiedBindingIntegrationTests
{
    [Fact]
    public async Task AttachedPropertyContinuationUsesRealWpfMetadataAndUnsavedGetterType()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio-QualifiedBinding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var project = Path.Combine(directory, "Fixture.csproj");
        var view = Path.Combine(directory, "View.xaml");
        var model = Path.Combine(directory, "Provider.cs");
        var source = ModelSource("Customer");
        const string xaml = """
            <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:p="clr-namespace:Fixture">
                <TextBlock Text="{Binding RelativeSource={RelativeSource Self}, Path=(p:Provider.Value).Nmae}" />
            </UserControl>
            """;
        try
        {
            await File.WriteAllTextAsync(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0-windows</TargetFramework>
                    <UseWPF>true</UseWPF>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(view, xaml);
            await File.WriteAllTextAsync(model, source);
            Assert.Equal(0, (await new BuildService().RunAsync(new BuildRequest(project, BuildOperation.Restore))).ExitCode);
            string? hostPath = Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_WORKSPACE_HOST");
            if (hostPath is not null) Assert.True(File.Exists(hostPath), "The explicitly selected workspace worker must exist.");
            await using var client = new WorkspaceClient(hostPath);
            var workspace = await client.LoadAsync(new LoadWorkspaceRequest(project, "Release"));
            Assert.DoesNotContain(workspace.Issues, issue => issue.Severity == "Error");

            var analysis = await client.AnalyzeXamlAsync(new(view, xaml, 1, project));
            Assert.True(analysis.Accepted, analysis.Status);
            var typo = Assert.Single(analysis.Diagnostics);
            Assert.Equal("XAMLBIND001", typo.Id);
            Assert.Equal("Nmae", xaml.Substring(typo.Start, typo.Length));
            Assert.Contains("Name", typo.Message);

            // Keep RelativeSource before the unfinished path so completion has a
            // known WPF DependencyObject source even while this attribute is open.
            var typed = xaml[..xaml.IndexOf("Nmae", StringComparison.Ordinal)];
            var completion = await client.GetXamlCompletionsAsync(new(view, typed, typed.Length, 2, project));
            Assert.True(completion.Available, completion.Status);
            Assert.NotNull(completion.Completion);
            Assert.Equal(typed.Length, completion.Completion.Start);
            Assert.Equal(0, completion.Completion.Length);
            Assert.Contains(completion.Completion.Items, item => item.DisplayText == "Name");
            Assert.DoesNotContain(completion.Completion.Items, item => item.DisplayText == "Title");

            var corrected = xaml.Replace("Nmae", "Name", StringComparison.Ordinal);
            var valid = await client.AnalyzeXamlAsync(new(view, corrected, 3, project));
            Assert.True(valid.Accepted, valid.Status);
            Assert.Empty(valid.Diagnostics);
            var definition = Assert.Single(await client.GetXamlDefinitionAsync(
                new(view, corrected, corrected.IndexOf(").Name", StringComparison.Ordinal) + 3, 3, project)));
            Assert.Equal(model, definition.Path);
            Assert.Equal(source.IndexOf("Name {", StringComparison.Ordinal), definition.Start);
            Assert.Equal("Name", source.Substring(definition.Start, definition.Length));

            var getter = Assert.Single(await client.GetXamlDefinitionAsync(
                new(view, corrected, corrected.IndexOf("Provider.Value", StringComparison.Ordinal) + "Provider.".Length + 1, 3, project)));
            Assert.Equal(model, getter.Path);
            Assert.Equal("GetValue", source.Substring(getter.Start, getter.Length));

            // The XAML is unchanged and both model classes still exist. Only the
            // attached property's declared value/getter type changes in memory.
            var unsaved = ModelSource("Order");
            Assert.True((await client.UpdateDocumentAsync(new(model, unsaved, 1, Analyze: false))).Accepted);
            Assert.Equal(source, await File.ReadAllTextAsync(model));
            var changed = await client.AnalyzeXamlAsync(new(view, corrected, 3, project));
            Assert.True(changed.Accepted, changed.Status);
            var oldMember = Assert.Single(changed.Diagnostics);
            Assert.Equal("XAMLBIND001", oldMember.Id);
            Assert.Equal("Name", corrected.Substring(oldMember.Start, oldMember.Length));

            var updated = await client.GetXamlCompletionsAsync(new(view, typed, typed.Length, 4, project));
            Assert.True(updated.Available, updated.Status);
            Assert.NotNull(updated.Completion);
            Assert.Contains(updated.Completion.Items, item => item.DisplayText == "Title");
            Assert.DoesNotContain(updated.Completion.Items, item => item.DisplayText == "Name");
            var titleXaml = corrected.Replace(").Name", ").Title", StringComparison.Ordinal);
            var titleAnalysis = await client.AnalyzeXamlAsync(new(view, titleXaml, 5, project));
            Assert.True(titleAnalysis.Accepted, titleAnalysis.Status);
            Assert.Empty(titleAnalysis.Diagnostics);
            var updatedDefinition = Assert.Single(await client.GetXamlDefinitionAsync(
                new(view, titleXaml, titleXaml.IndexOf(").Title", StringComparison.Ordinal) + 3, 5, project)));
            Assert.Equal(model, updatedDefinition.Path);
            Assert.Equal(unsaved.IndexOf("Title {", StringComparison.Ordinal), updatedDefinition.Start);
            Assert.Equal("Title", unsaved.Substring(updatedDefinition.Start, updatedDefinition.Length));

            await client.CloseDocumentAsync(model);
            var restored = await client.AnalyzeXamlAsync(new(view, corrected, 3, project));
            Assert.True(restored.Accepted, restored.Status);
            Assert.Empty(restored.Diagnostics);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string ModelSource(string valueType) => $$"""
        using System.Windows;

        namespace Fixture;

        public static class Provider
        {
            public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
                "Value", typeof({{valueType}}), typeof(Provider), new PropertyMetadata(null));

            public static {{valueType}} GetValue(DependencyObject target) => ({{valueType}})target.GetValue(ValueProperty);
            public static void SetValue(DependencyObject target, {{valueType}} value) => target.SetValue(ValueProperty, value);
        }

        public sealed class Customer
        {
            public string Name { get; set; } = "Ada";
        }

        public sealed class Order
        {
            public string Title { get; set; } = "Draft";
        }
        """;
}
