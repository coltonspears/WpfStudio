using WpfStudio.App.Features.Debugging;
using WpfStudio.Contracts;

namespace WpfStudio.Shell.Tests;

/// <summary>Breakpoint editing from the editor caret and the debugger's list, through the shell's breakpoint settings form.</summary>
public sealed class BreakpointEditingTests
{
    [Fact]
    public async Task TheSettingsFormCreatesAndUpdatesTheCaretBreakpointAndItsMarkers()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("Program.cs", "class Program\n{\n    static void Main() { }\n}\n");
        await test.Shell.OpenDocumentAsync(path);
        var editor = test.Shell.ActiveDocument!;
        editor.State.CaretLine = 3;
        var settings = test.Shell.Breakpoints.Settings;

        test.Shell.Breakpoints.HitCountCommand.Execute(null);
        Assert.True(settings.IsOpen);
        Assert.True(settings.IsNew);
        Assert.Equal("Program.cs, line 3", settings.Location);
        Assert.True(settings.UseHitCount);
        Assert.True(settings.FocusHitCount);
        Assert.Empty(test.Shell.Debugger.Breakpoints);
        settings.HitKind = settings.HitKinds.Single(kind => kind.Kind == HitConditionKind.Multiple);
        settings.HitTarget = 5;
        Assert.Equal("Breaks every 5 hits.", settings.Summary);
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.False(settings.IsOpen);
        var breakpoint = Assert.Single(test.Shell.Debugger.Breakpoints);
        Assert.Equal((path, 3, "%5", "", ""), (breakpoint.Path, breakpoint.Line, breakpoint.HitCondition, breakpoint.Condition, breakpoint.LogMessage));
        Assert.Equal("Breakpoint at Program.cs, line 3: Breaks every 5 hits.", test.Shell.Status);
        Assert.Equal([3], editor.BreakpointLines);
        Assert.Equal("%5", Assert.Single(editor.BreakpointMarkers).HitCondition);

        // Reopened for the message, the form shows what the breakpoint already has and keeps it.
        test.Shell.Breakpoints.LogMessageCommand.Execute(null);
        Assert.False(settings.IsNew);
        Assert.Equal((true, HitConditionKind.Multiple, 5), (settings.UseHitCount, settings.HitKind!.Kind, settings.HitTarget));
        Assert.True(settings.UseLogMessage);
        settings.LogMessage = "value is {x} in $FUNCTION";
        Assert.Equal([(TracepointPartKind.Text, "value is "), (TracepointPartKind.Expression, "x"), (TracepointPartKind.Text, " in "), (TracepointPartKind.Keyword, "$FUNCTION")],
            settings.MessagePreview.Select(part => (part.Kind, part.Text)));
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.Same(breakpoint, Assert.Single(test.Shell.Debugger.Breakpoints));
        Assert.True(breakpoint.IsTracepoint);
        Assert.Equal(("%5", "value is {x} in $FUNCTION"), (breakpoint.HitCondition, breakpoint.LogMessage));
        Assert.Contains("Tracepoint at Program.cs, line 3", test.Shell.Status);

        // Typing a condition turns it on; a part turned off is cleared from the breakpoint.
        test.Shell.Breakpoints.EditSettingsCommand.Execute(null);
        Assert.False(settings.UseCondition);
        settings.Condition = "x > 3";
        Assert.True(settings.UseCondition);
        settings.UseHitCount = false;
        settings.UseLogMessage = false;
        Assert.Equal("Breaks when x > 3 is true.", settings.Summary);
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.Equal(("x > 3", "", ""), (breakpoint.Condition, breakpoint.HitCondition, breakpoint.LogMessage));
        Assert.False(breakpoint.IsTracepoint);

        await test.Shell.Breakpoints.DisableCommand.ExecuteAsync(null);
        Assert.False(breakpoint.Enabled);
        Assert.Empty(editor.BreakpointLines);
        Assert.False(Assert.Single(editor.BreakpointMarkers).Enabled);

        await test.Shell.Breakpoints.ToggleCommand.ExecuteAsync(null);
        Assert.Empty(test.Shell.Debugger.Breakpoints);
        Assert.Empty(editor.BreakpointMarkers);
    }

    [Fact]
    public async Task AnEmptyPartIsReportedAndCancellingAddsNoBreakpoint()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("Program.cs", "class Program { }\n");
        await test.Shell.OpenDocumentAsync(path);
        var settings = test.Shell.Breakpoints.Settings;

        test.Shell.Breakpoints.ConditionCommand.Execute(null);
        Assert.True(settings.UseCondition);
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Enter the condition, or turn Condition off.", settings.Error);
        Assert.True(settings.IsOpen);
        Assert.Empty(test.Shell.Debugger.Breakpoints);

        settings.UseCondition = false;
        settings.UseLogMessage = true;
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.StartsWith("Enter the message to log", settings.Error);

        settings.CancelCommand.Execute(null);
        Assert.False(settings.IsOpen);
        Assert.Empty(test.Shell.Debugger.Breakpoints);

        test.Shell.Breakpoints.ShowCommand.Execute(null);
        Assert.Equal(2, test.Shell.Debugger.SelectedTab);
    }

    [Fact]
    public async Task TheDebuggerListOpensTheSettingsOfItsSelectedBreakpoint()
    {
        await using var test = new ShellTestContext();
        string path = await test.CreateFileAsync("Program.cs", "class Program\n{\n}\n");
        await test.Shell.Debugger.ToggleBreakpointAsync(path, 2);
        var breakpoint = Assert.Single(test.Shell.Debugger.Breakpoints);
        breakpoint.HitCondition = ">=3";
        var settings = test.Shell.Breakpoints.Settings;

        test.Shell.Debugger.EditBreakpointCommand.Execute(null);
        Assert.False(settings.IsOpen);

        test.Shell.Debugger.SelectedBreakpoint = breakpoint;
        test.Shell.Debugger.EditBreakpointCommand.Execute(null);
        Assert.True(settings.IsOpen);
        Assert.Equal((false, "Program.cs, line 2", HitConditionKind.AtLeast, 3), (settings.IsNew, settings.Location, settings.HitKind!.Kind, settings.HitTarget));
        Assert.Equal("Breaks when hit count is 3 or more.", settings.Summary);
        Assert.Equal("", settings.Error);
        settings.CancelCommand.Execute(null);

        // A hit count typed into the list by an older version, and not readable, is named rather than dropped silently.
        breakpoint.HitCondition = "every other";
        test.Shell.Debugger.EditBreakpointCommand.Execute(null);
        Assert.False(settings.UseHitCount);
        Assert.Contains("\"every other\" could not be read", settings.Error);
    }
}
