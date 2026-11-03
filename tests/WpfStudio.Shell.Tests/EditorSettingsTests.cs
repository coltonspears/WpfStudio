using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using WpfStudio.App.Controls;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core;
using WpfStudio.Core.Documents;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

public sealed class EditorSettingsTests
{
    [Fact]
    public async Task EditorSettingsRoundTripThroughTheSettingsFileAndKeepValuesInRange()
    {
        var directory = Directory.CreateTempSubdirectory("WpfStudio-EditorSettings-").FullName;
        try
        {
            var preferences = new EditorPreferences(new FontCatalog()) { FontFamily = "JetBrains Mono", FontSize = 15, TabSize = 2, InsertSpaces = false, WordWrap = true, ShowColumnRuler = true, ColumnRulerPosition = 100, TrimTrailingWhitespace = true, AutoSave = true };
            var store = new SettingsStore(directory);
            await store.SaveAsync(new StudioSettings { Editor = preferences.ToSettings(), ShowQuickInfo = false });
            var loaded = await store.LoadAsync();
            Assert.False(loaded.ShowQuickInfo);
            var restored = new EditorPreferences(new FontCatalog());
            restored.Load(loaded.Editor);
            Assert.Equal(preferences.ToSettings(), restored.ToSettings());

            restored.FontSize = 400; restored.TabSize = 0; restored.FontFamily = " ";
            Assert.Equal(EditorPreferences.MaximumFontSize, restored.FontSize);
            Assert.Equal(1, restored.TabSize);
            Assert.Equal(EditorSettings.DefaultFontFamily, restored.FontFamily);
            restored.ResetCommand.Execute(null);
            Assert.Equal(new EditorSettings(), restored.ToSettings());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DiagnosticPenIsReusedUntilTheThemeRecoloursItsBrush()
    {
        var theme = new SolidColorBrush(Colors.Red);
        Pen? held = null;
        var first = EditorSurface.ThemedPen(theme, 1.5, ref held);
        Assert.True(first.IsFrozen);
        Assert.False(theme.IsFrozen);
        Assert.Same(first, EditorSurface.ThemedPen(theme, 1.5, ref held));
        theme.Color = Colors.Blue;
        var second = EditorSurface.ThemedPen(theme, 1.5, ref held);
        Assert.NotSame(first, second);
        Assert.Equal(Colors.Blue, ((SolidColorBrush)second.Brush).Color);
    }

    [Fact]
    public async Task FontListFillsInWhenAWorkerThreadReadsItFirst()
    {
        var fonts = new FontCatalog();
        var listed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fonts.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(FontCatalog.Families)) listed.TrySetResult(); };
        await Task.Run(() => Assert.Empty(fonts.Families));
        await listed.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.NotEmpty(fonts.Families);
    }

    [Fact]
    public Task AnEditorSurfaceFollowsItsViewModelsPreferencesWhileLoaded() => OnDispatcherAsync(async () =>
    {
        var preferences = new EditorPreferences(new FontCatalog());
        await using var workspace = new WorkspaceClient();
        using var model = new EditorViewModel(new DocumentState(Path.Combine(Path.GetTempPath(), "Preferences.cs"), "class C { }"), workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { }) { Preferences = preferences };
        var editor = new EditorSurface { ViewModel = model };
        editor.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        preferences.FontSize = 20; preferences.WordWrap = true;
        Assert.Equal(20, editor.FontSize);
        Assert.True(editor.WordWrap);
        editor.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        preferences.FontSize = 22;
        Assert.Equal(20, editor.FontSize);
    });

    [Fact]
    public async Task ShellsDoNotShareEditorPreferencesAndDocumentsGetTheirShells()
    {
        await using var first = new ShellTestContext();
        await using var second = new ShellTestContext();
        Assert.NotSame(first.Shell.Editor, second.Shell.Editor);
        await first.Shell.OpenDocumentAsync(await first.CreateFileAsync("Preferences.cs", "class C { }"));
        Assert.Same(first.Shell.Editor, first.Shell.ActiveDocument!.Preferences);
    }

    private static Task OnDispatcherAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.BeginInvoke(async () =>
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

    [Fact]
    public async Task AnAutomaticSaveCannotWriteEditsWhileTheSaveOrDiscardQuestionIsOpen()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("Draft.cs", "original");
        await test.Shell.OpenDocumentAsync(path);
        var editor = test.Shell.ActiveDocument!;
        editor.State.Content = "unwanted edit";
        test.Dialogs.WhileAsking = () => test.Shell.AutoSave.RunNowAsync();
        test.Dialogs.SaveDecisions.Enqueue(SaveDecision.Discard);
        Assert.True(await test.Shell.CloseDocumentAsync(editor));
        Assert.Equal("original", await File.ReadAllTextAsync(path));

        // Outside a close, the same timed save does write.
        await test.Shell.OpenDocumentAsync(path);
        test.Shell.ActiveDocument!.State.Content = "kept edit";
        await test.Shell.AutoSave.RunNowAsync();
        Assert.Equal("kept edit", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task SavingTidiesWhitespaceOnlyWhenAsked()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("Tidy.cs", "class C  \n{\n}");
        await test.Shell.OpenDocumentAsync(path);
        var editor = test.Shell.ActiveDocument!;
        editor.TidyForSave(trimTrailingWhitespace: false, ensureFinalNewline: false);
        Assert.Equal("class C  \n{\n}", editor.State.Content);
        editor.TidyForSave(trimTrailingWhitespace: true, ensureFinalNewline: true);
        Assert.Equal("class C\n{\n}\n", editor.State.Content);
        Assert.True(editor.State.IsDirty);
    }
}
