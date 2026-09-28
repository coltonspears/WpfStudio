using System.Reflection;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

public sealed class XamlLiveFieldShellTests
{
    private const string Markup = """
        <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                x:Class="LiveFieldShell.View">
          <StackPanel><TextBox x:Name="Input"/><TextBlock Text="{Binding Text, ElementName=Input}"/></StackPanel>
        </Window>
        """;
    private const string Code = "using System.Windows; namespace LiveFieldShell; public partial class View : Window { public View() { InitializeComponent(); } }";
    private const string Observer = "namespace LiveFieldShell; public partial class View { public string Observe() => this.Input.Text; }";
    private const string CustomInput = "namespace LiveFieldShell; public class CustomInput : System.Windows.Controls.TextBox { }";

    [Fact]
    public async Task DirectNameEditsRefreshUnchangedCsharpAndSurviveUndoRestartAndDiscardWithoutAReviewedPlan()
    {
        await using var fixture = await Fixture.CreateAsync();
        var test = fixture.Test;
        await test.Shell.OpenDocumentAsync(fixture.ObserverPath);
        var observer = test.Shell.ActiveDocument!;
        observer.State.Content = Observer.Replace("Input", "ContactEmail", StringComparison.Ordinal);
        await test.Shell.OpenDocumentAsync(fixture.XamlPath);
        var xaml = test.Shell.ActiveDocument!;
        await xaml.RefreshAnalysisAsync();
        await observer.RefreshAnalysisAsync();
        Assert.Contains(observer.Diagnostics, MissingField);
        long codeVersion = observer.State.Version;
        string changed = Markup.Replace("x:Name=\"Input\"", "x:Name=\"ContactEmail\"", StringComparison.Ordinal);

        await ObserveCsharpRefreshAsync(test, observer, expectMissingField: false, async () =>
        {
            xaml.State.Content = changed;
            await xaml.RefreshAnalysisAsync();
        });
        Assert.Equal(codeVersion, observer.State.Version);
        Assert.Contains(xaml.Diagnostics, diagnostic => diagnostic.Id == "XAMLNAME004");
        Assert.DoesNotContain("unavailable", xaml.LanguageStatus, StringComparison.OrdinalIgnoreCase);
        await AssertFieldsAsync(observer, "ContactEmail", "Input");

        // Restoring the original text models ordinary editor undo, with no
        // reviewed rename plan or workspace transaction to supply authority.
        await ObserveCsharpRefreshAsync(test, observer, expectMissingField: true, async () =>
        {
            xaml.State.Content = Markup;
            await xaml.RefreshAnalysisAsync();
        });
        await AssertFieldsAsync(observer, "Input", "ContactEmail");
        await ObserveCsharpRefreshAsync(test, observer, expectMissingField: false, async () =>
        {
            xaml.State.Content = changed;
            await xaml.RefreshAnalysisAsync();
        });
        await ObserveCsharpRefreshAsync(test, observer, expectMissingField: false,
            () => test.Shell.RestartWorkspaceCommand.ExecuteAsync(null));
        Assert.True(test.Workspace.IsConnected, test.Shell.Status);
        Assert.Equal(codeVersion, observer.State.Version);
        await AssertFieldsAsync(observer, "ContactEmail", "Input");

        test.Dialogs.SaveDecisions.Enqueue(SaveDecision.Discard);
        await ObserveCsharpRefreshAsync(test, observer, expectMissingField: true, async () =>
            Assert.True(await test.Shell.CloseDocumentAsync(xaml)));
        Assert.Equal(codeVersion, observer.State.Version);
        await AssertFieldsAsync(observer, "Input", "ContactEmail");
        await test.Shell.OpenDocumentAsync(fixture.XamlPath);
        var reopened = test.Shell.ActiveDocument!;
        Assert.Equal(Markup, reopened.State.Content);
        await ObserveCsharpRefreshAsync(test, observer, expectMissingField: false, async () =>
        {
            reopened.State.Content = changed;
            await reopened.RefreshAnalysisAsync();
        });
        Assert.Equal(codeVersion, observer.State.Version);
        Assert.Equal(Markup, await File.ReadAllTextAsync(fixture.XamlPath));
        Assert.Equal(Observer, await File.ReadAllTextAsync(fixture.ObserverPath));
        await fixture.AssertGeneratedUnchangedAsync();
        Assert.Empty(test.Dialogs.Errors);
    }

    [Fact]
    public async Task DirectElementTypeEditsRefreshTheExistingCsharpMemberErrorWithoutChangingItsBuffer()
    {
        await using var fixture = await Fixture.CreateAsync();
        var test = fixture.Test;
        await test.Shell.OpenDocumentAsync(fixture.ObserverPath);
        var observer = test.Shell.ActiveDocument!;
        await test.Shell.OpenDocumentAsync(fixture.XamlPath);
        var xaml = test.Shell.ActiveDocument!;
        await xaml.RefreshAnalysisAsync();
        await observer.RefreshAnalysisAsync();
        Assert.DoesNotContain(observer.Diagnostics, diagnostic => diagnostic.Severity == "Error");
        long version = observer.State.Version;

        await ObserveCsharpRefreshAsync(test, observer, expectMissingField: true, async () =>
        {
            xaml.State.Content = Markup.Replace("<TextBox x:Name=\"Input\"/>", "<Button x:Name=\"Input\"/>", StringComparison.Ordinal);
            await xaml.RefreshAnalysisAsync();
        });
        Assert.Equal(version, observer.State.Version);
        Assert.Contains(observer.Diagnostics, diagnostic => diagnostic.Id == "CS1061" && diagnostic.Message.Contains("Text", StringComparison.Ordinal));
        var buttonMembers = await observer.CompleteAsync(observer.State.Content.IndexOf("this.Input.", StringComparison.Ordinal) + "this.Input.".Length);
        Assert.Contains(buttonMembers.Items, item => item.DisplayText == "Content");
        Assert.DoesNotContain(buttonMembers.Items, item => item.DisplayText == "Text");

        await ObserveCsharpRefreshAsync(test, observer, expectMissingField: false, async () =>
        {
            xaml.State.Content = Markup;
            await xaml.RefreshAnalysisAsync();
        });
        Assert.Equal(version, observer.State.Version);
        Assert.Equal(Observer, observer.State.Content);
        var textMembers = await observer.CompleteAsync(observer.State.Content.IndexOf("this.Input.", StringComparison.Ordinal) + "this.Input.".Length);
        Assert.Contains(textMembers.Items, item => item.DisplayText == "Text");
        await fixture.AssertGeneratedUnchangedAsync();
        Assert.Empty(test.Dialogs.Errors);
    }

    [Fact]
    public async Task CustomControlModelChangesAndDiscardRefreshOtherCsharpWithoutCancellingTheirOwnAnalysis()
    {
        await using var fixture = await Fixture.CreateAsync(customControl: true);
        var test = fixture.Test;
        string modelPath = Path.Combine(test.Root, "CustomInput.cs");
        await test.Shell.OpenDocumentAsync(fixture.XamlPath);
        var xaml = test.Shell.ActiveDocument!;
        await xaml.RefreshAnalysisAsync();
        await test.Shell.OpenDocumentAsync(fixture.ObserverPath);
        var observer = test.Shell.ActiveDocument!;
        await observer.RefreshAnalysisAsync();
        await test.Shell.OpenDocumentAsync(modelPath);
        var model = test.Shell.ActiveDocument!;
        await model.RefreshAnalysisAsync();
        await observer.RefreshAnalysisAsync();
        Assert.DoesNotContain(observer.Diagnostics, diagnostic => diagnostic.Severity == "Error");
        long observerVersion = observer.State.Version, xamlVersion = xaml.State.Version;
        int modelChanges = 0;
        test.Workspace.CSharpModelChanged += (_, path) =>
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(path, modelPath)) Interlocked.Increment(ref modelChanges);
        };

        await ObserveCsharpRefreshAsync(test, observer, expectMissingField: true, async () =>
        {
            model.State.Content = "#warning Custom model edit analyzed\n" + CustomInput.Replace("TextBox", "Button", StringComparison.Ordinal);
            await model.RefreshAnalysisAsync();
            // Its own model-change event must not cancel the originating
            // editor before the diagnostics from this analysis are applied.
            Assert.Contains(model.Diagnostics, diagnostic => diagnostic.Id == "CS1030" && diagnostic.Message.Contains("Custom model edit analyzed", StringComparison.Ordinal));
        });
        Assert.Equal(1, Volatile.Read(ref modelChanges));
        Assert.Equal(observerVersion, observer.State.Version);
        Assert.Equal(xamlVersion, xaml.State.Version);
        Assert.Contains(observer.Diagnostics, diagnostic => diagnostic.Id == "CS1061" && diagnostic.Message.Contains("Text", StringComparison.Ordinal));
        var members = await observer.CompleteAsync(observer.State.Content.IndexOf("this.Input.", StringComparison.Ordinal) + "this.Input.".Length);
        Assert.Contains(members.Items, item => item.DisplayText == "Content");
        Assert.DoesNotContain(members.Items, item => item.DisplayText == "Text");
        await model.RefreshAnalysisAsync();
        Assert.Equal(1, Volatile.Read(ref modelChanges));

        test.Dialogs.SaveDecisions.Enqueue(SaveDecision.Discard);
        await ObserveCsharpRefreshAsync(test, observer, expectMissingField: false, async () =>
            Assert.True(await test.Shell.CloseDocumentAsync(model)));
        Assert.Equal(2, Volatile.Read(ref modelChanges));
        Assert.Equal(observerVersion, observer.State.Version);
        Assert.Equal(xamlVersion, xaml.State.Version);
        members = await observer.CompleteAsync(observer.State.Content.IndexOf("this.Input.", StringComparison.Ordinal) + "this.Input.".Length);
        Assert.Contains(members.Items, item => item.DisplayText == "Text");
        Assert.Equal(CustomInput, await File.ReadAllTextAsync(modelPath));
        await fixture.AssertGeneratedUnchangedAsync();
        Assert.Empty(test.Dialogs.Errors);
    }

    [Fact]
    public async Task AcceptedXamlChangesNotifyWithoutPlansButUnchangedOrRejectedVersionsStayQuiet()
    {
        await using var fixture = await Fixture.CreateAsync();
        var client = fixture.Test.Workspace;
        int notifications = 0;
        client.NameProjectionChanged += (_, _) => Interlocked.Increment(ref notifications);
        Assert.True((await client.UpdateDocumentAsync(new(fixture.XamlPath, Markup, 1, Analyze: false))).Accepted);
        Assert.Equal(1, Volatile.Read(ref notifications));
        Assert.True((await client.UpdateDocumentAsync(new(fixture.XamlPath, Markup, 1, Analyze: false))).Accepted);
        Assert.True((await client.UpdateDocumentAsync(new(fixture.XamlPath, Markup, 2, Analyze: false))).Accepted);
        Assert.Equal(1, Volatile.Read(ref notifications));

        string changed = Markup.Replace("x:Name=\"Input\"", "x:Name=\"ContactEmail\"", StringComparison.Ordinal);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.UpdateDocumentAsync(
                new(fixture.XamlPath, changed, 3, Analyze: false), cancellation.Token));
        }
        Assert.Equal(1, Volatile.Read(ref notifications));
        Assert.True((await client.UpdateDocumentAsync(new(fixture.XamlPath, changed, 3, Analyze: false))).Accepted);
        Assert.Equal(2, Volatile.Read(ref notifications));
        Assert.True((await client.UpdateDocumentAsync(new(fixture.XamlPath, changed, 3, Analyze: false))).Accepted);
        Assert.False((await client.UpdateDocumentAsync(new(fixture.XamlPath, Markup, 1, Analyze: false))).Accepted);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => client.UpdateDocumentAsync(new(fixture.XamlPath, Markup, 3, Analyze: false)));
        Assert.Equal(2, Volatile.Read(ref notifications));

        await client.RestartAsync();
        int afterRestart = Volatile.Read(ref notifications);
        Assert.True(afterRestart > 2);
        Assert.True((await client.UpdateDocumentAsync(new(fixture.XamlPath, changed, 3, Analyze: false))).Accepted);
        Assert.Equal(afterRestart, Volatile.Read(ref notifications));
        await client.CloseDocumentAsync(fixture.XamlPath);
        Assert.True(Volatile.Read(ref notifications) > afterRestart);
        int afterClose = Volatile.Read(ref notifications);
        Assert.True((await client.UpdateDocumentAsync(new(fixture.XamlPath, Markup, 1, Analyze: false))).Accepted);
        Assert.Equal(afterClose + 1, Volatile.Read(ref notifications));
        int beforeLoad = Volatile.Read(ref notifications);
        await client.LoadAsync(new(fixture.ProjectPath, "Release"));
        Assert.True(Volatile.Read(ref notifications) > beforeLoad);
        Assert.True((await client.UpdateDocumentAsync(new(fixture.XamlPath, Markup, 1, Analyze: false))).Accepted);
        await fixture.AssertGeneratedUnchangedAsync();
    }

    [Theory]
    [InlineData(".xaml")]
    [InlineData(".cs")]
    public async Task CanceledDispatchedUpdateCannotSuppressRestoringPreviouslyAcknowledgedText(string extension)
    {
        await using var client = new WorkspaceClient();
        var remote = DispatchProxy.Create<IWorkspaceRpc, CommitBeforeReplyRpc>();
        var server = (CommitBeforeReplyRpc)remote;
        // Inject only the transport seam: cancellation after a worker commit
        // must be deterministic, rather than racing a real compilation.
        var proxyField = typeof(WorkspaceClient).GetField("_proxy", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(proxyField);
        proxyField.SetValue(client, remote);
        string path = Path.Combine(Path.GetTempPath(), "WpfStudio-CanceledNotification", "View" + extension);
        int names = 0, models = 0;
        client.NameProjectionChanged += (_, _) => names++;
        client.CSharpModelChanged += (_, changedPath) => { Assert.Equal(path, changedPath); models++; };
        int Notifications() => extension == ".xaml" ? names : models;

        Assert.True((await client.UpdateDocumentAsync(new(path, "A", 1, Analyze: false))).Accepted);
        Assert.Equal(1, Notifications());
        using (var cancellation = new CancellationTokenSource())
        {
            server.CancelReply = cancellation;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.UpdateDocumentAsync(
                new(path, "B", 2, Analyze: false), cancellation.Token));
        }
        Assert.Equal("B", server.CommittedText); // Other queries can now observe B.
        Assert.Equal(1, Notifications());
        Assert.True((await client.UpdateDocumentAsync(new(path, "A", 3, Analyze: false))).Accepted);
        Assert.Equal("A", server.CommittedText);
        Assert.Equal(2, Notifications()); // Restoring A must invalidate those observations of B.
        Assert.True((await client.UpdateDocumentAsync(new(path, "A", 3, Analyze: false))).Accepted);
        Assert.Equal(2, Notifications());

        int dispatched = server.UpdateCount;
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.UpdateDocumentAsync(
                new(path, "B", 4, Analyze: false), cancellation.Token));
        }
        Assert.Equal(dispatched, server.UpdateCount);
        Assert.True((await client.UpdateDocumentAsync(new(path, "A", 4, Analyze: false))).Accepted);
        Assert.Equal(2, Notifications());
        Assert.Equal(0, extension == ".xaml" ? models : names);
    }

    public class CommitBeforeReplyRpc : DispatchProxy
    {
        public string? CommittedText { get; private set; }
        public int UpdateCount { get; private set; }
        public CancellationTokenSource? CancelReply { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IWorkspaceRpc.UpdateDocumentAsync)) throw new NotSupportedException(targetMethod?.Name);
            var request = (UpdateDocumentRequest)args![0]!;
            CommittedText = request.Text;
            UpdateCount++;
            if (CancelReply is { } cancellation)
            {
                CancelReply = null;
                cancellation.Cancel();
                return Task.FromCanceled<DocumentUpdateResult>((CancellationToken)args[1]!);
            }
            return Task.FromResult(new DocumentUpdateResult(true, request.Version, []));
        }
    }

    private static bool MissingField(WorkspaceDiagnostic diagnostic) => diagnostic.Severity == "Error" && diagnostic.Id is "CS0103" or "CS1061";

    private static async Task ObserveCsharpRefreshAsync(ShellTestContext test, EditorViewModel observer, bool expectMissingField, Func<Task> change)
    {
        long version = observer.State.Version;
        int invalidated = 0;
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Invalidated(object? sender, EventArgs args) => Interlocked.Exchange(ref invalidated, 1);
        void ModelChanged(object? sender, string path)
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals(path, observer.State.Path)) Interlocked.Exchange(ref invalidated, 1);
        }
        void Received(object? sender, WorkspaceDiagnosticEvent args)
        {
            if (Volatile.Read(ref invalidated) != 0 && args.Path == observer.State.Path && args.Version == version
                && args.Diagnostics.Any(MissingField) == expectMissingField)
                refreshed.TrySetResult();
        }
        test.Workspace.NameProjectionChanged += Invalidated;
        test.Workspace.CSharpModelChanged += ModelChanged;
        test.Workspace.DiagnosticsReceived += Received;
        try
        {
            await change();
            // An invalidation clears the UI's old rows immediately. Wait for a
            // completed worker analysis as well, so an empty transient list
            // cannot masquerade as a recovered binding-generated field.
            await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (observer.Diagnostics.Any(MissingField) != expectMissingField)
                await Task.Delay(10, timeout.Token);
            Assert.Equal(version, observer.State.Version);
            Assert.DoesNotContain("unavailable", observer.LanguageStatus, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            test.Workspace.NameProjectionChanged -= Invalidated;
            test.Workspace.CSharpModelChanged -= ModelChanged;
            test.Workspace.DiagnosticsReceived -= Received;
        }
    }

    private static async Task AssertFieldsAsync(EditorViewModel editor, string present, string absent)
    {
        var result = await editor.CompleteAsync(editor.State.Content.IndexOf("this.", StringComparison.Ordinal) + 5);
        Assert.Contains(result.Items, item => item.DisplayText == present);
        Assert.DoesNotContain(result.Items, item => item.DisplayText == absent);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Dictionary<string, byte[]> _generated;
        private Fixture(ShellTestContext test, string project, string xaml, string observer, Dictionary<string, byte[]> generated)
        { Test = test; ProjectPath = project; XamlPath = xaml; ObserverPath = observer; _generated = generated; }
        public ShellTestContext Test { get; }
        public string ProjectPath { get; }
        public string XamlPath { get; }
        public string ObserverPath { get; }

        public static async Task<Fixture> CreateAsync(bool customControl = false)
        {
            var test = new ShellTestContext();
            try
            {
                string project = await test.CreateFileAsync("Fixture.csproj", "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF></PropertyGroup></Project>");
                string markup = customControl ? Markup.Replace("<Window ", "<Window xmlns:local=\"clr-namespace:LiveFieldShell\" ", StringComparison.Ordinal)
                    .Replace("<TextBox x:Name=\"Input\"/>", "<local:CustomInput x:Name=\"Input\"/>", StringComparison.Ordinal) : Markup;
                string xaml = await test.CreateFileAsync("View.xaml", markup);
                if (customControl) await test.CreateFileAsync("CustomInput.cs", CustomInput);
                await test.CreateFileAsync("View.xaml.cs", Code);
                string observer = await test.CreateFileAsync("Observer.cs", Observer);
                foreach (var operation in new[] { BuildOperation.Restore, BuildOperation.Build })
                {
                    var result = await new BuildService().RunAsync(new(project, operation, "Release"));
                    Assert.True(result.ExitCode == 0, string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
                }
                test.Shell.Workspace = await test.Workspace.LoadAsync(new(project, "Release"));
                var generated = Directory.EnumerateFiles(Path.Combine(test.Root, "obj"), "*.g.cs", SearchOption.AllDirectories)
                    .ToDictionary(path => path, File.ReadAllBytes);
                Assert.NotEmpty(generated);
                return new(test, project, xaml, observer, generated);
            }
            catch { await test.DisposeAsync(); throw; }
        }

        public async Task AssertGeneratedUnchangedAsync()
        {
            foreach (var (path, bytes) in _generated) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }

        public ValueTask DisposeAsync() => Test.DisposeAsync();
    }
}
