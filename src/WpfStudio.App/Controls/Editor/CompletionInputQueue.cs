using System.Windows.Input;

namespace WpfStudio.App.Controls.Editor;

/// <summary>One keystroke or editing command, in the order it arrived.</summary>
internal sealed record QueuedInput(string? Text, RoutedCommand? Command, object? Parameter);

/// <summary>
/// Input held while an accepted completion's edit is computed: idle, then pending (input is queued), then replaying
/// (input passes through again, in order, around the edit), then idle. However the edit ends, its input is replayed.
/// </summary>
internal sealed class CompletionInputQueue<TTarget>(TimeSpan timeout)
{
    public sealed class Pending(TTarget target, TimeSpan timeout)
    {
        public TTarget Target { get; } = target;
        public CancellationTokenSource Cancellation { get; } = new(timeout);
        public List<QueuedInput> Input { get; } = [];
    }

    public Pending? Current { get; private set; }
    public bool IsPending => Current is not null;
    public bool IsReplaying { get; private set; }

    public bool IsCurrent(Pending pending) => ReferenceEquals(Current, pending);

    /// <summary>Starts holding input. <paramref name="punctuation"/> is the text that accepted the entry, so it replays first.</summary>
    public Pending Begin(TTarget target, string? punctuation)
    {
        var pending = new Pending(target, timeout);
        if (!string.IsNullOrEmpty(punctuation)) pending.Input.Add(new QueuedInput(punctuation, null, null));
        return Current = pending;
    }

    public bool Queue(string text)
    {
        if (IsReplaying || Current is not { } pending) return false;
        pending.Input.Add(new QueuedInput(text, null, null));
        return true;
    }

    /// <summary>Holds a routed command so it keeps its place among the text. Copy reads the document and has no order to keep.</summary>
    public bool Queue(ICommand command, object? parameter)
    {
        if (IsReplaying || Current is not { } pending || command is not RoutedCommand routed || routed == ApplicationCommands.Copy) return false;
        pending.Input.Add(new QueuedInput(null, routed, parameter));
        return true;
    }

    /// <summary>Ends the wait. While <paramref name="apply"/> feeds the input back through the editor it is not queued again.</summary>
    public void Replay(Action apply)
    {
        Current = null;
        IsReplaying = true;
        try { apply(); }
        finally { IsReplaying = false; }
    }
}
