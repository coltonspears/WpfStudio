using System.Security.Cryptography;
using System.Text;
using WpfStudio.Contracts;
using WpfStudio.Workspace;

namespace WpfStudio.Workspace.Tests;

public sealed class XamlResourceScaleIntegrationTests
{
    private const string Model = "namespace Fixture; public class Customer { public string Name { get; set; } = \"\"; } public class Order { public string Title { get; set; } = \"\"; }";
    private const string Source = """
        <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <UserControl.Resources><ResourceDictionary Source="ZZResources/Entry.xaml" /></UserControl.Resources>
          <TextBlock Text="{Binding Source={StaticResource Current}, Path=Name}" />
        </UserControl>
        """;

    [Fact]
    public async Task LargeCatalogKeepsOrdinaryLanguageOperationsAndDependencyFreshness()
    {
        await using var fixture = await ScaleFixture.CreateAsync();
        int position = Source.IndexOf("Path=Name", StringComparison.Ordinal) + 5;
        string original = Dictionary("Customer");
        // The dependency sorts beyond 600 unrelated views. Their read state does
        // not participate in this consumer's completion or spelling proof.
        using var unrelated = new FileStream(fixture.PathFor("Views/View0599.xaml"), FileMode.Open, FileAccess.Read, FileShare.None);
        var completion = await fixture.Client.GetXamlCompletionsAsync(fixture.Request(Source, position));
        Assert.True(completion.Available, completion.Status);
        Assert.Contains(completion.Completion!.Items, item => item.DisplayText == "Name");
        Assert.Null(completion.Status);
        Assert.Contains("Customer", (await fixture.Client.GetXamlHoverAsync(fixture.Request(Source, position + 1)))!.Text);
        Assert.Equal(fixture.PathFor("Models.cs"), Assert.Single(await fixture.Client.GetXamlDefinitionAsync(fixture.Request(Source, position + 1))).Path);

        string typo = Source.Replace("Path=Name", "Path=Nmae", StringComparison.Ordinal);
        var analysis = await fixture.Client.AnalyzeXamlAsync(new(fixture.View, typo, 1, fixture.Project));
        Assert.True(analysis.Accepted, analysis.Status);
        Assert.Contains(analysis.Diagnostics, issue => issue.Id == "XAMLBIND001" && typo.Substring(issue.Start, issue.Length) == "Nmae");
        var action = Assert.Single(await fixture.Client.GetXamlCodeActionsAsync(fixture.Request(typo, position + 1)));
        var dataGuard = Assert.Single(action.AdditionalEdits!, edit => edit.Path == fixture.Data);
        Assert.Equal(Hash(original), dataGuard.ExpectedTextHash);
        Assert.Contains(action.AdditionalEdits!, edit => edit.Path == fixture.PathFor("ZZResources/Entry.xaml"));
        Assert.DoesNotContain(action.AdditionalEdits!, edit => edit.Path.Contains("View0599", StringComparison.Ordinal));

        XamlDocumentOverlay[] overlays = [new(fixture.Data, Dictionary("Order"), 12)];
        var changed = await fixture.Client.GetXamlCompletionsAsync(fixture.Request(Source, position, overlays));
        Assert.Contains(changed.Completion!.Items, item => item.DisplayText == "Title");
        Assert.DoesNotContain(changed.Completion.Items, item => item.DisplayText == "Name");
        var changedAnalysis = await fixture.Client.AnalyzeXamlAsync(new(fixture.View, Source, 1, fixture.Project, overlays));
        Assert.Contains(changedAnalysis.Diagnostics, issue => issue.Id == "XAMLBIND001");
        Assert.Contains((await fixture.Client.GetXamlCompletionsAsync(fixture.Request(Source, position))).Completion!.Items,
            item => item.DisplayText == "Name");

        DateTime timestamp = File.GetLastWriteTimeUtc(fixture.Data);
        string replacement = Dictionary("Order").PadRight(original.Length);
        Assert.Equal(original.Length, replacement.Length);
        await File.WriteAllTextAsync(fixture.Data, replacement);
        File.SetLastWriteTimeUtc(fixture.Data, timestamp);
        Assert.Contains((await fixture.Client.GetXamlCompletionsAsync(fixture.Request(Source, position))).Completion!.Items,
            item => item.DisplayText == "Title");
        using (var locked = new FileStream(fixture.Data, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var unknown = await fixture.Client.AnalyzeXamlAsync(new(fixture.View, typo, 1, fixture.Project));
            Assert.True(unknown.Accepted, unknown.Status);
            Assert.DoesNotContain(unknown.Diagnostics, issue => issue.Id == "XAMLBIND001");
        }
        await File.WriteAllTextAsync(fixture.Data, original);
        Assert.Contains((await fixture.Client.GetXamlCompletionsAsync(fixture.Request(Source, position))).Completion!.Items,
            item => item.DisplayText == "Name");

        // A targeted editor request is not evidence of a complete project scan.
        var batch = await fixture.Client.AnalyzeXamlProjectAsync(new(1, [], fixture.Project));
        Assert.True(batch.Accepted, batch.Status);
        Assert.True(batch.Truncated);
        Assert.True(batch.TotalFiles > batch.Files.Count);
        var rejection = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.RenameAsync(new(fixture.PathFor("Models.cs"),
            Model.IndexOf("string Name", StringComparison.Ordinal) + 7, 0, "DisplayName", Model, fixture.Project)));
        Assert.Contains("scan budget", rejection.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Model, await File.ReadAllTextAsync(fixture.PathFor("Models.cs")));
        Assert.Equal(Source, await File.ReadAllTextAsync(fixture.View));
        Assert.Contains((await fixture.Client.GetXamlCompletionsAsync(fixture.Request(Source, position))).Completion!.Items,
            item => item.DisplayText == "Name");
    }

    [Fact]
    public async Task LargeCatalogPreservesUnknownLaterMergeAndDirectKeyPrecedence()
    {
        await using var fixture = await ScaleFixture.CreateAsync();
        string merged = Source.Replace("<ResourceDictionary Source=\"ZZResources/Entry.xaml\" />", """
            <ResourceDictionary xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:m="clr-namespace:Fixture">
              <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="ZZResources/Entry.xaml" />
                <ResourceDictionary Source="ZZResources/Later.xaml" />
              </ResourceDictionary.MergedDictionaries>
            </ResourceDictionary>
            """, StringComparison.Ordinal).Replace("Path=Name", "Path=Nmae", StringComparison.Ordinal);
        using (var locked = new FileStream(fixture.PathFor("ZZResources/Later.xaml"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var unknown = await fixture.Client.AnalyzeXamlAsync(new(fixture.View, merged, 1, fixture.Project));
            Assert.True(unknown.Accepted, unknown.Status);
            Assert.DoesNotContain(unknown.Diagnostics, issue => issue.Id == "XAMLBIND001");
            string direct = merged.Replace("</ResourceDictionary.MergedDictionaries>",
                "</ResourceDictionary.MergedDictionaries><m:Customer x:Key=\"Current\" />", StringComparison.Ordinal);
            var known = await fixture.Client.AnalyzeXamlAsync(new(fixture.View, direct, 2, fixture.Project));
            Assert.True(known.Accepted, known.Status);
            Assert.Contains(known.Diagnostics, issue => issue.Id == "XAMLBIND001");
            Assert.NotEmpty(await fixture.Client.GetXamlCodeActionsAsync(fixture.Request(direct,
                direct.IndexOf("Path=Nmae", StringComparison.Ordinal) + 6)));
        }
        File.Delete(fixture.PathFor("ZZResources/Later.xaml"));
        Assert.DoesNotContain((await fixture.Client.AnalyzeXamlAsync(new(fixture.View, merged, 3, fixture.Project))).Diagnostics,
            issue => issue.Id == "XAMLBIND001");
        await File.WriteAllTextAsync(fixture.PathFor("ZZResources/Later.xaml"), Dictionary("Customer"));
        Assert.Contains((await fixture.Client.AnalyzeXamlAsync(new(fixture.View, merged, 4, fixture.Project))).Diagnostics,
            issue => issue.Id == "XAMLBIND001");
    }

    [Fact]
    public async Task UnloadedDuplicateResourceIdentityCannotAppearUniqueInLargeCatalog()
    {
        await using var fixture = await ScaleFixture.CreateAsync(duplicate: true);
        int position = Source.IndexOf("Path=Name", StringComparison.Ordinal) + 5;
        using var duplicate = new FileStream(fixture.PathFor("ZZDuplicate.xaml"), FileMode.Open, FileAccess.Read, FileShare.None);
        var completion = await fixture.Client.GetXamlCompletionsAsync(fixture.Request(Source, position));
        Assert.True(completion.Available, completion.Status);
        Assert.DoesNotContain(completion.Completion!.Items, item => item.DisplayText is "Name" or "Title");
        string typo = Source.Replace("Path=Name", "Path=Nmae", StringComparison.Ordinal);
        var analysis = await fixture.Client.AnalyzeXamlAsync(new(fixture.View, typo, 1, fixture.Project));
        Assert.True(analysis.Accepted, analysis.Status);
        Assert.DoesNotContain(analysis.Diagnostics, issue => issue.Id == "XAMLBIND001");
        var hover = await fixture.Client.GetXamlHoverAsync(fixture.Request(Source, position + 1));
        Assert.Contains("same URI", hover!.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Client.GetXamlCodeActionsAsync(fixture.Request(typo, position + 1)));
    }

    private static string Dictionary(string type) => $$"""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:m="clr-namespace:Fixture">
          <m:{{type}} x:Key="Current" />
        </ResourceDictionary>
        """;

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class ScaleFixture : IAsyncDisposable
    {
        public string Directory { get; }
        public string Project => PathFor("Fixture.csproj");
        public string View => PathFor("Consumer.xaml");
        public string Data => PathFor("ZZResources/Data.xaml");
        public WorkspaceClient Client { get; }
        private ScaleFixture(string directory, string? host) { Directory = directory; Client = new(host); }
        public string PathFor(string relative) => Path.Combine(Directory, relative.Replace('/', Path.DirectorySeparatorChar));
        public XamlCompletionRequest Request(string text, int position, IReadOnlyList<XamlDocumentOverlay>? overlays = null) =>
            new(View, text, position, 1, Project, overlays);

        public static async Task<ScaleFixture> CreateAsync(bool duplicate = false)
        {
            string directory = Path.Combine(Path.GetTempPath(), "WpfStudio-ResourceScale-" + Guid.NewGuid().ToString("N"));
            var fixture = new ScaleFixture(directory, Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_WORKSPACE_HOST"));
            System.IO.Directory.CreateDirectory(fixture.PathFor("Views"));
            System.IO.Directory.CreateDirectory(fixture.PathFor("ZZResources"));
            try
            {
                await File.WriteAllTextAsync(fixture.Project, $$"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF></PropertyGroup>
                      {{(duplicate ? "<ItemGroup><Page Update=\"ZZDuplicate.xaml\" LogicalName=\"ZZResources/Data.xaml\" /></ItemGroup>" : "")}}
                    </Project>
                    """);
                await File.WriteAllTextAsync(fixture.View, Source);
                await File.WriteAllTextAsync(fixture.PathFor("Models.cs"), Model);
                await File.WriteAllTextAsync(fixture.Data, Dictionary("Customer"));
                await File.WriteAllTextAsync(fixture.PathFor("ZZResources/Later.xaml"), Dictionary("Order"));
                await File.WriteAllTextAsync(fixture.PathFor("ZZResources/Entry.xaml"), """
                    <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                      <ResourceDictionary.MergedDictionaries><ResourceDictionary Source="Data.xaml" /></ResourceDictionary.MergedDictionaries>
                    </ResourceDictionary>
                    """);
                if (duplicate) await File.WriteAllTextAsync(fixture.PathFor("ZZDuplicate.xaml"), Dictionary("Order"));
                for (int index = 0; index < 600; index++)
                    await File.WriteAllTextAsync(fixture.PathFor($"Views/View{index:D4}.xaml"),
                        "<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><TextBlock Text=\"Unrelated view\" /></UserControl>");
                Assert.Equal(0, (await new BuildService().RunAsync(new(fixture.Project, BuildOperation.Restore))).ExitCode);
                var workspace = await fixture.Client.LoadAsync(new(fixture.Project, "Release"));
                Assert.DoesNotContain(workspace.Issues, issue => issue.Severity == "Error");
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            // This is the fixture's exact generated root, never a discovered tree.
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }
}
