using System.Windows.Media;
using WpfStudio.App.Controls;
using WpfStudio.App.Services;
using WpfStudio.Contracts;
using WpfStudio.Core;

namespace WpfStudio.Shell.Tests;

public sealed class EditorSettingsTests
{
    [Fact]
    public async Task EditorSettingsRoundTripThroughTheSettingsFileAndKeepValuesInRange()
    {
        var directory = Directory.CreateTempSubdirectory("WpfStudio-EditorSettings-").FullName;
        try
        {
            var preferences = new EditorPreferences { FontFamily = "JetBrains Mono", FontSize = 15, TabSize = 2, InsertSpaces = false, WordWrap = true, ShowColumnRuler = true, ColumnRulerPosition = 100, TrimTrailingWhitespace = true, AutoSave = true };
            var store = new SettingsStore(directory);
            await store.SaveAsync(new StudioSettings { Editor = preferences.ToSettings(), ShowQuickInfo = false });
            var loaded = await store.LoadAsync();
            Assert.False(loaded.ShowQuickInfo);
            var restored = new EditorPreferences();
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
        var preferences = new EditorPreferences();
        var listed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        preferences.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(EditorPreferences.FontFamilies)) listed.TrySetResult(); };
        await Task.Run(() => Assert.Empty(preferences.FontFamilies));
        await listed.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.NotEmpty(preferences.FontFamilies);
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
