using System.Windows.Threading;
using WpfStudio.App.Controls;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.CodeAssist;
using WpfStudio.Contracts;
using WpfStudio.Core;
using WpfStudio.Core.Documents;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

public sealed class CodeAssistEditorTests
{
    private static CodeAssistEngine NewEngine() => new(new AssistModelCatalog(AssistModelCatalog.DefaultDirectory));

    [Fact]
    public async Task ASettingsFileOlderThanTheFeatureLeavesItOff()
    {
        var directory = Directory.CreateTempSubdirectory("WpfStudio-CodeAssist-").FullName;
        try
        {
            Assert.False(new StudioSettings().CodeAssist.Enabled);
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), """{ "Theme": "Light", "ShowQuickInfo": false }""");
            var loaded = await new SettingsStore(directory).LoadAsync();
            Assert.Equal("Light", loaded.Theme);
            Assert.Equal(new CodeAssistSettings(), loaded.CodeAssist);
            Assert.False(loaded.CodeAssist.Enabled);
            Assert.False(loaded.CodeAssist.LineSuggestions);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SettingsSurviveTheSettingsFile()
    {
        var directory = Directory.CreateTempSubdirectory("WpfStudio-CodeAssist-").FullName;
        try
        {
            var saved = new CodeAssistSettings { Enabled = true, MemberModelVersion = "1.0.0", LineSuggestions = true, LineModelVersion = "1.0.0" };
            var store = new SettingsStore(directory);
            await store.SaveAsync(new StudioSettings { CodeAssist = saved });
            using var engine = NewEngine();
            var preferences = new CodeAssistPreferences(engine, new InlineDispatcher());
            preferences.Load((await store.LoadAsync()).CodeAssist);
            Assert.Equal(saved, preferences.ToSettings());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void AnOrdinaryStartupWithTheFeatureOffDoesNothing()
    {
        using var engine = NewEngine();
        var preferences = new CodeAssistPreferences(engine, new InlineDispatcher());
        int reports = 0;
        engine.StatusChanged += (_, _) => reports++;
        preferences.Load(new CodeAssistSettings());
        Assert.Equal(0, reports);
        Assert.False(engine.RanksMembers);
        Assert.False(engine.SuggestsLines);
    }

    [Fact]
    public async Task SwitchesReachTheEngineAndLineSuggestionsStayOffUntilAskedFor()
    {
        using var engine = NewEngine();
        var preferences = new CodeAssistPreferences(engine, new InlineDispatcher()) { Enabled = true };
        Assert.True(engine.RanksMembers);
        Assert.False(engine.SuggestsLines);
        preferences.LineSuggestions = true;
        Assert.True(engine.SuggestsLines);
        preferences.MemberModelVersion = "1.0.0";
        Assert.NotEmpty(await engine.RankMembersAsync("x.", 2, ["Add", "Clear"]));
        Assert.Contains("code-assist-members 1.0.0", engine.Status, StringComparison.Ordinal);
        preferences.Enabled = false;
        Assert.False(engine.RanksMembers);
        Assert.False(engine.SuggestsLines);
    }

    [Fact]
    public void ThePageOffersLatestAndEveryInstalledVersion()
    {
        using var engine = NewEngine();
        var preferences = new CodeAssistPreferences(engine, new InlineDispatcher());
        Assert.Equal(["Latest (2.0.0)", "2.0.0", "1.0.0"], preferences.MemberModels.Select(choice => choice.Label));
        Assert.Equal(["Latest (2.0.0)", "2.0.0", "1.0.0"], preferences.LineModels.Select(choice => choice.Label));
        Assert.Equal(CodeAssistSettings.LatestVersion, preferences.SelectedMemberModel!.Version);
        Assert.Contains("about 7 in 10", preferences.SelectedLineModel!.Limitations, StringComparison.Ordinal);
    }

    [Fact]
    public void AVersionNoLongerInstalledFollowsTheNewest()
    {
        using var engine = NewEngine();
        var preferences = new CodeAssistPreferences(engine, new InlineDispatcher());
        preferences.Load(new CodeAssistSettings { MemberModelVersion = "0.9.0", LineModelVersion = "1.0.0" });
        Assert.Equal(CodeAssistSettings.LatestVersion, preferences.MemberModelVersion);
        Assert.Equal("1.0.0", preferences.LineModelVersion);
    }

    [Fact]
    public Task AnAcceptedSuggestionIsOneUndoableInsertAtTheCaret() => OnDispatcherAsync(async () =>
    {
        await using var workspace = new WorkspaceClient();
        const string source = "int Answer() => ";
        var state = new DocumentState(Path.Combine(Path.GetTempPath(), "Line.cs"), source);
        using var model = new EditorViewModel(state, workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { });
        var editor = new EditorSurface { ViewModel = model };
        editor.CaretOffset = source.Length;
        Assert.False(editor.LineSuggestion.TryAccept());
        editor.LineSuggestion.Show("42;", source.Length);
        Assert.True(editor.LineSuggestion.IsVisible);
        Assert.Equal(source, state.Content);
        Assert.True(editor.LineSuggestion.TryAccept());
        Assert.Equal(source + "42;", state.Content);
        Assert.Equal(state.Content.Length, editor.CaretOffset);
        Assert.False(editor.LineSuggestion.IsVisible);
        editor.Undo();
        Assert.Equal(source, state.Content);
    });

    [Fact]
    public Task ASuggestionGoesAwayWhenTheCaretMovesOrTheTextChanges() => OnDispatcherAsync(async () =>
    {
        await using var workspace = new WorkspaceClient();
        const string source = "int a;\nint b;";
        var state = new DocumentState(Path.Combine(Path.GetTempPath(), "Dismiss.cs"), source);
        using var model = new EditorViewModel(state, workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { });
        var editor = new EditorSurface { ViewModel = model };

        editor.CaretOffset = source.Length;
        editor.LineSuggestion.Show(" // note", source.Length);
        editor.CaretOffset = 3;
        Assert.False(editor.LineSuggestion.IsVisible);
        Assert.False(editor.LineSuggestion.TryAccept());

        editor.CaretOffset = source.Length;
        editor.LineSuggestion.Show(" // note", source.Length);
        editor.Document.Insert(source.Length, "x");
        Assert.False(editor.LineSuggestion.IsVisible);
        Assert.Equal(source + "x", state.Content);

        editor.LineSuggestion.Show(" // note", editor.CaretOffset);
        state.Content = "replaced from outside";
        Assert.False(editor.LineSuggestion.IsVisible);
    });

    [Fact]
    public Task OnlyAnOpenLineInAnEditableCSharpFileAsksForSuggestions() => OnDispatcherAsync(async () =>
    {
        await using var workspace = new WorkspaceClient();
        using var engine = NewEngine();
        await engine.ConfigureAsync(new CodeAssistOptions(true, LineSuggestions: true));
        using var csharp = new EditorViewModel(new DocumentState(Path.Combine(Path.GetTempPath(), "Yes.cs"), ""), workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { }) { CodeAssist = engine };
        using var xaml = new EditorViewModel(new DocumentState(Path.Combine(Path.GetTempPath(), "No.xaml"), ""), workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { }) { CodeAssist = engine };
        using var plain = new EditorViewModel(new DocumentState(Path.Combine(Path.GetTempPath(), "None.cs"), ""), workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { });
        Assert.True(csharp.SuggestsLines);
        Assert.False(xaml.SuggestsLines);
        Assert.False(plain.SuggestsLines);
        Assert.Null(await xaml.SuggestLineAsync(0));
        await engine.ConfigureAsync(new CodeAssistOptions(true));
        Assert.False(csharp.SuggestsLines);
    });

    [Fact]
    public Task RoslynsListIsRankedAndARecommendationStillCommits() => OnDispatcherAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "WpfStudio-CodeAssistIntegration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string project = Path.Combine(directory, "Fixture.csproj");
            string file = Path.Combine(directory, "Fixture.cs");
            const string source = "class Fixture { void Run(System.Collections.Generic.List<string> names) {\n// Clear out the list\nnames. } }";
            await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            await File.WriteAllTextAsync(file, source);
            Assert.Equal(0, (await new BuildService().RunAsync(new BuildRequest(project, BuildOperation.Restore))).ExitCode);
            await using var workspace = new WorkspaceClient();
            await workspace.LoadAsync(new LoadWorkspaceRequest(project));
            using var engine = NewEngine();
            var state = new DocumentState(file, source);
            using var model = new EditorViewModel(state, workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { }) { CodeAssist = engine };
            int caret = source.IndexOf("names. ", StringComparison.Ordinal) + "names.".Length;

            var off = await model.CompleteAsync(caret);
            Assert.NotEmpty(off.Items);
            Assert.DoesNotContain(off.Items, item => item.Recommended);

            await engine.ConfigureAsync(new CodeAssistOptions(true));
            Assert.NotEmpty(await engine.RankMembersAsync(source, caret, ["Add", "Clear"]));
            var on = await model.CompleteAsync(caret);
            // Nothing the compiler offered is lost, and every id still refers to its own item.
            Assert.Equal(off.Items.Select(item => item.DisplayText).Order(), on.Items.Select(item => item.DisplayText).Order());
            Assert.Equal(CodeAssistEngine.MaxRecommendations, on.Items.Count(item => item.Recommended));
            Assert.All(on.Items.Take(CodeAssistEngine.MaxRecommendations), item => Assert.True(item.Recommended));
            var clear = on.Items[0];
            Assert.Equal("Clear", clear.DisplayText);
            TextEdit? edit = await model.CompletionEditAsync(clear, on.Version);
            Assert.NotNull(edit);
            Assert.Equal("Clear", edit.NewText);
            Assert.Equal(caret, edit.Start);
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    });

    private static Task OnDispatcherAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
