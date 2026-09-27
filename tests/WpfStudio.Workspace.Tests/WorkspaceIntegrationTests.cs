using WpfStudio.Contracts;
using WpfStudio.Workspace;

namespace WpfStudio.Workspace.Tests;

public class WorkspaceIntegrationTests
{
    [Fact]
    public async Task LaunchChoicesHonorConfigurationAndBuildDiagnosticsStayBounded()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio.Workspace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var project = Path.Combine(directory, "Conditional.csproj");
        var warnings = string.Join(';', Enumerable.Range(1, 2200));
        await File.WriteAllTextAsync(project, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType></PropertyGroup><PropertyGroup Condition=\"'$(Configuration)' == 'QA'\"><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><Noise Include=\"{warnings}\" /></ItemGroup><Target Name=\"EmitWarnings\" BeforeTargets=\"Build\"><Warning Text=\"Warning %(Noise.Identity)\" Code=\"LIMIT001\" /></Target></Project>");
        var build = new BuildService();
        Assert.Empty(await build.DiscoverLaunchTargetsAsync(project));
        Assert.Equal(["net9.0"], Assert.Single(await build.DiscoverLaunchTargetsAsync(project, configuration: "QA")).TargetFrameworks);
        var diagnosticEvents = 0;
        var limitMessages = 0;
        var result = await build.RunAsync(new BuildRequest(project), new SynchronousProgress<BuildOutputEvent>(output =>
        {
            if (output.Diagnostic != null) Interlocked.Increment(ref diagnosticEvents);
            if (output.Text.StartsWith("Additional build diagnostics omitted", StringComparison.Ordinal)) Interlocked.Increment(ref limitMessages);
        }));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2000, result.Diagnostics.Count);
        Assert.Equal(2000, diagnosticEvents);
        Assert.Equal(1, limitMessages);
    }

    [Fact]
    public async Task FailedLoadDisconnectsWorker()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio.Workspace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var solution = Path.Combine(directory, "Malformed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution><Project");
        await using var client = new WorkspaceClient();
        await Assert.ThrowsAnyAsync<Exception>(() => client.LoadAsync(new LoadWorkspaceRequest(solution)));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task DisposeCancelsAnInProgressWorkspaceLoad()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio.Workspace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var project = Path.Combine(directory, "Loading.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var client = new WorkspaceClient();
        var loading = client.LoadAsync(new LoadWorkspaceRequest(project));
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        Assert.False(client.IsConnected);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task RealWpfWorkspaceSupportsGeneratedMembersUnsavedIntelligenceAndRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio.Workspace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var project = Path.Combine(directory, "Fixture.csproj");
        var view = Path.Combine(directory, "MainWindow.xaml.cs");
        const string source = "using System.Windows; namespace Fixture; public partial class MainWindow : Window { public MainWindow() { InitializeComponent(); TitleLabel.Text = new MainViewModel().Name; } public string Echo(string value, int count) => value; public void Use() { Echo(\"x\", 1); } }";
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><Nullable>enable</Nullable><Configurations>Debug;Release;QA</Configurations></PropertyGroup><ItemGroup><PackageReference Include=\"CommunityToolkit.Mvvm\" Version=\"8.4.0\" /></ItemGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(directory, "App.xaml"), "<Application x:Class=\"Fixture.App\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" />");
        await File.WriteAllTextAsync(Path.Combine(directory, "App.xaml.cs"), "using System.Windows; namespace Fixture; public partial class App : Application { }");
        await File.WriteAllTextAsync(Path.Combine(directory, "MainWindow.xaml"), "<Window x:Class=\"Fixture.MainWindow\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"><TextBlock x:Name=\"TitleLabel\" /></Window>");
        await File.WriteAllTextAsync(view, source);
        await File.WriteAllTextAsync(Path.Combine(directory, "MainViewModel.cs"), "using CommunityToolkit.Mvvm.ComponentModel; namespace Fixture; public partial class MainViewModel : ObservableObject { [ObservableProperty] private string name = \"Ada\"; }");
        await File.WriteAllTextAsync(Path.Combine(directory, "Other.cs"), "namespace Fixture; public class Other { public string Use(MainWindow window) => window.Echo(\"other\", 2); }");
        Directory.CreateDirectory(Path.Combine(directory, "Properties"));
        await File.WriteAllTextAsync(Path.Combine(directory, "Properties", "launchSettings.json"), "{\"profiles\":{\"Desktop\":{\"commandName\":\"Project\",\"commandLineArgs\":\"--label \\\"two words\\\"\",\"workingDirectory\":\".\",\"environmentVariables\":{\"FIXTURE_ENV\":\"ready\"}}}}");
        var build = new BuildService();
        var messages = new List<string>();
        var result = await build.RunAsync(new BuildRequest(project), new SynchronousProgress<BuildOutputEvent>(e => { lock (messages) messages.Add(e.Text); }));
        Assert.True(result.ExitCode == 0, string.Join(Environment.NewLine, messages));
        await using var client = new WorkspaceClient();
        var snapshot = await client.LoadAsync(new LoadWorkspaceRequest(project));
        Assert.Single(snapshot.Projects);
        Assert.Contains("QA", snapshot.Configurations!);
        Assert.DoesNotContain(snapshot.Issues, i => i.Severity == "Error");
        Assert.Contains(snapshot.Projects[0].Files, f => f.Path.EndsWith(".xaml"));
        Assert.Contains(snapshot.Projects[0].Files, f => f.IsGenerated);
        var update = await client.UpdateDocumentAsync(new UpdateDocumentRequest(view, source, 1));
        Assert.True(update.Accepted);
        Assert.DoesNotContain(update.Diagnostics, d => d.Severity == "Error");

        var invocation = source.IndexOf("Echo(\"x\"", StringComparison.Ordinal);
        var definitions = await client.GetDefinitionAsync(new DocumentPositionRequest(view, invocation + 1, 1));
        Assert.Single(definitions);
        Assert.Equal(view, definitions[0].Path);
        var references = await client.FindReferencesAsync(new DocumentPositionRequest(view, source.IndexOf("Echo(string", StringComparison.Ordinal) + 1, 1));
        Assert.NotEmpty(references);
        var signatures = await client.GetSignatureHelpAsync(new DocumentPositionRequest(view, invocation + "Echo(\"x\", ".Length, 1));
        Assert.Contains(signatures.Signatures, s => s.Parameters.Count == 2);
        Assert.Equal(1, signatures.ActiveParameter);
        var formatted = await client.FormatDocumentAsync(new DocumentRequest(view, 1));
        Assert.NotEmpty(formatted.Documents[0].Edits);
        var rename = await client.RenameAsync(new RenameRequest(view, invocation + 1, 1, "Repeat"));
        Assert.Contains(rename.Documents.SelectMany(d => d.Edits), e => e.NewText.Contains("Repeat"));
        Assert.NotEmpty(rename.Warnings);
        Assert.Contains(rename.Documents, d => d.Path.EndsWith("Other.cs") && d.Version == 0 && d.ExpectedTextHash is { Length: 64 });
        var generatedDefinition = await client.GetDefinitionAsync(new DocumentPositionRequest(view, source.IndexOf(".Name", StringComparison.Ordinal) + 2, 1));
        Assert.NotEmpty(generatedDefinition);
        Assert.All(generatedDefinition, d => Assert.True(File.Exists(d.Path)));

        var completionSource = source.Replace("new MainViewModel().Name", "new MainViewModel().Na");
        await client.UpdateDocumentAsync(new UpdateDocumentRequest(view, completionSource, 2));
        var completionPosition = completionSource.IndexOf(".Na;", StringComparison.Ordinal) + 3;
        var completions = await client.GetCompletionsAsync(new DocumentPositionRequest(view, completionPosition, 2));
        var name = Assert.Single(completions.Items, i => i.DisplayText == "Name");
        var latestCompletions = await client.GetCompletionsAsync(new DocumentPositionRequest(view, completionPosition, 2));
        Assert.Null(await client.GetCompletionEditAsync(new CompletionEditRequest(view, 2, name.Id)));
        name = Assert.Single(latestCompletions.Items, i => i.DisplayText == "Name");
        await client.UpdateDocumentAsync(new UpdateDocumentRequest(view, completionSource, 2, Analyze: false));
        var edit = await client.GetCompletionEditAsync(new CompletionEditRequest(view, 2, name.Id));
        Assert.NotNull(edit);
        Assert.Equal("Name", edit.NewText);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.UpdateDocumentAsync(new UpdateDocumentRequest(view, source, 2)));
        Assert.False((await client.UpdateDocumentAsync(new UpdateDocumentRequest(view, source, 1))).Accepted);
        await client.RestartAsync();
        Assert.Contains((await client.GetCompletionsAsync(new DocumentPositionRequest(view, completionPosition, 2))).Items, i => i.DisplayText == "Name");
        await client.CloseDocumentAsync(view);
        Assert.True((await client.UpdateDocumentAsync(new UpdateDocumentRequest(view, source, 1))).Accepted);
        var targets = await build.DiscoverLaunchTargetsAsync(project);
        Assert.Single(targets);
        Assert.Contains("net10.0-windows", targets[0].TargetFrameworks);
        var launch = await build.ResolveLaunchAsync(project, "Debug", null, "Desktop");
        Assert.EndsWith("Fixture.dll", launch.Program);
        Assert.Equal(directory, launch.WorkingDirectory);
        Assert.Equal(["--label", "two words"], launch.Arguments);
        Assert.Equal("ready", launch.Environment["FIXTURE_ENV"]);
        var slnx = Path.Combine(directory, "Fixture.slnx");
        await File.WriteAllTextAsync(slnx, "<Solution><Project Path=\"Fixture.csproj\" /></Solution>");
        var solution = await client.LoadAsync(new LoadWorkspaceRequest(slnx));
        Assert.Single(solution.Projects);
        Assert.DoesNotContain(solution.Issues, i => i.Severity == "Error");
        var sln = Path.Combine(directory, "Fixture.sln");
        await File.WriteAllTextAsync(sln, "Microsoft Visual Studio Solution File, Format Version 12.00\n# Visual Studio Version 17\nProject(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Fixture\", \"Fixture.csproj\", \"{DF3150EE-97E5-41B6-8F82-5B73323D1DBE}\"\nEndProject\nGlobal\nEndGlobal\n");
        Assert.Single((await client.LoadAsync(new LoadWorkspaceRequest(sln))).Projects);
        Assert.Equal(0, (await build.RunAsync(new BuildRequest(project, Configuration: "QA"))).ExitCode);
        var custom = await client.LoadAsync(new LoadWorkspaceRequest(project, "QA"));
        Assert.Contains(Path.DirectorySeparatorChar + "QA" + Path.DirectorySeparatorChar, Assert.Single(custom.Projects).OutputPath!);
        // Keep temporary project files for failed-run investigation; OS temporary storage owns cleanup.
    }

    [Fact]
    public async Task MissingGlobalJsonSdkStillAllowsFileNavigation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio.Workspace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "global.json"), "{\"sdk\":{\"version\":\"99.0.100\",\"rollForward\":\"disable\"}}");
        var project = Path.Combine(directory, "MissingSdk.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(directory, "Sample.cs"), "class Sample {}");
        await using var client = new WorkspaceClient();
        var snapshot = await client.LoadAsync(new LoadWorkspaceRequest(project));
        Assert.Equal("Unavailable", snapshot.SdkVersion);
        Assert.Equal(["Debug", "Release"], snapshot.Configurations);
        Assert.Contains(snapshot.Issues, i => i.Severity == "Error");
        Assert.Contains(Assert.Single(snapshot.Projects).Files, f => f.Name == "Sample.cs");
    }

    [Fact]
    public async Task AvailablePinnedSdkIsUsedInsteadOfLatestSdk()
    {
        var sdkRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "sdk");
        var sdk = Directory.Exists(sdkRoot) ? Directory.GetDirectories(sdkRoot, "9.*").Select(Path.GetFileName).Order(StringComparer.Ordinal).LastOrDefault() : null;
        if (sdk is null) return; // Compatibility fixture runs when a .NET 9 SDK is installed alongside the host's .NET 10 runtime.
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio.Workspace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "global.json"), $"{{\"sdk\":{{\"version\":\"{sdk}\",\"rollForward\":\"disable\"}}}}");
        var project = Path.Combine(directory, "Pinned.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
        var source = Path.Combine(directory, "Sample.cs");
        await File.WriteAllTextAsync(source, "public class Sample { public string Name => \"pinned\"; }");
        var buildOutput = new List<string>();
        var buildResult = await new BuildService().RunAsync(new BuildRequest(project), new SynchronousProgress<BuildOutputEvent>(line => { lock (buildOutput) buildOutput.Add(line.Text); }));
        Assert.True(buildResult.ExitCode == 0, string.Join(Environment.NewLine, buildOutput));
        await using var client = new WorkspaceClient();
        var snapshot = await client.LoadAsync(new LoadWorkspaceRequest(project));
        Assert.Equal(sdk, snapshot.SdkVersion);
        Assert.DoesNotContain(snapshot.Issues, issue => issue.Severity == "Error");
        var update = await client.UpdateDocumentAsync(new UpdateDocumentRequest(source, await File.ReadAllTextAsync(source), 1));
        Assert.DoesNotContain(update.Diagnostics, diagnostic => diagnostic.Severity == "Error");
    }

    [Fact]
    public async Task CancellingBuildTerminatesItsRunningProcessTree()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio.Workspace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var project = Path.Combine(directory, "Waiting.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><Target Name=\"WaitForCancellation\" BeforeTargets=\"Build\"><Message Text=\"WAIT_STARTED\" Importance=\"high\" /><Exec Command=\"powershell -NoProfile -Command &amp;quot;Start-Sleep -Seconds 30&amp;quot;\" /></Target></Project>".Replace("&amp;quot;", "&quot;"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var observedWait = false;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await new BuildService().RunAsync(new BuildRequest(project), new SynchronousProgress<BuildOutputEvent>(output =>
        {
            if (output.Text.Contains("WAIT_STARTED", StringComparison.Ordinal)) { observedWait = true; cancellation.CancelAfter(300); }
        }), cancellation.Token);
        Assert.True(observedWait);
        Assert.True(result.Cancelled);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15));
    }

    private sealed class SynchronousProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
