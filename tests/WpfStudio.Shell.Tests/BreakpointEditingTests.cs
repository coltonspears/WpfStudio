namespace WpfStudio.Shell.Tests;

/// <summary>Breakpoint editing from the editor caret, through the shell's debugger and dialogs.</summary>
public sealed class BreakpointEditingTests
{
    [Fact]
    public async Task HitCountAndTracepointPromptsCreateAndUpdateTheCaretBreakpointAndItsMarkers()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("Program.cs", "class Program\n{\n    static void Main() { }\n}\n");
        await test.Shell.OpenDocumentAsync(path);
        var editor = test.Shell.ActiveDocument!;
        editor.State.CaretLine = 3;

        test.Dialogs.Prompts.Enqueue("%5");
        await test.Shell.Breakpoints.HitCountCommand.ExecuteAsync(null);
        var breakpoint = Assert.Single(test.Shell.Debugger.Breakpoints);
        Assert.Equal((path, 3, "%5"), (breakpoint.Path, breakpoint.Line, breakpoint.HitCondition));
        Assert.StartsWith("Breakpoint ", test.Shell.Status);
        Assert.Equal([3], editor.BreakpointLines);
        Assert.Equal("%5", Assert.Single(editor.BreakpointMarkers).HitCondition);

        test.Dialogs.Prompts.Enqueue("value is {x}");
        await test.Shell.Breakpoints.LogMessageCommand.ExecuteAsync(null);
        Assert.Same(breakpoint, Assert.Single(test.Shell.Debugger.Breakpoints));
        Assert.True(breakpoint.IsTracepoint);
        Assert.Contains("Tracepoint set", test.Shell.Status);

        await test.Shell.Breakpoints.DisableCommand.ExecuteAsync(null);
        Assert.False(breakpoint.Enabled);
        Assert.Empty(editor.BreakpointLines);
        Assert.False(Assert.Single(editor.BreakpointMarkers).Enabled);

        await test.Shell.Breakpoints.ToggleCommand.ExecuteAsync(null);
        Assert.Empty(test.Shell.Debugger.Breakpoints);
        Assert.Empty(editor.BreakpointMarkers);
    }

    [Fact]
    public async Task AnInvalidHitCountIsReportedAndNoBreakpointIsAdded()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("Program.cs", "class Program { }\n");
        await test.Shell.OpenDocumentAsync(path);
        test.Dialogs.Prompts.Enqueue("not a number");
        await test.Shell.Breakpoints.HitCountCommand.ExecuteAsync(null);
        Assert.Empty(test.Shell.Debugger.Breakpoints);
        Assert.Contains(test.Dialogs.Errors, error => error.StartsWith("Breakpoint hit count"));

        test.Shell.Breakpoints.ShowCommand.Execute(null);
        Assert.Equal(2, test.Shell.Debugger.SelectedTab);
    }
}
