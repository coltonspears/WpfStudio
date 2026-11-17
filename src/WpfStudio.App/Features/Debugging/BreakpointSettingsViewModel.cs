using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Debugging;

namespace WpfStudio.App.Features.Debugging;

/// <summary>The part of the breakpoint settings that takes focus when they open.</summary>
public enum BreakpointSettingsFocus { Condition, HitCount, LogMessage }

/// <summary>A hit count comparison as the settings list it.</summary>
public sealed record HitConditionChoice(HitConditionKind Kind, string Text);

public enum TracepointPartKind { Text, Expression, Keyword }

/// <summary>A piece of a tracepoint message as the settings preview it: plain text, an <c>{expression}</c> or a <c>$KEYWORD</c>.</summary>
public sealed record TracepointPart(TracepointPartKind Kind, string Text);

/// <summary>Something the settings offer to insert into a tracepoint message.</summary>
public sealed record TracepointToken(string Label, string Text, string Description);

/// <summary>
/// One breakpoint's settings, in a form: when it breaks (a condition and a hit count) and whether it logs a message and
/// continues instead. The form edits a copy; the breakpoint, created when the line has none, changes only on Save.
/// </summary>
public sealed partial class BreakpointSettingsViewModel : ObservableObject
{
    // The keywords the debugger expands in a tracepoint message (DebuggerViewModel).
    private static readonly string[] Keywords = ["$FUNCTION", "$FILEPOS", "$HITCOUNT", "$TID"];
    private readonly Func<BreakpointSettingsViewModel, Task> _save;
    private bool _loading;

    public BreakpointSettingsViewModel(Func<BreakpointSettingsViewModel, Task> save) => _save = save;

    public IReadOnlyList<HitConditionChoice> HitKinds { get; } =
    [
        new(HitConditionKind.Equal, "is equal to"),
        new(HitConditionKind.AtLeast, "is at least"),
        new(HitConditionKind.Multiple, "is a multiple of")
    ];
    public IReadOnlyList<TracepointToken> Tokens { get; } =
    [
        new("{ expression }", "{}", "A C# expression, evaluated in the stopped frame. With text selected, wraps the selection"),
        new("$FUNCTION", "$FUNCTION", "The method that hit the breakpoint"),
        new("$FILEPOS", "$FILEPOS", "The file and line, such as Program.cs(12)"),
        new("$HITCOUNT", "$HITCOUNT", "How many times the breakpoint has been hit"),
        new("$TID", "$TID", "The ID of the thread that hit it")
    ];

    [ObservableProperty] public partial bool IsOpen { get; set; }
    /// <summary>The breakpoint's file and line; it is looked up again on Save, in case it was removed meanwhile.</summary>
    public string Path { get; private set; } = "";
    public int Line { get; private set; }
    [ObservableProperty] public partial string Location { get; private set; } = "";
    /// <summary>True when the line has no breakpoint yet, so saving adds one.</summary>
    [ObservableProperty] public partial bool IsNew { get; private set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))] public partial bool UseCondition { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))] public partial string Condition { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))] public partial bool UseHitCount { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))] public partial HitConditionChoice? HitKind { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))] public partial int HitTarget { get; set; } = 1;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))] public partial bool UseLogMessage { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary), nameof(MessagePreview))] public partial string LogMessage { get; set; } = "";
    [ObservableProperty] public partial string Error { get; set; } = "";

    /// <summary>Asks the view to focus a part of the form; the view clears it once done.</summary>
    [ObservableProperty] public partial bool FocusCondition { get; set; }
    [ObservableProperty] public partial bool FocusHitCount { get; set; }
    [ObservableProperty] public partial bool FocusLogMessage { get; set; }

    /// <summary>How the debugger reads the log message, piece by piece.</summary>
    public IReadOnlyList<TracepointPart> MessagePreview => Preview(LogMessage);

    /// <summary>What the breakpoint will do, as a sentence.</summary>
    public string Summary
    {
        get
        {
            var when = new List<string>();
            if (ConditionValue is { Length: > 0 } condition) when.Add($"when {condition} is true");
            if (HitConditionValue.Length > 0 && HitCondition.TryParse(HitConditionValue, out var hit, out _)) when.Add(hit.Describe());
            var action = LogMessageValue.Length > 0 ? "Logs the message to Debugger > Output and continues" : "Breaks";
            return when.Count == 0 ? action + " on every hit." : $"{action} {string.Join(" and ", when)}.";
        }
    }

    /// <summary>The settings as the breakpoint stores them; a part that is turned off is empty.</summary>
    public string ConditionValue => UseCondition ? Condition.Trim() : "";
    public string HitConditionValue => UseHitCount && HitKind is { } kind && HitTarget > 0 ? new HitCondition(kind.Kind, HitTarget).Format() : "";
    public string LogMessageValue => UseLogMessage ? LogMessage.Trim() : "";

    /// <summary>Fills the form from the breakpoint on <paramref name="line"/>, or with defaults for a new one, and shows it.</summary>
    public void Open(string path, int line, BreakpointViewModel? breakpoint, BreakpointSettingsFocus? focus = null)
    {
        _loading = true;
        try
        {
            Path = path; Line = line; IsNew = breakpoint is null; Error = "";
            Location = $"{System.IO.Path.GetFileName(path)}, line {line}";
            Condition = breakpoint?.Condition ?? ""; UseCondition = Condition.Length > 0;
            if (HitCondition.TryParse(breakpoint?.HitCondition, out var hit, out _) && hit.Kind != HitConditionKind.Always)
            { HitKind = HitKinds.First(k => k.Kind == hit.Kind); HitTarget = hit.Count; UseHitCount = true; }
            else
            {
                HitKind = HitKinds[0]; HitTarget = 1; UseHitCount = false;
                if (breakpoint?.HitCondition is { Length: > 0 } unreadable) Error = $"The hit count \"{unreadable}\" could not be read; saving replaces it.";
            }
            LogMessage = breakpoint?.LogMessage ?? ""; UseLogMessage = LogMessage.Length > 0;
            // Opened for one part, that part is turned on and focused. Opened for the whole breakpoint, the form starts at the top.
            switch (focus)
            {
                case BreakpointSettingsFocus.Condition: UseCondition = true; FocusCondition = true; break;
                case BreakpointSettingsFocus.HitCount: UseHitCount = true; FocusHitCount = true; break;
                case BreakpointSettingsFocus.LogMessage: UseLogMessage = true; FocusLogMessage = true; break;
                default: FocusCondition = true; break;
            }
        }
        finally { _loading = false; }
        OnPropertyChanged(nameof(Summary));
        IsOpen = true;
    }

    // Editing a part turns it on, so typing a condition is enough without ticking its box first.
    partial void OnConditionChanged(string value) { if (!_loading && value.Length > 0) UseCondition = true; }
    partial void OnHitKindChanged(HitConditionChoice? value) { if (!_loading && value is not null) UseHitCount = true; }
    partial void OnHitTargetChanged(int value) { if (!_loading) UseHitCount = true; }
    partial void OnLogMessageChanged(string value) { if (!_loading && value.Length > 0) UseLogMessage = true; }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!IsOpen) return;
        if (UseCondition && ConditionValue.Length == 0) { Error = "Enter the condition, or turn Condition off."; FocusCondition = true; return; }
        if (UseHitCount && HitTarget < 1) { Error = "The hit count starts at 1."; FocusHitCount = true; return; }
        if (UseLogMessage && LogMessageValue.Length == 0) { Error = "Enter the message to log, or turn logging off to break instead."; FocusLogMessage = true; return; }
        Error = "";
        IsOpen = false;
        await _save(this);
    }

    [RelayCommand] private void Cancel() => IsOpen = false;

    internal static IReadOnlyList<TracepointPart> Preview(string message)
    {
        var parts = new List<TracepointPart>();
        foreach (var segment in TracepointMessage.Parse(message))
        {
            if (segment.IsExpression) { parts.Add(new(TracepointPartKind.Expression, segment.Text)); continue; }
            var text = segment.Text;
            var start = 0;
            for (var i = text.IndexOf('$', StringComparison.Ordinal); i >= 0 && i < text.Length; i = text.IndexOf('$', i + 1))
            {
                var at = i;
                if (Keywords.FirstOrDefault(k => string.CompareOrdinal(text, at, k, 0, k.Length) == 0) is not { } keyword) continue;
                if (i > start) parts.Add(new(TracepointPartKind.Text, text[start..i]));
                parts.Add(new(TracepointPartKind.Keyword, keyword));
                start = i + keyword.Length;
                i = start - 1;
                if (start >= text.Length) break;
            }
            if (start < text.Length) parts.Add(new(TracepointPartKind.Text, text[start..]));
        }
        return parts;
    }
}
